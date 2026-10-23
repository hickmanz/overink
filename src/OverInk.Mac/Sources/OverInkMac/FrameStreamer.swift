import CoreImage
import CoreMedia
import Foundation
import OverInkCore
import ScreenCaptureKit

/// Captures the streamed rectangle with ScreenCaptureKit and sends it to the clients as JPEG frames.
///
/// One `SCStream` per display the rectangle touches; the pieces are composed into one image (gaps between
/// displays are filled with a dark colour). Frames only arrive when the screen changes, so a static desktop
/// costs nothing; a repeat of the last frame goes out every couple of seconds as a keep-alive and when a
/// client joins.
final class FrameStreamer: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    struct Configuration: Equatable, Sendable {
        var plan: StreamPlan
        var fps: Int
        var quality: Int
    }

    /// A client that cannot take more than this per frame gets a smaller picture instead.
    static let maxFrameBytes = 1_000_000
    private static let keepAliveInterval: TimeInterval = 2
    /// How long a frame takes to get from the overlay's drawing to the stream.
    private static let captureLatency = 0.08

    private let server: RemoteServer
    private let queue = DispatchQueue(label: "app.overink.mac.frame-streamer")
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    private let lock = NSLock()

    /// Called off the main thread when capturing fails (for example, no Screen Recording permission).
    var onIssue: (@Sendable (String?) -> Void)?

    // Mutated on `queue` only, except `timeline` (see `lock`).
    private var configuration: Configuration?
    private var generation = 0
    private var streams: [SCStream] = []
    private var buffers: [String: CVPixelBuffer] = [:]
    private var pieceByStream: [ObjectIdentifier: String] = [:]
    private var lastEncode = Date.distantPast
    private var encodePending = false
    private var lastFrame: Data?
    private var lastFrameHash = 0
    private var lastSent = Date.distantPast
    private var sequence: UInt32 = 0
    private var keepAlive: DispatchSourceTimer?
    private var timeline = RevisionTimeline()

    init(server: RemoteServer) {
        self.server = server
        super.init()
    }

    // MARK: Control

    /// Starts, restarts or stops capture. `nil` stops.
    func apply(_ configuration: Configuration?, displayIDs: [String: CGDirectDisplayID]) {
        queue.async { [self] in
            guard configuration != self.configuration else { return }
            self.configuration = configuration
            generation += 1
            let generation = generation
            stopStreams()
            guard let configuration else {
                keepAlive?.cancel()
                keepAlive = nil
                lastFrame = nil
                lastFrameHash = 0
                onIssue?(nil)
                return
            }
            startKeepAlive()
            Task {
                await self.startStreams(configuration, displayIDs: displayIDs, generation: generation)
            }
        }
    }

    /// Records the ink revision at this moment so frames can be stamped with what they contain.
    func recordRevision(_ revision: Int64) {
        let now = Self.hostSeconds()
        lock.withLock { timeline.record(revision: revision, at: now) }
    }

    /// Sends the newest frame again (a client joined or returned from editing a capture).
    func resend() {
        queue.async { [self] in
            guard let lastFrame else { return }
            server.offerFrame(lastFrame)
            lastSent = Date()
        }
    }

    // MARK: Streams

    private func stopStreams() {
        let old = streams
        streams = []
        buffers = [:]
        pieceByStream = [:]
        encodePending = false
        for stream in old {
            stream.stopCapture { _ in }
        }
    }

    private func startStreams(_ configuration: Configuration, displayIDs: [String: CGDirectDisplayID], generation: Int) async {
        guard CGPreflightScreenCaptureAccess() else {
            onIssue?("Screen Recording permission is needed to stream your screen. Allow OverInk in System Settings → Privacy & Security → Screen Recording.")
            return
        }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            var started: [(SCStream, String)] = []
            for piece in configuration.plan.pieces {
                guard let cgID = displayIDs[piece.displayID],
                      let display = content.displays.first(where: { $0.displayID == cgID }) else { continue }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let config = SCStreamConfiguration()
                config.sourceRect = CGRect(
                    x: piece.source.x, y: piece.source.y, width: piece.source.width, height: piece.source.height
                )
                config.width = max(2, Int(piece.destination.width.rounded()))
                config.height = max(2, Int(piece.destination.height.rounded()))
                config.pixelFormat = kCVPixelFormatType_32BGRA
                config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, configuration.fps)))
                config.queueDepth = 3
                config.showsCursor = true
                config.capturesAudio = false

                let stream = SCStream(filter: filter, configuration: config, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
                started.append((stream, piece.displayID))
            }
            guard !started.isEmpty else {
                NSLog(
                    "OverInk remote stream: windows %d apps %d preflight %d",
                    content.windows.count, content.applications.count, CGPreflightScreenCaptureAccess() ? 1 : 0
                )
                NSLog(
                    "OverInk remote stream: wanted %@ (known %@), shareable displays %@",
                    configuration.plan.pieces.map(\.displayID).description,
                    displayIDs.description,
                    content.displays.map(\.displayID).description
                )
                onIssue?("The selected display is not available.")
                return
            }

            let accepted: Bool = queue.sync {
                guard generation == self.generation else { return false }
                for (stream, pieceID) in started {
                    pieceByStream[ObjectIdentifier(stream)] = pieceID
                }
                streams = started.map(\.0)
                return true
            }
            guard accepted else { return }
            for (stream, _) in started {
                try await stream.startCapture()
            }
            // A newer configuration may have arrived while starting; it stopped these via `stopStreams`.
            onIssue?(nil)
        } catch {
            onIssue?("Streaming could not start: \(error.localizedDescription)")
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onIssue?("Streaming stopped: \(error.localizedDescription)")
    }

    // MARK: Frames (on `queue`)

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let pieceID = pieceByStream[ObjectIdentifier(stream)],
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        buffers[pieceID] = pixels
        scheduleEncode(frameTime: CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer)))
    }

    private func scheduleEncode(frameTime: Double) {
        guard let configuration else { return }
        let interval = 1 / Double(max(1, configuration.fps))
        let wait = interval - Date().timeIntervalSince(lastEncode)
        if wait <= 0 {
            encode(frameTime: frameTime)
        } else if !encodePending {
            // Too soon: encode the newest buffers once the interval is up.
            encodePending = true
            queue.asyncAfter(deadline: .now() + wait) { [self] in
                encodePending = false
                encode(frameTime: Self.hostSeconds())
            }
        }
    }

    private func encode(frameTime: Double) {
        guard let configuration, !buffers.isEmpty else { return }
        lastEncode = Date()
        let plan = configuration.plan

        var canvas = CIImage(color: CIColor(cgColor: Self.gapColor()))
            .cropped(to: CGRect(x: 0, y: 0, width: plan.outputWidth, height: plan.outputHeight))
        for piece in plan.pieces {
            guard let pixels = buffers[piece.displayID] else { continue }
            let image = CIImage(cvPixelBuffer: pixels)
            let destination = piece.destination
            // Core Image's origin is bottom-left; the plan's is top-left.
            let y = Double(plan.outputHeight) - destination.y - destination.height
            let scaleX = destination.width / image.extent.width
            let scaleY = destination.height / image.extent.height
            let placed = image
                .transformed(by: CGAffineTransform(scaleX: scaleX, y: scaleY))
                .transformed(by: CGAffineTransform(translationX: destination.x, y: y))
            canvas = placed.composited(over: canvas)
        }

        guard let jpeg = jpeg(canvas, quality: configuration.quality) else { return }
        let hash = jpeg.hashValue
        let revision = lock.withLock {
            timeline.revision(forFrameAt: frameTime, latency: Self.captureLatency)
        }

        // A frame that is identical to the last one is not worth sending (keep-alive covers idle screens).
        if hash == lastFrameHash, lastFrame != nil { return }
        lastFrameHash = hash

        sequence &+= 1
        let header = RemoteFrameHeader(
            sequence: sequence,
            inkRevision: revision,
            width: UInt16(clamping: plan.outputWidth),
            height: UInt16(clamping: plan.outputHeight)
        )
        let frame = RemoteFrame.encode(header: header, jpeg: jpeg)
        lastFrame = frame
        lastSent = Date()
        server.offerFrame(frame)
    }

    /// JPEG at `quality` (1-100), stepping the quality down if the frame is too big for a phone or tablet.
    private func jpeg(_ image: CIImage, quality: Int) -> Data? {
        var q = Double(min(max(quality, 1), 100)) / 100
        for _ in 0..<4 {
            let options: [CIImageRepresentationOption: Any] = [
                CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): q,
            ]
            guard let data = context.jpegRepresentation(of: image, colorSpace: colorSpace, options: options) else {
                return nil
            }
            if data.count <= Self.maxFrameBytes || q <= 0.3 { return data }
            q = max(0.3, q - 0.2)
        }
        return nil
    }

    private func startKeepAlive() {
        keepAlive?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.keepAliveInterval, repeating: Self.keepAliveInterval)
        timer.setEventHandler { [self] in
            guard let lastFrame, Date().timeIntervalSince(lastSent) >= Self.keepAliveInterval else { return }
            server.offerFrame(lastFrame)
            lastSent = Date()
        }
        timer.resume()
        keepAlive = timer
    }

    private static func gapColor() -> CGColor {
        let hex = StreamPlan.gapColorHex.dropFirst()
        let value = UInt32(hex, radix: 16) ?? 0x23252A
        return CGColor(
            srgbRed: CGFloat((value >> 16) & 0xFF) / 255,
            green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255,
            alpha: 1
        )
    }

    private static func hostSeconds() -> Double {
        CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock()))
    }
}
