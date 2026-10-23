import Foundation

public enum StreamTarget: String, Codable, CaseIterable, Sendable {
    /// The display the toolbar is on.
    case followToolbar
    /// One chosen display.
    case specificDisplay
    /// Every display in one wide image.
    case allDisplays
}

/// A display as the streaming code sees it. `id` is stable across reconnects and reboots (the display UUID),
/// unlike `CGDirectDisplayID`.
public struct StreamDisplay: Equatable, Sendable {
    public var id: String
    /// Global point coordinates, top-left origin.
    public var bounds: PixelRect
    public var isPrimary: Bool
    /// Pixels per point.
    public var backingScale: Double

    public init(id: String, bounds: PixelRect, isPrimary: Bool, backingScale: Double) {
        self.id = id
        self.bounds = bounds
        self.isPrimary = isPrimary
        self.backingScale = backingScale
    }
}

/// Everything the remote feature stores in settings.
public struct RemoteSettings: Codable, Equatable, Sendable {
    public var enabled = false
    public var port = 8787
    /// BSD interface name the QR code advertises; `nil` picks the best one.
    public var adapterId: String?
    public var maxFps = 10
    public var jpegQuality = 80
    /// Longest edge of the streamed image in pixels. 0 = native (capped for the all-displays target).
    public var maxEdge = 2560
    public var maxClients = 8
    public var requireJoinApproval = false
    public var libraryEnabled = true

    public var streamTarget = StreamTarget.followToolbar
    public var streamDisplay: String?
    public var regionEnabled = false
    public var regionDisplay: String?
    public var regionX = 0.0
    public var regionY = 0.0
    public var regionW = 1.0
    public var regionH = 1.0

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case enabled, port, adapterId, maxFps, jpegQuality, maxEdge, maxClients
        case requireJoinApproval, libraryEnabled
        case streamTarget, streamDisplay, regionEnabled, regionDisplay, regionX, regionY, regionW, regionH
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = RemoteSettings()
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? d.enabled
        port = try c.decodeIfPresent(Int.self, forKey: .port) ?? d.port
        adapterId = try c.decodeIfPresent(String.self, forKey: .adapterId)
        maxFps = try c.decodeIfPresent(Int.self, forKey: .maxFps) ?? d.maxFps
        jpegQuality = try c.decodeIfPresent(Int.self, forKey: .jpegQuality) ?? d.jpegQuality
        maxEdge = try c.decodeIfPresent(Int.self, forKey: .maxEdge) ?? d.maxEdge
        maxClients = try c.decodeIfPresent(Int.self, forKey: .maxClients) ?? d.maxClients
        requireJoinApproval = try c.decodeIfPresent(Bool.self, forKey: .requireJoinApproval) ?? d.requireJoinApproval
        libraryEnabled = try c.decodeIfPresent(Bool.self, forKey: .libraryEnabled) ?? d.libraryEnabled
        streamTarget = (try? c.decodeIfPresent(StreamTarget.self, forKey: .streamTarget)) ?? d.streamTarget
        streamDisplay = try c.decodeIfPresent(String.self, forKey: .streamDisplay)
        regionEnabled = try c.decodeIfPresent(Bool.self, forKey: .regionEnabled) ?? d.regionEnabled
        regionDisplay = try c.decodeIfPresent(String.self, forKey: .regionDisplay)
        regionX = try c.decodeIfPresent(Double.self, forKey: .regionX) ?? d.regionX
        regionY = try c.decodeIfPresent(Double.self, forKey: .regionY) ?? d.regionY
        regionW = try c.decodeIfPresent(Double.self, forKey: .regionW) ?? d.regionW
        regionH = try c.decodeIfPresent(Double.self, forKey: .regionH) ?? d.regionH
        sanitize()
    }

    /// Brings every value into its supported range (a hand-edited settings file cannot break the server).
    public mutating func sanitize() {
        port = (1024 ... 65535).contains(port) ? port : 8787
        maxFps = min(max(maxFps, 1), 30)
        jpegQuality = min(max(jpegQuality, 30), 100)
        if maxEdge > 0 { maxEdge = min(max(maxEdge, 320), 7680) }
        maxClients = min(max(maxClients, 1), 16)
    }
}

