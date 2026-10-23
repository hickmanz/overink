import Foundation
import Network
import OverInkCore

/// What the host decides about a `hello`.
enum RemoteHelloResult: Sendable {
    case accept(RemoteAdmission)
    case reject(RemoteError)
}

struct RemoteAdmission: Sendable {
    var welcome: RemoteWelcome
    var name: String
    var color: String
    /// Set for the paired owner device that may use `/lib`.
    var libToken: String?
}

/// HTTP + WebSocket server on the LAN port.
///
/// Everything here runs on one serial queue. Hosts talk to it through the closures below, which are called
/// on that queue (hop to the main actor inside them when needed).
final class RemoteServer: @unchecked Sendable {
    enum ServerError: Error { case invalidPort }

    // MARK: Hooks

    /// Decides a `hello`. `peer` is the remote address, `connectionID` the id the client will be known by.
    var onHello: (@Sendable (RemoteHello, _ peer: String, _ connectionID: String) async -> RemoteHelloResult)?
    /// A message from an admitted client.
    var onMessage: (@Sendable (_ connectionID: String, RemoteClientMessage) -> Void)?
    /// An admitted client came or went (also called for kicks).
    var onRosterChanged: (@Sendable () -> Void)?
    var onClientRemoved: (@Sendable (_ connectionID: String) -> Void)?
    /// Called when the number of clients that want live frames changes (0 means the streamer can idle).
    var onStreamDemandChanged: (@Sendable (_ activeClients: Int) -> Void)?
    /// Handles `/lib` and any other dynamic route. Return `nil` to fall through to static files.
    var routeHandler: (@Sendable (HTTPRequest, _ peer: String) async -> HTTPResponse?)?
    /// Files served at `/`.
    var staticRoot: URL?
    /// Maximum simultaneous admitted clients.
    var maxClients = 8

    // MARK: State (queue-confined)

