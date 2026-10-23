import AppKit
import Foundation
import OverInkCore

/// A connected display, with both the system id (for AppKit/ScreenCaptureKit) and the stable id and
/// geometry the remote streaming logic works with.
struct LiveDisplay {
    let id: CGDirectDisplayID
    let stream: StreamDisplay
}

enum LiveDisplays {
    static func current() -> [LiveDisplay] {
        let mainID = CGMainDisplayID()
        return NSScreen.screens.compactMap { screen in
            guard let id = screen.overInkDisplayID else { return nil }
            return LiveDisplay(
                id: id,
                stream: StreamDisplay(
                    id: stableID(of: id),
                    bounds: PixelRect(CGDisplayBounds(id)),
                    isPrimary: id == mainID,
                    backingScale: Double(screen.backingScaleFactor)
                )
            )
        }
    }

    /// The display's UUID, which survives reconnecting it (the numeric id does not).
    static func stableID(of id: CGDirectDisplayID) -> String {
        guard let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue(),
              let text = CFUUIDCreateString(nil, uuid) as String? else {
            return String(id)
        }
        return text
    }
}

/// Runs the remote side of OverInk: accepts iPads over the LAN, authenticates them, draws their ink on the
/// overlay and keeps every participant in step.
@MainActor
final class RemoteHost {
    enum Status: Equatable {
        case stopped
        case running(port: Int)
        case failed(String)
    }

    /// Per-stroke limits so one client cannot make the Mac draw without bound.
    private static let maxLiveStrokesPerClient = 8
    private static let maxPointsPerStroke = 100_000

    private struct LiveOwner {
        let clientID: String
        let displayID: CGDirectDisplayID
        let strokeID: UUID
        var pointCount: Int
    }

    private(set) var status = Status.stopped {
        didSet { if status != oldValue { onStatusChange?() } }
    }

    /// Called when `status` or the connected clients change.
    var onStatusChange: (() -> Void)?
    var onClientsChange: (() -> Void)?

    /// Called when a client asks for a library capture. Returns the new item's id.
    var captureToLibrary: (@MainActor () async -> Result<String, Error>)?
    /// Called whenever the streamed rectangle or the demand for frames changes.
    var onStreamChanged: (() -> Void)?

    let server = RemoteServer()
    let pairing: PairingService

    /// Runs the server for this launch even when the setting is off (the `--remote` development flag).
    var forceEnabled = false

    private let settings: SettingsModel
    private let session: AnnotationSession
    private let overlay: OverlayController
    private let toolbarFrame: () -> NSRect?

    private lazy var streamer = FrameStreamer(server: server)
    private var frameDemand = 0

    /// Why the picture is not streaming (permission missing, display gone), if it is not.
    private(set) var streamIssue: String? {
        didSet { if streamIssue != oldValue { onStatusChange?() } }
    }

    private var inbox: (stream: AsyncStream<(String, RemoteClientMessage)>, continuation: AsyncStream<(String, RemoteClientMessage)>.Continuation)
    private var consumer: Task<Void, Never>?
    private var live: [String: LiveOwner] = [:]
    private var followDisplayID: String?
    private var lastStreamRect: PixelRect?
    private var lastBroadcastState: RemoteState?
    private var stateBroadcastPending = false
    private var observing = false

    init(
        settings: SettingsModel,
        session: AnnotationSession,
        overlay: OverlayController,
        pairing: PairingService,
        toolbarFrame: @escaping () -> NSRect?
    ) {
        self.settings = settings
        self.session = session
        self.overlay = overlay
        self.pairing = pairing
        self.toolbarFrame = toolbarFrame
        inbox = AsyncStream<(String, RemoteClientMessage)>.makeStream()
    }

    var isRunning: Bool { if case .running = status { return true } else { return false } }
    var clientCount: Int { server.clientCount }
    var roster: [RemoteRosterEntry] { server.roster() }

    // MARK: Lifecycle

