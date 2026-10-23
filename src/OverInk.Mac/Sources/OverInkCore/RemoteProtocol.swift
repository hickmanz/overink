import Foundation

public enum RemoteProtocol {
    public static let version = 1
    public static let binaryFrameType: UInt8 = 1
    /// `[type u8][seq u32][inkRevision i64][width u16][height u16]`, all little-endian, then the JPEG.
    public static let frameHeaderSize = 17
    /// Largest text message accepted from a client. Larger ones are dropped.
    public static let maxClientMessageBytes = 1_048_576
    /// Cap on numbers in one `strokePoints`/`erase` message, so a bad client cannot make the host allocate freely.
    public static let maxPointValuesPerMessage = 60_000
    /// Colors handed to participants in join order (same palette as the Windows host).
    public static let participantColors = [
        "#E53935", "#1E88E5", "#43A047", "#FB8C00", "#8E24AA", "#00ACC1", "#FDD835", "#6D4C41"
    ]
}

/// Stroke ids travel as lowercase GUID strings (what the web client's `crypto.randomUUID()` makes and compares).
public enum RemoteID {
    public static func wire(_ id: UUID) -> String { id.uuidString.lowercased() }

    public static func uuid(_ wire: String) -> UUID? { UUID(uuidString: wire) }

    public static func normalize(_ wire: String) -> String { wire.lowercased() }
}

// MARK: - Shared payloads

public struct RemoteHello: Codable, Equatable, Sendable {
    public var v: Int
    public var deviceKey: String?
    public var pair: String?
    public var join: String?
    public var deviceName: String

    public init(
        v: Int = RemoteProtocol.version,
        deviceKey: String? = nil,
        pair: String? = nil,
        join: String? = nil,
        deviceName: String
    ) {
        self.v = v
        self.deviceKey = deviceKey
        self.pair = pair
        self.join = join
        self.deviceName = deviceName
    }
}

public struct RemoteMonitorRect: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var w: Double
    public var h: Double
    public var primary: Bool

    public init(x: Double, y: Double, w: Double, h: Double, primary: Bool) {
        self.x = x
        self.y = y
        self.w = w
        self.h = h
        self.primary = primary
    }
}

public struct RemoteState: Codable, Equatable, Sendable {
    public var tool: String
    public var color: String
    public var size: Double
    public var mode: String
    public var screenW: Double
    public var screenH: Double
    public var regionActive: Bool
    public var pressureSensitive: Bool
    public var screenVeil: Double
    public var customColors: [String]?
    public var streamEpoch: Int
    public var monitors: [RemoteMonitorRect]?

    public init(
        tool: String,
        color: String,
        size: Double,
        mode: String,
        screenW: Double,
        screenH: Double,
        regionActive: Bool,
        pressureSensitive: Bool = true,
        screenVeil: Double = 0,
        customColors: [String]? = nil,
        streamEpoch: Int = 0,
        monitors: [RemoteMonitorRect]? = nil
    ) {
        self.tool = tool
        self.color = color
        self.size = size
        self.mode = mode
        self.screenW = screenW
        self.screenH = screenH
        self.regionActive = regionActive
        self.pressureSensitive = pressureSensitive
        self.screenVeil = screenVeil
        self.customColors = customColors
        self.streamEpoch = streamEpoch
        self.monitors = monitors
    }
}

public struct RemoteStrokeSnapshot: Codable, Equatable, Sendable {
    /// Lowercase GUID string (see `RemoteID`).
    public var id: String
    public var color: String
    public var size: Double
    /// `x, y, pressure` triples; x and y are 0-1 fractions of the streamed rectangle.
    public var pts: [Float]
    public var ignorePressure: Bool

    public init(id: String, color: String, size: Double, pts: [Float], ignorePressure: Bool) {
        self.id = id
        self.color = color
        self.size = size
        self.pts = pts
        self.ignorePressure = ignorePressure
    }
}

// MARK: - Host to client

public struct RemoteWelcome: Codable, Equatable, Sendable {
    public var v: Int
    public var clientId: String
    public var deviceKey: String?
    public var screenW: Double
    public var screenH: Double
    public var state: RemoteState
    public var inkRevision: Int64
    public var libToken: String?

    public init(
        v: Int = RemoteProtocol.version,
        clientId: String,
        deviceKey: String? = nil,
        screenW: Double,
        screenH: Double,
        state: RemoteState,
        inkRevision: Int64,
        libToken: String? = nil
    ) {
        self.v = v
        self.clientId = clientId
        self.deviceKey = deviceKey
        self.screenW = screenW
        self.screenH = screenH
        self.state = state
        self.inkRevision = inkRevision
        self.libToken = libToken
    }
}