    private let queue = DispatchQueue(label: "overink.remote.server")
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: Connection] = [:]
    private var clients: [String: Connection] = [:]
    private var allowedStaticFiles: Set<String> = []
    private var lastDemand = -1
    private var startReported = false

    private static let maxConnections = 64

    var isRunning: Bool { queue.sync { listener != nil } }
    var clientCount: Int { queue.sync { clients.count } }
    private(set) var port: UInt16 = 0

    // MARK: Lifecycle

    func start(port: Int, completion: @escaping @Sendable (Result<UInt16, Error>) -> Void) {
        guard let nwPort = NWEndpoint.Port(rawValue: UInt16(clamping: port)), port > 0 else {
            completion(.failure(ServerError.invalidPort))
            return
        }
        queue.async { [self] in
            guard listener == nil else {
                completion(.success(self.port))
                return
            }
            do {
                let parameters = NWParameters.tcp
                parameters.allowLocalEndpointReuse = true
                let listener = try NWListener(using: parameters, on: nwPort)
                allowedStaticFiles = Self.enumerateStaticFiles(staticRoot)
                startReported = false
                listener.stateUpdateHandler = { [weak self] state in
                    guard let self else { return }
                    switch state {
                    case .ready:
                        if !self.startReported {
                            self.startReported = true
                            self.port = nwPort.rawValue
                            completion(.success(nwPort.rawValue))
                        }
                    case let .failed(error):
                        if !self.startReported {
                            self.startReported = true
                            completion(.failure(error))
                        }
                        self.stopLocked()
                    default:
                        break
                    }
                }
                listener.newConnectionHandler = { [weak self] connection in
                    self?.accept(connection)
                }
                self.listener = listener
                listener.start(queue: queue)
            } catch {
                completion(.failure(error))
            }
        }
    }

    func stop() {
        queue.sync { stopLocked() }
    }

    private func stopLocked() {
        listener?.cancel()
        listener = nil
        for connection in connections.values { connection.close(sendCloseFrame: true) }
        connections.removeAll()
        clients.removeAll()
        updateDemand()
    }

    // MARK: Sending (callable from any thread)

    /// Queues a control message for one client.
    func send(_ data: Data, to id: String) {
        queue.async { [self] in clients[id]?.enqueueControl(data) }
    }

    /// Queues a control message for every admitted client except `id`; `libraryOnly` limits it to devices with `/lib` access.
    func broadcast(_ data: Data, except id: String? = nil, libraryOnly: Bool = false) {
        queue.async { [self] in
            for (clientID, client) in clients where clientID != id {
                if libraryOnly, client.libToken == nil { continue }
                client.enqueueControl(data)
            }
        }
    }

    /// Offers a binary frame to every client that is watching live. Only the newest frame per client is kept.
    func offerFrame(_ frame: Data) {
        queue.async { [self] in
            for client in clients.values where client.wantsFrames { client.offerFrame(frame) }
        }
    }

    func setDocMode(_ doc: Bool, for id: String) {
        queue.async { [self] in
            clients[id]?.docMode = doc
            updateDemand()
        }
    }

    func kick(_ id: String) {
        queue.async { [self] in
            guard let client = clients[id] else { return }
            client.close(sendCloseFrame: true)
            remove(client)
        }
    }

    func roster() -> [RemoteRosterEntry] {
        queue.sync {
            clients.map { RemoteRosterEntry(clientId: $0.key, name: $0.value.name, color: $0.value.color) }
                .sorted { $0.clientId < $1.clientId }
        }
    }

    /// Colours currently held by participants (for picking a free one).
    func usedColors() -> [String] {
        queue.sync { clients.values.map(\.color) }
    }

    // MARK: Accepting

    private func accept(_ nwConnection: NWConnection) {
        guard connections.count < Self.maxConnections else {
            nwConnection.cancel()
            return
        }
        let connection = Connection(nw: nwConnection, queue: queue)
        connection.peer = Self.address(of: nwConnection.endpoint)
        connections[ObjectIdentifier(connection)] = connection

        guard RemoteNetwork.isAllowedRemote(connection.peer) else {
            connection.start()
            connection.sendAndClose(HTTPResponse.empty(403).serialized())
            return
        }

        connection.onData = { [weak self, weak connection] in
            guard let self, let connection else { return }
            self.handleData(connection)
        }
        connection.onClosed = { [weak self, weak connection] in
            guard let self, let connection else { return }
            self.remove(connection)
        }
        connection.start()
        connection.armHeaderTimeout()
    }

    private func remove(_ connection: Connection) {
        connections[ObjectIdentifier(connection)] = nil
        if let id = connection.clientID, clients[id] === connection {
            clients[id] = nil
            onClientRemoved?(id)
            onRosterChanged?()
            updateDemand()
        }
    }

    private func updateDemand() {
        let demand = clients.values.filter(\.wantsFrames).count
        if demand != lastDemand {
            lastDemand = demand
            onStreamDemandChanged?(demand)
        }
    }

    // MARK: Data handling

    private func handleData(_ connection: Connection) {
        switch connection.mode {
        case .http:
            handleHTTP(connection)
        case .webSocket:
            handleWebSocket(connection)
        }
    }

    private func handleHTTP(_ connection: Connection) {
        switch HTTPRequestParser.parse(connection.inbox) {
        case .needMore:
            return
        case let .invalid(status):
            connection.sendAndClose(HTTPResponse.text(status, "Bad request").serialized())
        case let .request(request, consumed):
            connection.inbox.removeFirst(consumed)
            connection.cancelHeaderTimeout()
            route(request, on: connection)
        }
    }

    private func route(_ request: HTTPRequest, on connection: Connection) {
        let peer = connection.peer
        let head = request.method == "HEAD"

        if request.path == "/ws" {
            switch WebSocketHandshake.respond(to: request) {
            case let .reject(status):
                connection.sendAndClose(HTTPResponse.text(status, "WebSocket request expected.").serialized())
            case let .upgrade(response):
                connection.mode = .webSocket
                connection.send(response)
                connection.armIdleTimeout()
                // Bytes after the request (none expected) would already be in the inbox.
                handleWebSocket(connection)
            }
            return
        }
        if request.path == "/health" {
            connection.sendAndClose(HTTPResponse.json(["ok": true]).serialized(includeBody: !head))
            return
        }

        let handler = routeHandler
        let root = staticRoot
        let allowed = allowedStaticFiles
        connection.beginWork()
        Task { [weak connection] in
            var response = await handler?(request, peer)
            if response == nil {
                response = Self.staticResponse(for: request, root: root, allowed: allowed)
            }
            let final = response ?? HTTPResponse.text(404, "Not found")
            self.queue.async {
                connection?.sendAndClose(final.serialized(includeBody: !head))
            }
        }
    }

    static func staticResponse(for request: HTTPRequest, root: URL?, allowed: Set<String>) -> HTTPResponse? {
        guard request.method == "GET" || request.method == "HEAD" else { return nil }
        guard let root, let relative = StaticFileResolver.relativePath(for: request.path, allowed: allowed),
              let data = try? Data(contentsOf: root.appendingPathComponent(relative)) else {
            return nil
        }
        return HTTPResponse(
            status: 200,
            headers: [
                ("Content-Type", MIMEType.forPath(relative)),
                ("Cache-Control", "no-store, no-cache, must-revalidate")
            ],
            body: data
        )
    }

    private static func enumerateStaticFiles(_ root: URL?) -> Set<String> {
        guard let root,
              let walker = FileManager.default.enumerator(
                  at: root, includingPropertiesForKeys: [.isRegularFileKey], options: [.skipsHiddenFiles]
              ) else {
            return []
        }
        var files: Set<String> = []
        let prefix = root.standardizedFileURL.path + "/"
        for case let url as URL in walker {
            guard (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else { continue }
            let path = url.standardizedFileURL.path
            if path.hasPrefix(prefix) { files.insert(String(path.dropFirst(prefix.count))) }
        }
        return files
    }

    // MARK: WebSocket

    private func handleWebSocket(_ connection: Connection) {
        connection.decoder.append(connection.inbox)
        connection.inbox.removeAll()
        guard !connection.helloInFlight else { return }

        while true {
            let event: WebSocketEvent?
            do {
                event = try connection.decoder.next()
            } catch {
                connection.close(sendCloseFrame: true, code: 1002)
                return
            }
            guard let event else { return }
            connection.touch()

            switch event {
            case let .ping(payload):
                connection.sendRaw(WebSocketFrameEncoder.pong(payload))
            case .pong:
                break
            case .close:
                connection.close(sendCloseFrame: true)
                return
            case .binary:
                break // Clients never send binary; ignore like the Windows host.
            case let .text(data):
                if connection.clientID == nil {
                    admit(connection, helloData: data)
                    return
                }
                if let message = RemoteClientMessage.decode(data), let id = connection.clientID {
                    switch message {
                    case let .viewMode(doc):
                        connection.docMode = doc
                        updateDemand()
                    default:
                        break
                    }
                    onMessage?(id, message)
                }
            }
        }
    }

    private func admit(_ connection: Connection, helloData: Data) {
        guard let type = try? RemoteJSON.messageType(in: helloData), type == "hello" else {
            rejectHello(connection, RemoteError(.badHello, "First message must be hello."))
            return
        }
        guard let hello = try? RemoteJSON.decoder().decode(RemoteHello.self, from: helloData),
              hello.v == RemoteProtocol.version else {
            rejectHello(connection, RemoteError(.versionMismatch, "Unsupported protocol version."))
            return
        }
        guard let onHello else {
            rejectHello(connection, RemoteError(.denied, "Remote sketching is not available."))
            return
        }

        let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        connection.helloInFlight = true
        let peer = connection.peer
        Task { [weak connection] in
            let result = await onHello(hello, peer, id)
            self.queue.async {
                guard let connection else { return }
                connection.helloInFlight = false
                switch result {
                case let .reject(error):
                    self.rejectHello(connection, error)
                case let .accept(admission):
                    guard self.clients.count < self.maxClients else {
                        self.rejectHello(connection, RemoteError(.sessionFull, "Session is full."))
                        return
                    }
                    connection.clientID = id
                    connection.name = admission.name
                    connection.color = admission.color
                    connection.libToken = admission.libToken
                    self.clients[id] = connection
                    if let welcome = try? RemoteJSON.wrap("welcome", payload: admission.welcome) {
                        connection.enqueueControl(welcome)
                    }
                    self.updateDemand()
                    self.onRosterChanged?()
                    // Messages that arrived while the hello was being decided.
                    self.handleWebSocket(connection)
                }
            }
        }
    }

    private func rejectHello(_ connection: Connection, _ error: RemoteError) {
        if let data = try? RemoteJSON.wrap("error", payload: error) {
            connection.sendRaw(WebSocketFrameEncoder.text(data))
        }
        connection.close(sendCloseFrame: true, afterFlush: true)
    }

    // MARK: Helpers

    static func address(of endpoint: NWEndpoint) -> String {
        guard case let .hostPort(host, _) = endpoint else { return "" }
        switch host {
        case let .ipv4(address): return "\(address)".split(separator: "%").first.map(String.init) ?? "\(address)"
        case let .ipv6(address): return "\(address)"
        case let .name(name, _): return name
        @unknown default: return ""
        }
    }
}