public enum StreamTargetResolver {
    public static let minRegionNormSize = 0.08
    public static let minRegionPoints = 40.0

    /// The rectangle the iPad sees and draws on, in global points: the region if one is set, otherwise the
    /// target (see `targetRect`).
    public static func streamRect(
        settings: RemoteSettings,
        displays: [StreamDisplay],
        followDisplay: String?
    ) -> PixelRect {
        guard !displays.isEmpty else { return PixelRect(x: 0, y: 0, width: 0, height: 0) }
        if settings.regionEnabled {
            let display = find(displays, settings.regionDisplay) ?? primary(of: displays)
            return regionToPoints(settings, displayBounds: display.bounds)
        }
        return targetRect(settings: settings, displays: displays, followDisplay: followDisplay)
    }

    /// The whole target, ignoring any region: a display, or the union of every display.
    public static func targetRect(
        settings: RemoteSettings,
        displays: [StreamDisplay],
        followDisplay: String?
    ) -> PixelRect {
        guard !displays.isEmpty else { return PixelRect(x: 0, y: 0, width: 0, height: 0) }
        switch settings.streamTarget {
        case .allDisplays:
            return DesktopLayout.union(displays.map(\.bounds)) ?? primary(of: displays).bounds
        case .specificDisplay:
            return (find(displays, settings.streamDisplay) ?? find(displays, followDisplay) ?? primary(of: displays)).bounds
        case .followToolbar:
            return (find(displays, followDisplay) ?? primary(of: displays)).bounds
        }
    }

    /// The displays inside `stream` as 0-1 rectangles of it. Empty unless two or more are visible, since a
    /// single screen needs no "zoom to monitor" shortcut.
    public static func monitorRects(stream: PixelRect, displays: [StreamDisplay]) -> [RemoteMonitorRect] {
        guard stream.width > 0, stream.height > 0 else { return [] }
        let parts = displays
            .compactMap { display in display.bounds.intersection(stream).map { (display, $0) } }
            .sorted { $0.1.x < $1.1.x }
        guard parts.count >= 2 else { return [] }
        return parts.map { display, overlap in
            RemoteMonitorRect(
                x: (overlap.x - stream.x) / stream.width,
                y: (overlap.y - stream.y) / stream.height,
                w: overlap.width / stream.width,
                h: overlap.height / stream.height,
                primary: display.isPrimary
            )
        }
    }

    /// A value that changes whenever the streamed rectangle (position or size) changes, so clients reset
    /// their view. Deterministic, unlike `Hasher`.
    public static func epoch(of stream: PixelRect) -> Int {
        var hash: Int32 = 17
        for value in [stream.x, stream.y, stream.width, stream.height] {
            hash = hash &* 31 &+ Int32(truncatingIfNeeded: Int(value.rounded(.towardZero)))
        }
        return Int(hash & 0x7FFF_FFFF)
    }

    /// Stores a region picked on screen, clamped inside the display that holds most of it.
    public static func settingRegion(
        _ settings: RemoteSettings,
        to rect: PixelRect,
        displays: [StreamDisplay]
    ) -> RemoteSettings {
        guard !displays.isEmpty else { return settings }
        let display = displayHolding(rect, in: displays)
        let area = display.bounds
        let clamped = clampInside(rect, area: area)
        var result = settings
        result.regionEnabled = true
        result.regionDisplay = display.id
        result.regionX = (clamped.minX - area.minX) / area.width
        result.regionY = (clamped.minY - area.minY) / area.height
        result.regionW = clamped.width / area.width
        result.regionH = clamped.height / area.height
        return result
    }

    public static func clearingRegion(_ settings: RemoteSettings) -> RemoteSettings {
        var result = settings
        result.regionEnabled = false
        result.regionDisplay = nil
        result.regionX = 0
        result.regionY = 0
        result.regionW = 1
        result.regionH = 1
        return result
    }