    /// Starts or stops the server to match the settings.
    func apply() {
        server.maxClients = settings.value.remote.maxClients
        let wanted = settings.value.remote.enabled || forceEnabled
        if wanted, !isRunning || runningPort != settings.value.remote.port {
            if isRunning { stop() }
            start()
        } else if !wanted, isRunning || status != .stopped {
            stop()
        }
        refreshStream()
    }

    private var runningPort: Int? {
        if case .running(let port) = status { return port }
        return nil
    }

    private func start() {
        observeIfNeeded()
        configureServer()
        let port = settings.value.remote.port
        server.start(port: port) { [weak self] result in
            Task { @MainActor in
                guard let self else { return }
                switch result {
                case .success(let bound):
                    self.status = .running(port: Int(bound))
                case .failure(let error):
                    self.status = .failed(Self.describe(error, port: port))
                }
            }
        }
        consumer = Task { [weak self, stream = inbox.stream] in
            for await (id, message) in stream {
                guard let self else { return }
                self.handle(message, from: id)
            }
        }
    }

    func stop() {
        consumer?.cancel()
        consumer = nil
        for key in Array(live.keys) { cancelLive(key, broadcast: false) }
        streamer.apply(nil, displayIDs: [:])
        frameDemand = 0
        server.stop()
        status = .stopped
        lastBroadcastState = nil
        onClientsChange?()
    }

    private static func describe(_ error: Error, port: Int) -> String {
        let text = error.localizedDescription
        if text.localizedCaseInsensitiveContains("address already in use") {
            return "Port \(port) is already in use. Choose another port in Settings."
        }
        return text
    }

    /// The shared web client. A packaged app carries it in `Contents/Resources/RemoteWeb`; `swift run` and
    /// the development bundle fall back to the SwiftPM resource bundle next to the build.
    private static func webClientRoot() -> URL? {
        if let packaged = Bundle.main.resourceURL?.appendingPathComponent("RemoteWeb"),
           FileManager.default.fileExists(atPath: packaged.appendingPathComponent("index.html").path) {
            return packaged
        }
        return Bundle.module.url(forResource: "RemoteWeb", withExtension: nil)
    }

    private func configureServer() {
        server.staticRoot = Self.webClientRoot()

        server.onHello = { [weak self] hello, peer, connectionID in
            guard let self else {
                return .reject(RemoteError(code: RemoteErrorCode.denied.rawValue, message: "OverInk is shutting down."))
            }
            return await self.admit(hello, peer: peer, connectionID: connectionID)
        }
        let continuation = inbox.continuation
        server.onMessage = { id, message in
            continuation.yield((id, message))
        }
        server.onRosterChanged = { [weak self] in
            Task { @MainActor in self?.rosterChanged() }
        }
        server.onClientRemoved = { [weak self] id in
            Task { @MainActor in self?.clientRemoved(id) }
        }
        server.onStreamDemandChanged = { [weak self] count in
            Task { @MainActor in
                guard let self else { return }
                let joined = count > self.frameDemand
                self.frameDemand = count
                self.updateStreamer()
                if joined { self.streamer.resend() }
            }
        }
        streamer.onIssue = { [weak self] message in
            if let message { NSLog("OverInk remote stream: %@", message) }
            Task { @MainActor in self?.streamIssue = message }
        }
    }