// MARK: - Connection

private final class Connection: @unchecked Sendable {
    enum Mode { case http, webSocket }

    let nw: NWConnection
    let queue: DispatchQueue
    var peer = ""
    var mode = Mode.http
    var inbox = Data()
    var decoder = WebSocketDecoder()
    var onData: (() -> Void)?
    var onClosed: (() -> Void)?

    // Set once admitted.
    var clientID: String?
    var name = "Guest"
    var color = "#FFFFFF"
    var libToken: String?
    var docMode = false
    var helloInFlight = false

    private var controlQueue: [Data] = []
    private var pendingFrame: Data?
    private var sending = false
    private var closed = false
    private var closeAfterFlush = false
    private var headerTimer: DispatchWorkItem?
    private var idleTimer: DispatchWorkItem?
    private var lastActivity = Date()

    init(nw: NWConnection, queue: DispatchQueue) {
        self.nw = nw
        self.queue = queue
    }

    var wantsFrames: Bool { clientID != nil && !docMode && !closed }

    func start() {
        nw.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed, .cancelled:
                self.finish()
            default:
                break
            }
        }
        nw.start(queue: queue)
        receive()
    }

    private func receive() {
        nw.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self, !self.closed else { return }
            if let data, !data.isEmpty {
                self.inbox.append(data)
                self.onData?()
            }
            if error != nil || isComplete {
                self.finish()
                return
            }
            self.receive()
        }
    }

    // MARK: Sending

    func enqueueControl(_ json: Data) {
        guard !closed else { return }
        controlQueue.append(WebSocketFrameEncoder.text(json))
        pump()
    }

    func offerFrame(_ frame: Data) {
        guard !closed else { return }
        pendingFrame = WebSocketFrameEncoder.binary(frame)
        pump()
    }

    /// Writes bytes now, ahead of queued control messages (handshake and protocol replies).
    func send(_ data: Data) {
        controlQueue.insert(data, at: 0)
        pump()
    }

    func sendRaw(_ data: Data) {
        controlQueue.append(data)
        pump()
    }

    func sendAndClose(_ data: Data) {
        closeAfterFlush = true
        controlQueue.append(data)
        pump()
    }

    func beginWork() {
        cancelHeaderTimeout()
    }

    private func pump() {
        guard !sending, !closed else { return }
        let next: Data
        if !controlQueue.isEmpty {
            next = controlQueue.removeFirst()
        } else if let frame = pendingFrame {
            pendingFrame = nil
            next = frame
        } else {
            if closeAfterFlush { finish() }
            return
        }
        sending = true
        nw.send(content: next, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.sending = false
            if error != nil {
                self.finish()
                return
            }
            self.pump()
        })
    }

    // MARK: Closing

    func close(sendCloseFrame: Bool, code: UInt16 = 1000, afterFlush: Bool = false) {
        guard !closed else { return }
        if sendCloseFrame, mode == .webSocket {
            controlQueue.append(WebSocketFrameEncoder.close(code: code))
        }
        closeAfterFlush = true
        if afterFlush || sendCloseFrame {
            pump()
            // Give the close frame a moment, then drop the socket regardless.
            queue.asyncAfter(deadline: .now() + 1) { [weak self] in self?.finish() }
        } else {
            finish()
        }
    }

    private func finish() {
        guard !closed else { return }
        closed = true
        headerTimer?.cancel()
        idleTimer?.cancel()
        controlQueue.removeAll()
        pendingFrame = nil
        nw.cancel()
        let callback = onClosed
        onData = nil
        onClosed = nil
        callback?()
    }

    // MARK: Timeouts

    func armHeaderTimeout() {
        let work = DispatchWorkItem { [weak self] in self?.finish() }
        headerTimer = work
        queue.asyncAfter(deadline: .now() + 15, execute: work)
    }

    func cancelHeaderTimeout() {
        headerTimer?.cancel()
        headerTimer = nil
    }

    func touch() {
        lastActivity = Date()
    }

    /// The web client pings every 2 seconds, so a long silence means the tablet is gone.
    func armIdleTimeout() {
        idleTimer?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.closed else { return }
            if Date().timeIntervalSince(self.lastActivity) > 60 {
                self.finish()
            } else {
                self.armIdleTimeout()
            }
        }
        idleTimer = work
        queue.asyncAfter(deadline: .now() + 20, execute: work)
    }
}