public enum RemoteErrorCode: String, Sendable {
    case badHello, versionMismatch, badToken, approvalRequired, denied, sessionFull
}

public struct RemoteError: Codable, Equatable, Sendable {
    public var code: String
    public var message: String

    public init(code: String, message: String) {
        self.code = code
        self.message = message
    }

    public init(_ code: RemoteErrorCode, _ message: String) {
        self.init(code: code.rawValue, message: message)
    }
}

public struct RemoteAck: Codable, Equatable, Sendable {
    public var id: String
    public var inkRevision: Int64

    public init(id: String, inkRevision: Int64) {
        self.id = id
        self.inkRevision = inkRevision
    }
}

public struct RemoteStrokesRemoved: Codable, Equatable, Sendable {
    public var ids: [String]
    public init(ids: [String]) { self.ids = ids }
}

public struct RemoteStrokesAdded: Codable, Equatable, Sendable {
    public var strokes: [RemoteStrokeSnapshot]
    public init(strokes: [RemoteStrokeSnapshot]) { self.strokes = strokes }
}

public struct RemoteRosterEntry: Codable, Equatable, Sendable {
    public var clientId: String
    public var name: String
    public var color: String

    public init(clientId: String, name: String, color: String) {
        self.clientId = clientId
        self.name = name
        self.color = color
    }
}

public struct RemoteRoster: Codable, Equatable, Sendable {
    public var participants: [RemoteRosterEntry]
    public init(participants: [RemoteRosterEntry]) { self.participants = participants }
}

public struct RemotePong: Codable, Equatable, Sendable {
    public var ms: Double
    public init(ms: Double) { self.ms = ms }
}

public struct RemoteCaptured: Codable, Equatable, Sendable {
    public var id: String
    public init(id: String) { self.id = id }
}

public struct RemoteCaptureFailed: Codable, Equatable, Sendable {
    public var message: String
    public init(message: String) { self.message = message }
}

public struct RemoteLibraryItemChanged: Codable, Equatable, Sendable {
    public var id: String
    public var rev: String

    public init(id: String, rev: String) {
        self.id = id
        self.rev = rev
    }
}

public struct RemoteEmpty: Codable, Equatable, Sendable {
    public init() {}
}

public struct RemotePeerStrokeBegin: Codable, Equatable, Sendable {
    public var author: String
    public var id: String
    public var color: String
    public var size: Double
    public var x: Double
    public var y: Double
    public var p: Float

    public init(author: String, id: String, color: String, size: Double, x: Double, y: Double, p: Float) {
        self.author = author
        self.id = id
        self.color = color
        self.size = size
        self.x = x
        self.y = y
        self.p = p
    }
}

public struct RemotePeerStrokePoints: Codable, Equatable, Sendable {
    public var author: String
    public var id: String
    public var pts: [Float]

    public init(author: String, id: String, pts: [Float]) {
        self.author = author
        self.id = id
        self.pts = pts
    }
}

public struct RemotePeerStrokeID: Codable, Equatable, Sendable {
    public var author: String
    public var id: String

    public init(author: String, id: String) {
        self.author = author
        self.id = id
    }
}

public struct RemotePeerShape: Codable, Equatable, Sendable {
    public var author: String
    public var id: String
    public var tool: String
    public var x1: Double
    public var y1: Double
    public var x2: Double
    public var y2: Double
    public var color: String
    public var size: Double

    public init(
        author: String, id: String, tool: String,
        x1: Double, y1: Double, x2: Double, y2: Double, color: String, size: Double
    ) {
        self.author = author
        self.id = id
        self.tool = tool
        self.x1 = x1
        self.y1 = y1
        self.x2 = x2
        self.y2 = y2
        self.color = color
        self.size = size
    }
}

// MARK: - Client to host

public struct RemoteStrokeBegin: Codable, Equatable, Sendable {
    public var id: String
    public var color: String
    public var size: Double
    public var x: Double
    public var y: Double
    public var p: Float

    public init(id: String, color: String, size: Double, x: Double, y: Double, p: Float = 1) {
        self.id = id
        self.color = color
        self.size = size
        self.x = x
        self.y = y
        self.p = p
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        color = try c.decodeIfPresent(String.self, forKey: .color) ?? ""
        size = try c.decodeIfPresent(Double.self, forKey: .size) ?? 0
        x = try c.decode(Double.self, forKey: .x)
        y = try c.decode(Double.self, forKey: .y)
        p = try c.decodeIfPresent(Float.self, forKey: .p) ?? 1
    }
}