    private func observeIfNeeded() {
        guard !observing else { return }
        observing = true
        session.inkDiffHandler = { [weak self] diff in self?.broadcast(diff) }
        session.observeChanges { [weak self] in
            guard let self else { return }
            self.streamer.recordRevision(Int64(self.session.revision))
        }
        streamer.recordRevision(Int64(session.revision))
        session.observeSettings { [weak self] in self?.scheduleStateBroadcast() }
        settings.observe { [weak self] in
            guard let self else { return }
            self.server.maxClients = self.settings.value.remote.maxClients
            self.refreshStream()
            self.updateStreamer()
            self.scheduleStateBroadcast()
        }
        overlay.addModeObserver { [weak self] _ in self?.scheduleStateBroadcast() }
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.refreshStream()
                self?.scheduleStateBroadcast()
            }
        }
    }

    // MARK: Geometry

    var displays: [LiveDisplay] { LiveDisplays.current() }

    /// The rectangle clients see and draw on, in global points.
    func streamRect() -> PixelRect {
        let live = displays
        updateFollowDisplay(live)
        return StreamTargetResolver.streamRect(
            settings: settings.value.remote,
            displays: live.map(\.stream),
            followDisplay: followDisplayID
        )
    }

    private func updateFollowDisplay(_ live: [LiveDisplay]) {
        guard let frame = toolbarFrame(), frame.width > 0 else { return }
        let top = CGDisplayBounds(CGMainDisplayID()).height - frame.maxY
        let rect = PixelRect(x: frame.minX, y: top, width: frame.width, height: frame.height)
        if let display = StreamTargetResolver.displayNearest(to: rect, in: live.map(\.stream)) {
            followDisplayID = display.id
        }
    }

    private func mapper() -> RemoteInkMapper {
        let live = displays
        updateFollowDisplay(live)
        let rect = StreamTargetResolver.streamRect(
            settings: settings.value.remote,
            displays: live.map(\.stream),
            followDisplay: followDisplayID
        )
        return RemoteInkMapper(
            stream: rect,
            displays: live.map { CaptureDisplay(id: $0.id, bounds: $0.stream.bounds) }
        )
    }

    /// Notices when the streamed rectangle moved (toolbar dragged to another display, region changed,
    /// monitors rearranged) and tells the streamer and the clients.
    func refreshStream() {
        guard isRunning else { return }
        let rect = streamRect()
        guard rect != lastStreamRect else { return }
        lastStreamRect = rect
        updateStreamer()
        onStreamChanged?()
        scheduleStateBroadcast()
    }

    /// Captures only while somebody is watching, and re-plans when the rectangle or quality settings change.
    private func updateStreamer() {
        guard isRunning, frameDemand > 0 else {
            streamer.apply(nil, displayIDs: [:])
            return
        }
        let live = displays
        let remote = settings.value.remote
        let maxEdge = remote.maxEdge > 0
            ? remote.maxEdge
            : (remote.streamTarget == .allDisplays ? RemoteStreamEstimator.autoAllDisplaysMaxEdge : 7680)
        guard let plan = StreamPlan.make(rect: streamRect(), displays: live.map(\.stream), maxEdge: maxEdge) else {
            streamer.apply(nil, displayIDs: [:])
            return
        }
        streamer.apply(
            FrameStreamer.Configuration(plan: plan, fps: remote.maxFps, quality: remote.jpegQuality),
            displayIDs: Dictionary(live.map { ($0.stream.id, $0.id) }, uniquingKeysWith: { first, _ in first })
        )
    }

    // MARK: State

    private func buildState() -> RemoteState {
        let live = displays
        updateFollowDisplay(live)
        let streams = live.map(\.stream)
        let rect = StreamTargetResolver.streamRect(
            settings: settings.value.remote, displays: streams, followDisplay: followDisplayID
        )
        return RemoteState(
            tool: Self.wireName(of: session.tool),
            color: session.colorHex,
            size: session.penWidth,
            mode: overlay.mode == .draw ? "Draw" : "PassThrough",
            screenW: rect.width,
            screenH: rect.height,
            regionActive: settings.value.remote.regionEnabled,
            pressureSensitive: session.pressureSensitive,
            screenVeil: session.veilOpacity,
            customColors: settings.value.customColors,
            streamEpoch: StreamTargetResolver.epoch(of: rect),
            monitors: StreamTargetResolver.monitorRects(stream: rect, displays: streams)
        )
    }

    static func wireName(of tool: SketchTool) -> String {
        tool.rawValue.prefix(1).uppercased() + tool.rawValue.dropFirst()
    }

    private func scheduleStateBroadcast() {
        guard isRunning, !stateBroadcastPending else { return }
        stateBroadcastPending = true
        Task { @MainActor [weak self] in
            guard let self else { return }
            self.stateBroadcastPending = false
            guard self.isRunning else { return }
            let state = self.buildState()
            guard state != self.lastBroadcastState else { return }
            self.lastBroadcastState = state
            self.sendAll("state", state)
        }
    }

    // MARK: Admission

    private func admit(_ hello: RemoteHello, peer: String, connectionID: String) async -> RemoteHelloResult {
        guard hello.v == RemoteProtocol.version else {
            return .reject(RemoteError(code: RemoteErrorCode.versionMismatch.rawValue, message: "This iPad page is out of date. Reload it."))
        }

        let name = Self.cleanName(hello.deviceName)
        var issuedKey: String?
        var libraryDeviceID: String?
        var needsApproval = false

        if hello.pair != nil {
            guard pairing.tryConsumePairToken(hello.pair) else {
                return .reject(RemoteError(code: RemoteErrorCode.badToken.rawValue, message: "That pairing code has expired. Scan the QR code again."))
            }
            let issued = pairing.issueDeviceKey(deviceName: name, canUseLibrary: true)
            issuedKey = issued.key
            libraryDeviceID = issued.device.id
        } else if let device = pairing.findByDeviceKey(hello.deviceKey) {
            libraryDeviceID = device.canUseLibrary ? device.id : nil
        } else if pairing.validateJoinToken(hello.join) {
            needsApproval = settings.value.remote.requireJoinApproval
        } else if RemoteNetwork.isLoopback(peer) {
            // The Mac's own browser (testing, or the same machine) is trusted.
            let issued = pairing.issueDeviceKey(deviceName: name, canUseLibrary: true)
            issuedKey = issued.key
            libraryDeviceID = issued.device.id
        } else {
            return .reject(RemoteError(code: RemoteErrorCode.badToken.rawValue, message: "This device is not paired. Scan the QR code on the Mac."))
        }

        if needsApproval, !(await askApproval(for: name)) {
            return .reject(RemoteError(code: RemoteErrorCode.denied.rawValue, message: "The host declined the request."))
        }

        let libToken: String? = {
            guard settings.value.remote.libraryEnabled, let libraryDeviceID else { return nil }
            return libraryTokens.issue(deviceID: libraryDeviceID, clientID: connectionID)
        }()

        let rect = streamRect()
        let state = buildState()
        let welcome = RemoteWelcome(
            clientId: connectionID,
            deviceKey: issuedKey,
            screenW: rect.width,
            screenH: rect.height,
            state: state,
            inkRevision: Int64(session.revision),
            libToken: libToken
        )
        let color = ParticipantColors.pick(used: server.usedColors())
        return .accept(RemoteAdmission(welcome: welcome, name: name, color: color, libToken: libToken))
    }

    let libraryTokens = LibraryTokenRegistry()

    private static func cleanName(_ raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "Guest" : String(trimmed.prefix(64))
    }

    private func askApproval(for name: String) async -> Bool {
        let alert = NSAlert()
        alert.messageText = "\(name) wants to join"
        alert.informativeText = "They will be able to draw on your screen until you disconnect them."
        alert.addButton(withTitle: "Allow")
        alert.addButton(withTitle: "Deny")
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    // MARK: Roster

    private func rosterChanged() {
        sendAll("roster", RemoteRoster(participants: server.roster()))
        onClientsChange?()
        onStatusChange?()
    }

    private func clientRemoved(_ id: String) {
        libraryTokens.revoke(clientID: id)
        for (key, owner) in live where owner.clientID == id { cancelLive(key, broadcast: true) }
    }

    func disconnect(_ clientID: String) {
        server.kick(clientID)
    }

    // MARK: Sending helpers

    private func sendAll<T: Encodable>(_ type: String, _ payload: T, except id: String? = nil) {
        guard let data = try? RemoteJSON.wrap(type, payload: payload) else { return }
        server.broadcast(data, except: id)
    }

    private func send<T: Encodable>(_ type: String, _ payload: T, to id: String) {
        guard let data = try? RemoteJSON.wrap(type, payload: payload) else { return }
        server.send(data, to: id)
    }

    /// Sends a message that carries no payload to every client.
    func broadcastBare(_ type: String) {
        server.broadcast(RemoteJSON.wrap(type))
    }

    private func broadcast(_ diff: AnnotationSession.InkDiff) {
        guard isRunning else { return }
        if diff.cleared {
            broadcastBare("cleared")
            return
        }
        if !diff.removed.isEmpty {
            sendAll("strokesRemoved", RemoteStrokesRemoved(ids: diff.removed.map(RemoteID.wire)))
        }
        if !diff.added.isEmpty {
            let mapper = mapper()
            let snapshots = diff.added.compactMap { placed -> RemoteStrokeSnapshot? in
                guard let display = mapper.displays.first(where: { $0.id == placed.displayID }) else { return nil }
                return mapper.snapshot(of: placed.stroke, on: display)
            }
            if !snapshots.isEmpty { sendAll("strokesAdded", RemoteStrokesAdded(strokes: snapshots)) }
        }
    }

    // MARK: Messages from clients

    private func handle(_ message: RemoteClientMessage, from clientID: String) {
        switch message {
        case .strokeBegin(let begin): beginStroke(begin, from: clientID)
        case .strokePoints(let points): extendStroke(points, from: clientID)
        case .strokeEnd(let id): endStroke(id, from: clientID)
        case .strokeCancel(let id):
            let key = Self.key(clientID, id)
            if live[key] != nil {
                cancelLive(key, broadcast: true, wireID: id)
            }
        case .shapePreview(let shape): relayShapePreview(shape, from: clientID)
        case .shapeCommit(let shape): commitShape(shape, from: clientID)
        case .erase(let erase): eraseInk(erase, from: clientID)

        case .setTool(let name):
            if let tool = SketchTool(rawValue: name.lowercased()) { session.tool = tool }
        case .setColor(let hex):
            if let color = ColorHex.normalize(hex) { session.colorHex = color }
        case .saveColor(let hex):
            if let color = ColorHex.normalize(hex) {
                settings.update { value in
                    var palette = CustomColorPalette(normalizing: value.customColors)
                    if palette.add(color) { value.customColors = palette.colors }
                }
            }
        case .removeColor(let hex):
            if let color = ColorHex.normalize(hex) {
                settings.update { value in
                    var palette = CustomColorPalette(normalizing: value.customColors)
                    if palette.remove(color) { value.customColors = palette.colors }
                }
            }
        case .setSize(let size):
            if size.isFinite { session.penWidth = size }
        case .setPressure(let enabled):
            session.pressureSensitive = enabled
        case .setVeil(let opacity):
            if opacity.isFinite { session.veilOpacity = min(max(opacity, 0), 0.6) }

        case .undo: session.undo(author: clientID)
        case .redo: session.redo(author: clientID)
        case .clear: session.clear(author: clientID)

        case .ping(let ms):
            send("pong", RemotePong(ms: ms), to: clientID)
        case .hover(let hover):
            let relayed = RemoteHover(clientId: clientID, x: hover.x, y: hover.y)
            sendAll("hover", relayed, except: clientID)
        case .viewMode(let doc):
            server.setDocMode(doc, for: clientID)
            if !doc { streamer.resend() }
        case .capture:
            captureForClient(clientID)
        }
        // Acknowledgements for strokes go out from the handlers; everything else is covered by the
        // state broadcast that the session observers schedule.
    }

    private static func key(_ clientID: String, _ wireID: String) -> String {
        clientID + "/" + RemoteID.normalize(wireID)
    }

    // MARK: Strokes

    private func beginStroke(_ begin: RemoteStrokeBegin, from clientID: String) {
        guard begin.x.isFinite, begin.y.isFinite, !begin.id.isEmpty, begin.id.count <= 64 else { return }
        let key = Self.key(clientID, begin.id)
        guard live[key] == nil else { return }
        guard live.values.filter({ $0.clientID == clientID }).count < Self.maxLiveStrokesPerClient else { return }

        let mapper = mapper()
        guard let display = mapper.display(x: begin.x, y: begin.y) else { return }
        let local = mapper.localPoint(x: begin.x, y: begin.y, on: display)
        let color = ColorHex.normalize(begin.color) ?? session.colorHex
        let width = begin.size > 0 ? min(max(begin.size, 0.5), 200) : session.penWidth
        let strokeID = strokeIdentity(for: begin.id)

        let stroke = InkStroke(
            id: strokeID,
            colorHex: color,
            width: width,
            points: [InkPoint(x: local.x, y: local.y, pressure: begin.p)],
            ignoresPressure: !session.pressureSensitive
        )
        live[key] = LiveOwner(clientID: clientID, displayID: display.id, strokeID: strokeID, pointCount: 1)
        session.beginLive(id: key, displayID: display.id, stroke: stroke, author: clientID)

        sendAll(
            "peerStrokeBegin",
            RemotePeerStrokeBegin(
                author: clientID, id: begin.id, color: color, size: width,
                x: begin.x, y: begin.y, p: begin.p
            ),
            except: clientID
        )
    }

    private func extendStroke(_ message: RemoteStrokePoints, from clientID: String) {
        let key = Self.key(clientID, message.id)
        guard var owner = live[key], message.pts.count >= 3 else { return }
        let mapper = mapper()
        guard let display = mapper.displays.first(where: { $0.id == owner.displayID }) else { return }

        var points: [InkPoint] = []
        var index = 0
        while index + 2 < message.pts.count, owner.pointCount + points.count < Self.maxPointsPerStroke {
            let x = Double(message.pts[index])
            let y = Double(message.pts[index + 1])
            if x.isFinite, y.isFinite {
                let local = mapper.localPoint(x: x, y: y, on: display)
                points.append(InkPoint(x: local.x, y: local.y, pressure: message.pts[index + 2]))
            }
            index += 3
        }
        guard !points.isEmpty else { return }
        owner.pointCount += points.count
        live[key] = owner
        session.extendLive(id: key, with: points)
        sendAll("peerStrokePoints", RemotePeerStrokePoints(author: clientID, id: message.id, pts: message.pts), except: clientID)
    }

    private func endStroke(_ wireID: String, from clientID: String) {
        let key = Self.key(clientID, wireID)
        guard let owner = live.removeValue(forKey: key), var finished = session.endLive(id: key) else { return }
        StrokeSmoother.apply(to: &finished.stroke)
        session.append(finished.stroke, on: owner.displayID, author: clientID)
        send("ack", RemoteAck(id: wireID, inkRevision: Int64(session.revision)), to: clientID)
        sendAll("peerStrokeEnd", RemotePeerStrokeID(author: clientID, id: wireID), except: clientID)
    }

    private func cancelLive(_ key: String, broadcast: Bool, wireID: String? = nil) {
        guard let owner = live.removeValue(forKey: key) else { return }
        session.endLive(id: key)
        guard broadcast, isRunning else { return }
        let id = wireID ?? String(key.drop { $0 != "/" }.dropFirst())
        sendAll("peerStrokeCancel", RemotePeerStrokeID(author: owner.clientID, id: id), except: owner.clientID)
    }

    /// The id a stroke is stored under: the client's own GUID when it is unused, so `strokesRemoved`
    /// refers to the same string the client knows.
    private func strokeIdentity(for wireID: String) -> UUID {
        if let uuid = RemoteID.uuid(wireID),
           !session.strokes.contains(where: { $0.id == uuid }),
           !live.values.contains(where: { $0.strokeID == uuid }) {
            return uuid
        }
        return UUID()
    }

    // MARK: Shapes

    private func relayShapePreview(_ shape: RemoteShape, from clientID: String) {
        guard shape.tool.lowercased() != "pen", SketchTool(rawValue: shape.tool.lowercased()) != nil else { return }
        let color = ColorHex.normalize(shape.color) ?? session.colorHex
        sendAll(
            "peerShapePreview",
            RemotePeerShape(
                author: clientID, id: shape.id, tool: shape.tool,
                x1: shape.x1, y1: shape.y1, x2: shape.x2, y2: shape.y2,
                color: color, size: min(max(shape.size, 0.5), 200)
            ),
            except: clientID
        )
    }

    private func commitShape(_ shape: RemoteShape, from clientID: String) {
        guard let tool = SketchTool(rawValue: shape.tool.lowercased()),
              [.line, .arrow, .rectangle, .ellipse].contains(tool),
              [shape.x1, shape.y1, shape.x2, shape.y2].allSatisfy(\.isFinite) else { return }

        let mapper = mapper()
        guard let display = mapper.display(x: shape.x1, y: shape.y1) else { return }
        let start = mapper.localPoint(x: shape.x1, y: shape.y1, on: display)
        let end = mapper.localPoint(x: shape.x2, y: shape.y2, on: display)
        let points = StrokeGeometry.shapePoints(
            tool: tool,
            from: InkPoint(x: start.x, y: start.y),
            to: InkPoint(x: end.x, y: end.y)
        )
        let width = shape.size > 0 ? min(max(shape.size, 0.5), 200) : session.penWidth
        let stroke = InkStroke(
            id: strokeIdentity(for: shape.id),
            colorHex: ColorHex.normalize(shape.color) ?? session.colorHex,
            width: width,
            points: points,
            ignoresPressure: true
        )
        session.append(stroke, on: display.id, author: clientID)
        send("ack", RemoteAck(id: shape.id, inkRevision: Int64(session.revision)), to: clientID)
        sendAll(
            "peerShapeCommit",
            RemotePeerShape(
                author: clientID, id: shape.id, tool: shape.tool,
                x1: shape.x1, y1: shape.y1, x2: shape.x2, y2: shape.y2,
                color: stroke.colorHex, size: width
            ),
            except: clientID
        )
    }

    // MARK: Erasing

    private func eraseInk(_ erase: RemoteErase, from clientID: String) {
        guard erase.size.isFinite, erase.size > 0, erase.pts.count >= 2 else { return }
        let mapper = mapper()
        var normalized: [(Double, Double)] = []
        var index = 0
        while index + 1 < erase.pts.count, normalized.count < 4_000 {
            let x = Double(erase.pts[index])
            let y = Double(erase.pts[index + 1])
            if x.isFinite, y.isFinite { normalized.append((x, y)) }
            index += 2
        }
        guard !normalized.isEmpty else { return }

        let diameter = min(erase.size, 400)
        for display in mapper.displays {
            let candidates = session.strokes(for: display.id)
            guard !candidates.isEmpty else { continue }
            let path = normalized.map { mapper.localPoint(x: $0.0, y: $0.1, on: display) }
            let result = StrokeEraser.erase(
                strokes: candidates, path: path, diameter: diameter, whole: erase.whole ?? false
            )
            // Everyone, including the sender, hears the authoritative result through the ink diff.
            session.apply(result, on: display.id, author: clientID)
        }
    }

    // MARK: Library capture

    private func captureForClient(_ clientID: String) {
        guard settings.value.remote.libraryEnabled, let captureToLibrary else {
            send("captureFailed", RemoteCaptureFailed(message: "Library capture is turned off on the Mac."), to: clientID)
            return
        }
        Task { @MainActor [weak self] in
            let result = await captureToLibrary()
            guard let self else { return }
            switch result {
            case .success(let id):
                self.send("captured", RemoteCaptured(id: id), to: clientID)
            case .failure(let error):
                self.send("captureFailed", RemoteCaptureFailed(message: error.localizedDescription), to: clientID)
            }
        }
    }
}