    /// Region fractions applied to a display's rect, with minimum sizes enforced and kept inside it.
    public static func regionToPoints(_ settings: RemoteSettings, displayBounds: PixelRect) -> PixelRect {
        let nx = min(max(settings.regionX, 0), 1)
        let ny = min(max(settings.regionY, 0), 1)
        let nw = min(max(settings.regionW, minRegionNormSize), 1)
        let nh = min(max(settings.regionH, minRegionNormSize), 1)

        let left = displayBounds.minX + nx * displayBounds.width
        let top = displayBounds.minY + ny * displayBounds.height
        let width = min(nw * displayBounds.width, displayBounds.maxX - left)
        let height = min(nh * displayBounds.height, displayBounds.maxY - top)
        return PixelRect(x: left, y: top, width: max(minRegionPoints, width), height: max(minRegionPoints, height))
    }

    /// Moves and shrinks `rect` so it lies inside `area` (minimum size enforced).
    public static func clampInside(_ rect: PixelRect, area: PixelRect) -> PixelRect {
        var left = max(area.minX, rect.minX)
        var top = max(area.minY, rect.minY)
        let right = min(area.maxX, rect.maxX)
        let bottom = min(area.maxY, rect.maxY)
        let width = max(minRegionPoints, right - left)
        let height = max(minRegionPoints, bottom - top)
        if left + width > area.maxX { left = area.maxX - width }
        if top + height > area.maxY { top = area.maxY - height }
        return PixelRect(x: left, y: top, width: width, height: height)
    }

    /// The display with the largest overlap with `rect`, else the nearest one.
    public static func displayHolding(_ rect: PixelRect, in displays: [StreamDisplay]) -> StreamDisplay {
        let best = displays
            .compactMap { display in display.bounds.intersection(rect).map { (display, $0.width * $0.height) } }
            .max { $0.1 < $1.1 }
        if let best { return best.0 }
        let center = PixelPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2)
        return displays.min { distance(center, $0.bounds) < distance(center, $1.bounds) } ?? primary(of: displays)
    }

    /// The display under the centre of `rect` (used to follow the toolbar), or the nearest.
    public static func displayNearest(to rect: PixelRect, in displays: [StreamDisplay]) -> StreamDisplay? {
        guard !displays.isEmpty, rect.width > 0, rect.height > 0 else { return nil }
        let center = PixelPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2)
        return displays.first { $0.bounds.contains(center) }
            ?? displays.min { distance(center, $0.bounds) < distance(center, $1.bounds) }
    }

    public static func find(_ displays: [StreamDisplay], _ id: String?) -> StreamDisplay? {
        guard let id, !id.isEmpty else { return nil }
        return displays.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    public static func primary(of displays: [StreamDisplay]) -> StreamDisplay {
        displays.first(where: \.isPrimary) ?? displays[0]
    }

    private static func distance(_ point: PixelPoint, _ rect: PixelRect) -> Double {
        let dx = max(max(rect.minX - point.x, 0), point.x - rect.maxX)
        let dy = max(max(rect.minY - point.y, 0), point.y - rect.maxY)
        return dx * dx + dy * dy
    }
}

/// How the streamed rectangle is cut across displays and scaled into the frame that goes to the iPad.
public struct StreamPlan: Equatable, Sendable {
    public struct Piece: Equatable, Sendable {
        public var displayID: String
        /// The part of the display to capture, in that display's local points (top-left origin).
        public var source: PixelRect
        /// Where it lands in the output frame, in output pixels (top-left origin).
        public var destination: PixelRect

        public init(displayID: String, source: PixelRect, destination: PixelRect) {
            self.displayID = displayID
            self.source = source
            self.destination = destination
        }
    }

    public var rect: PixelRect
    public var outputWidth: Int
    public var outputHeight: Int
    /// Output pixels per point.
    public var pixelsPerPoint: Double
    public var pieces: [Piece]

    /// Gaps between displays show this colour.
    public static let gapColorHex = "#23252A"