public struct RemoteStrokePoints: Codable, Equatable, Sendable {
    public var id: String
    public var pts: [Float]

    public init(id: String, pts: [Float]) {
        self.id = id
        self.pts = pts
    }
}

public struct RemoteStrokeID: Codable, Equatable, Sendable {
    public var id: String
    public init(id: String) { self.id = id }
}

public struct RemoteShape: Codable, Equatable, Sendable {
    public var id: String
    public var tool: String
    public var x1: Double
    public var y1: Double
    public var x2: Double
    public var y2: Double
    public var color: String
    public var size: Double

    public init(
        id: String, tool: String,
        x1: Double, y1: Double, x2: Double, y2: Double, color: String, size: Double
    ) {
        self.id = id
        self.tool = tool
        self.x1 = x1
        self.y1 = y1
        self.x2 = x2
        self.y2 = y2
        self.color = color
        self.size = size
    }
}

public struct RemoteErase: Codable, Equatable, Sendable {
    /// `x, y` pairs (0-1). One pair is a tap; two or more are the ends of a swept segment.
    public var pts: [Float]
    /// Eraser diameter in the host's logical points.
    public var size: Double
    public var whole: Bool?

    public init(pts: [Float], size: Double, whole: Bool? = nil) {
        self.pts = pts
        self.size = size
        self.whole = whole
    }
}

public struct RemoteHover: Codable, Equatable, Sendable {
    public var clientId: String
    public var x: Double
    public var y: Double

    public init(clientId: String, x: Double, y: Double) {
        self.clientId = clientId
        self.x = x
        self.y = y
    }
}

/// Every message a client may send after `hello`.
public enum RemoteClientMessage: Equatable, Sendable {
    case strokeBegin(RemoteStrokeBegin)
    case strokePoints(RemoteStrokePoints)
    case strokeEnd(id: String)
    case strokeCancel(id: String)
    case shapePreview(RemoteShape)
    case shapeCommit(RemoteShape)
    case erase(RemoteErase)
    case setTool(String)
    case setColor(String)
    case saveColor(String)
    case removeColor(String)
    case setSize(Double)
    case setPressure(Bool)
    case setVeil(Double)
    case undo
    case redo
    case clear
    case ping(ms: Double)
    /// Relayed to the other clients unchanged.
    case hover(RemoteHover)
    /// `true` for `doc` (editing a library capture, live frames paused).
    case viewMode(doc: Bool)
    case capture

    /// Parses a post-hello message. Returns `nil` for unknown types or malformed payloads.
    public static func decode(_ data: Data) -> RemoteClientMessage? {
        guard data.count <= RemoteProtocol.maxClientMessageBytes,
              let type = try? RemoteJSON.messageType(in: data) else {
            return nil
        }
        let decoder = RemoteJSON.decoder()
        func payload<T: Decodable>(_ type: T.Type) -> T? { try? decoder.decode(T.self, from: data) }

        switch type {
        case "strokeBegin":
            return payload(RemoteStrokeBegin.self).map { .strokeBegin($0) }
        case "strokePoints":
            guard let message = payload(RemoteStrokePoints.self),
                  message.pts.count <= RemoteProtocol.maxPointValuesPerMessage else { return nil }
            return .strokePoints(message)
        case "strokeEnd":
            return payload(RemoteStrokeID.self).map { .strokeEnd(id: $0.id) }
        case "strokeCancel":
            return payload(RemoteStrokeID.self).map { .strokeCancel(id: $0.id) }
        case "shapePreview":
            return payload(RemoteShape.self).map { .shapePreview($0) }
        case "shapeCommit":
            return payload(RemoteShape.self).map { .shapeCommit($0) }
        case "erase":
            guard let message = payload(RemoteErase.self),
                  message.pts.count >= 2,
                  message.pts.count <= RemoteProtocol.maxPointValuesPerMessage else { return nil }
            return .erase(message)
        case "setTool":
            return payload(StringField.self).flatMap { $0.tool }.map { .setTool($0) }
        case "setColor":
            return payload(StringField.self).flatMap { $0.color }.map { .setColor($0) }
        case "saveColor":
            return payload(StringField.self).flatMap { $0.color }.map { .saveColor($0) }
        case "removeColor":
            return payload(StringField.self).flatMap { $0.color }.map { .removeColor($0) }
        case "setSize":
            return payload(NumberField.self).flatMap { $0.size }.map { .setSize($0) }
        case "setPressure":
            return payload(BoolField.self).flatMap { $0.enabled }.map { .setPressure($0) }
        case "setVeil":
            return payload(NumberField.self).flatMap { $0.value }.map { .setVeil($0) }
        case "undo": return .undo
        case "redo": return .redo
        case "clear": return .clear
        case "ping":
            return .ping(ms: payload(NumberField.self)?.ms ?? 0)
        case "hover":
            return payload(RemoteHover.self).map { .hover($0) }
        case "viewMode":
            guard let mode = payload(StringField.self)?.mode else { return nil }
            return .viewMode(doc: mode.lowercased() == "doc")
        case "capture": return .capture
        default: return nil
        }
    }

