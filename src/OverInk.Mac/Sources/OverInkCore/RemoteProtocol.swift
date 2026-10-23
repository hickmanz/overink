import Foundation

public enum RemoteProtocol {
    public static let version = 1
    public static let binaryFrameType: UInt8 = 1
}

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
    public var id: UUID
    public var color: String
    public var size: Double
    public var pts: [Float]
    public var ignorePressure: Bool

    public init(id: UUID, color: String, size: Double, pts: [Float], ignorePressure: Bool) {
        self.id = id
        self.color = color
        self.size = size
        self.pts = pts
        self.ignorePressure = ignorePressure
    }
}

public enum RemoteJSON {
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }

    public static func decoder() -> JSONDecoder {
        JSONDecoder()
    }

    public static func wrap<T: Encodable>(_ type: String, payload: T) throws -> Data {
        let payloadData = try encoder().encode(payload)
        guard var object = try JSONSerialization.jsonObject(with: payloadData) as? [String: Any] else {
            throw EncodingError.invalidValue(
                payload,
                EncodingError.Context(codingPath: [], debugDescription: "Payload must encode as an object")
            )
        }
        object["t"] = type
        return try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
    }

    public static func messageType(in data: Data) throws -> String? {
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return object?["t"] as? String
    }
}