    public static func make(rect: PixelRect, displays: [StreamDisplay], maxEdge: Int) -> StreamPlan? {
        guard !rect.isEmpty else { return nil }
        let touched = displays.filter { $0.bounds.intersection(rect) != nil }
        guard !touched.isEmpty else { return nil }

        let nativeScale = touched.map(\.backingScale).max().map { max($0, 1) } ?? 1
        let longEdgePoints = max(rect.width, rect.height)
        let ppp = min(nativeScale, Double(max(maxEdge, 1)) / longEdgePoints)

        let width = max(1, Int((rect.width * ppp).rounded()))
        let height = max(1, Int((rect.height * ppp).rounded()))
        let pieces = touched.compactMap { display -> Piece? in
            guard let overlap = display.bounds.intersection(rect) else { return nil }
            return Piece(
                displayID: display.id,
                source: PixelRect(
                    x: overlap.x - display.bounds.x,
                    y: overlap.y - display.bounds.y,
                    width: overlap.width,
                    height: overlap.height
                ),
                destination: PixelRect(
                    x: (overlap.x - rect.x) * ppp,
                    y: (overlap.y - rect.y) * ppp,
                    width: overlap.width * ppp,
                    height: overlap.height * ppp
                )
            )
        }
        return StreamPlan(rect: rect, outputWidth: width, outputHeight: height, pixelsPerPoint: ppp, pieces: pieces)
    }
}

public struct RemoteStreamPreset: Equatable, Sendable {
    public var maxEdge: Int
    public var fps: Int
    public var quality: Int

    public init(maxEdge: Int, fps: Int, quality: Int) {
        self.maxEdge = maxEdge
        self.fps = fps
        self.quality = quality
    }

    /// `nativeLongEdge` is the long edge in pixels of what is streamed at full resolution.
    public static func balanced(nativeLongEdge: Int) -> RemoteStreamPreset {
        RemoteStreamPreset(maxEdge: min(1920, nativeLongEdge), fps: 10, quality: 75)
    }

    public static func sharp(nativeLongEdge: Int) -> RemoteStreamPreset {
        RemoteStreamPreset(maxEdge: min(2560, nativeLongEdge), fps: 12, quality: 82)
    }

    public static func native(nativeLongEdge: Int) -> RemoteStreamPreset {
        RemoteStreamPreset(maxEdge: min(7680, nativeLongEdge), fps: 10, quality: 95)
    }

    public static let eco = RemoteStreamPreset(maxEdge: 1280, fps: 6, quality: 65)

    public func applying(to settings: RemoteSettings) -> RemoteSettings {
        var result = settings
        result.maxEdge = maxEdge
        result.maxFps = fps
        result.jpegQuality = quality
        result.sanitize()
        return result
    }
}

public struct RemoteStreamEstimate: Equatable, Sendable {
    public var streamWidth: Int
    public var streamHeight: Int
    public var nativeLongEdge: Int
    public var downscaleRatio: Double
    public var targetLabel: String
    public var guidance: String
    public var estimatedKbps: Double
}

public enum RemoteStreamEstimator {
    /// "Auto" (native) is capped at this long edge when the whole desktop is streamed, so a multi-display
    /// desktop does not make every frame several MB over Wi-Fi.
    public static let autoAllDisplaysMaxEdge = 3840

    /// Long edge in pixels of the stream rectangle at native density.
    public static func nativeLongEdge(stream: PixelRect, displays: [StreamDisplay]) -> Int {
        let scale = displays.filter { $0.bounds.intersection(stream) != nil }.map(\.backingScale).max() ?? 1
        return max(1, Int((max(stream.width, stream.height) * max(scale, 1)).rounded()))
    }

    public static func effectiveMaxEdge(settings: RemoteSettings, stream: PixelRect, displays: [StreamDisplay]) -> Int {
        guard settings.maxEdge > 0 else {
            let native = nativeLongEdge(stream: stream, displays: displays)
            return isWholeDesktop(settings) ? min(native, autoAllDisplaysMaxEdge) : native
        }
        return min(max(settings.maxEdge, 320), 7680)
    }