    private struct StringField: Decodable {
        var tool: String?
        var color: String?
        var mode: String?
    }

    private struct NumberField: Decodable {
        var size: Double?
        var value: Double?
        var ms: Double?
    }

    private struct BoolField: Decodable {
        var enabled: Bool?
    }
}

// MARK: - JSON helpers

public enum RemoteJSON {
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }

    public static func decoder() -> JSONDecoder {
        JSONDecoder()
    }

    /// Encodes `payload` as a JSON object and adds the `"t"` type discriminator as the first key,
    /// without round-tripping through `JSONSerialization` (which would widen `Float` values).
    public static func wrap<T: Encodable>(_ type: String, payload: T) throws -> Data {
        let body = try encoder().encode(payload)
        guard let first = body.first, first == UInt8(ascii: "{"), let last = body.last, last == UInt8(ascii: "}") else {
            throw EncodingError.invalidValue(
                payload,
                EncodingError.Context(codingPath: [], debugDescription: "Payload must encode as an object")
            )
        }
        let inner = body.dropFirst().dropLast()
        var out = Data()
        out.append(contentsOf: Array(#"{"t":"#.utf8))
        out.append(try JSONEncoder().encode(type))
        if !inner.isEmpty {
            out.append(UInt8(ascii: ","))
            out.append(inner)
        }
        out.append(UInt8(ascii: "}"))
        return out
    }

    public static func wrap(_ type: String) -> Data {
        // A bare message has no payload; this cannot fail.
        (try? wrap(type, payload: RemoteEmpty())) ?? Data()
    }

    public static func messageType(in data: Data) throws -> String? {
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return object?["t"] as? String
    }
}

// MARK: - Binary frames

public struct RemoteFrameHeader: Equatable, Sendable {
    public var sequence: UInt32
    public var inkRevision: Int64
    public var width: UInt16
    public var height: UInt16

    public init(sequence: UInt32, inkRevision: Int64, width: UInt16, height: UInt16) {
        self.sequence = sequence
        self.inkRevision = inkRevision
        self.width = width
        self.height = height
    }
}

public enum RemoteFrame {
    public static func encode(header: RemoteFrameHeader, jpeg: Data) -> Data {
        var data = Data(capacity: RemoteProtocol.frameHeaderSize + jpeg.count)
        data.append(RemoteProtocol.binaryFrameType)
        append(header.sequence, to: &data)
        append(UInt64(bitPattern: header.inkRevision), to: &data)
        append(header.width, to: &data)
        append(header.height, to: &data)
        data.append(jpeg)
        return data
    }

    /// Reads a frame's header and returns it with the JPEG payload. `nil` when the data is not a frame.
    public static func decode(_ data: Data) -> (header: RemoteFrameHeader, jpeg: Data)? {
        guard data.count >= RemoteProtocol.frameHeaderSize else { return nil }
        let bytes = [UInt8](data.prefix(RemoteProtocol.frameHeaderSize))
        guard bytes[0] == RemoteProtocol.binaryFrameType else { return nil }
        let header = RemoteFrameHeader(
            sequence: UInt32(littleEndianBytes: bytes[1 ..< 5]),
            inkRevision: Int64(bitPattern: UInt64(littleEndianBytes: bytes[5 ..< 13])),
            width: UInt16(littleEndianBytes: bytes[13 ..< 15]),
            height: UInt16(littleEndianBytes: bytes[15 ..< 17])
        )
        return (header, Data(data.dropFirst(RemoteProtocol.frameHeaderSize)))
    }

    private static func append<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
        withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
    }
}

private extension FixedWidthInteger {
    init(littleEndianBytes bytes: ArraySlice<UInt8>) {
        var value: Self = 0
        for (index, byte) in bytes.enumerated() {
            value |= Self(truncatingIfNeeded: byte) << (8 * index)
        }
        self = value
    }
}