    public static func estimate(settings: RemoteSettings, stream: PixelRect, displays: [StreamDisplay]) -> RemoteStreamEstimate {
        let native = nativeLongEdge(stream: stream, displays: displays)
        let maxEdge = effectiveMaxEdge(settings: settings, stream: stream, displays: displays)
        let scale = min(1.0, Double(maxEdge) / Double(native))
        let aspect = stream.height > 0 ? stream.width / stream.height : 1
        let longEdge = max(1, Int((Double(native) * scale).rounded()))
        let (w, h) = aspect >= 1
            ? (longEdge, max(1, Int((Double(longEdge) / aspect).rounded())))
            : (max(1, Int((Double(longEdge) * aspect).rounded())), longEdge)
        return RemoteStreamEstimate(
            streamWidth: w,
            streamHeight: h,
            nativeLongEdge: native,
            downscaleRatio: scale,
            targetLabel: describe(settings, nativeLongEdge: native),
            guidance: guidance(settings: settings, downscale: scale, streamLongEdge: longEdge),
            estimatedKbps: kbps(width: w, height: h, fps: settings.maxFps, quality: settings.jpegQuality)
        )
    }

    private static func isWholeDesktop(_ settings: RemoteSettings) -> Bool {
        !settings.regionEnabled && settings.streamTarget == .allDisplays
    }

    private static func describe(_ settings: RemoteSettings, nativeLongEdge: Int) -> String {
        if settings.regionEnabled { return "Capture region" }
        switch settings.streamTarget {
        case .allDisplays: return "All displays"
        case .specificDisplay: return classify(nativeLongEdge)
        case .followToolbar: return classify(nativeLongEdge) + " (follows toolbar)"
        }
    }

    private static func classify(_ longEdge: Int) -> String {
        if longEdge >= 3800 { return "4K-class display" }
        if longEdge >= 2500 { return "1440p-class display" }
        if longEdge >= 1900 { return "1080p-class display" }
        return "Standard display"
    }

    private static func guidance(settings: RemoteSettings, downscale: Double, streamLongEdge: Int) -> String {
        let percent = Int((downscale * 100).rounded())
        if isWholeDesktop(settings), settings.maxEdge <= 0, downscale < 0.98 {
            return "All displays in one wide image (\(streamLongEdge)px wide, \(percent)% of native). "
                + "Use the display buttons on the iPad to zoom to one screen; raise the size for a sharper zoom."
        }
        if downscale >= 0.98 {
            return "Streaming at full resolution: best for pinch-zoom on remote clients."
        }
        if percent < 55 {
            return "Only \(percent)% of native resolution: remote zoom will look soft. Try the Sharp or Native preset."
        }
        if settings.maxFps <= 6 {
            return "Low frame rate saves Wi-Fi; motion on screen will look choppy."
        }
        if settings.jpegQuality < 65 {
            return "JPEG quality is low: text and fine lines may show compression artifacts."
        }
        return "Downscaling to \(percent)% of native (\(streamLongEdge)px long edge). Good balance for most Wi-Fi."
    }

    private static func kbps(width: Int, height: Int, fps: Int, quality: Int) -> Double {
        let bytesPerFrame = min(max(Double(width * height) * (Double(quality) / 100) * 0.12, 25_000), 1_200_000)
        return bytesPerFrame * Double(max(1, fps)) * 8 / 1000
    }
}

/// Maps "what ink revision does this frame contain" over time.
///
/// The iPad keeps its own vector copy of a stroke until a frame arrives whose revision covers it. Screen
/// frames lag what is on screen, so a frame is stamped with the revision that was current a little *before*
/// the frame was captured: claiming a stroke is in a frame that does not yet show it would make the iPad
/// drop its copy early and the stroke would flicker out.
public struct RevisionTimeline: Sendable {
    private var entries: [(time: Double, revision: Int64)] = []
    private let capacity: Int

    public init(capacity: Int = 256) {
        self.capacity = max(2, capacity)
    }

    /// Records that the ink revision became `revision` at `time` (seconds, monotonic).
    public mutating func record(revision: Int64, at time: Double) {
        if let last = entries.last, last.revision == revision { return }
        entries.append((time, revision))
        if entries.count > capacity { entries.removeFirst(entries.count - capacity) }
    }

    /// The newest revision recorded at or before `time - latency`; 0 if none.
    public func revision(forFrameAt time: Double, latency: Double) -> Int64 {
        let cutoff = time - latency
        return entries.last(where: { $0.time <= cutoff })?.revision ?? 0
    }
}
