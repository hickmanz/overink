import CryptoKit
import Foundation

public enum WebSocketOpcode: UInt8, Sendable {
    case continuation = 0x0
    case text = 0x1
    case binary = 0x2
    case close = 0x8
    case ping = 0x9
    case pong = 0xA

    var isControl: Bool { rawValue >= 0x8 }
}

public enum WebSocketHandshake {
    private static let guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

    /// `Sec-WebSocket-Accept` for a client's `Sec-WebSocket-Key` (RFC 6455 section 4.2.2).
    public static func acceptKey(for clientKey: String) -> String {
        let digest = Insecure.SHA1.hash(data: Data((clientKey + guid).utf8))
        return Data(digest).base64EncodedString()
    }

    public enum Outcome: Equatable, Sendable {
        case upgrade(response: Data)
        case reject(status: Int)
    }

    /// Validates an upgrade request and builds the `101` response.
    public static func respond(to request: HTTPRequest) -> Outcome {
        guard request.method == "GET",
              request.header("upgrade")?.lowercased() == "websocket",
              request.header("connection")?.lowercased().contains("upgrade") == true,
              let key = request.header("sec-websocket-key"),
              Data(base64Encoded: key)?.count == 16 else {
            return .reject(status: 400)
        }
        guard request.header("sec-websocket-version") == "13" else { return .reject(status: 400) }
        let response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: \(acceptKey(for: key))\r\n\r\n"
        return .upgrade(response: Data(response.utf8))
    }
}

public enum WebSocketEvent: Equatable, Sendable {
    case text(Data)
    case binary(Data)
    case ping(Data)
    case pong(Data)
    case close(code: UInt16?)
}

public enum WebSocketError: Error, Equatable, Sendable {
    case unmaskedClientFrame
    case reservedBitsSet
    case invalidOpcode
    case fragmentedControlFrame
    case controlFrameTooLong
    case unexpectedContinuation
    case interruptedMessage
    case messageTooLarge
}

/// Builds frames the server sends (never masked).
public enum WebSocketFrameEncoder {
    public static func encode(_ opcode: WebSocketOpcode, payload: Data) -> Data {
        var frame = Data(capacity: payload.count + 10)
        frame.append(0x80 | opcode.rawValue)
        let length = payload.count
        if length < 126 {
            frame.append(UInt8(length))
        } else if length <= 0xFFFF {
            frame.append(126)
            frame.append(UInt8(length >> 8))
            frame.append(UInt8(length & 0xFF))
        } else {
            frame.append(127)
            for shift in stride(from: 56, through: 0, by: -8) {
                frame.append(UInt8((UInt64(length) >> UInt64(shift)) & 0xFF))
            }
        }
        frame.append(payload)
        return frame
    }

    public static func text(_ data: Data) -> Data { encode(.text, payload: data) }
    public static func binary(_ data: Data) -> Data { encode(.binary, payload: data) }
    public static func pong(_ data: Data) -> Data { encode(.pong, payload: data) }

    public static func close(code: UInt16 = 1000) -> Data {
        encode(.close, payload: Data([UInt8(code >> 8), UInt8(code & 0xFF)]))
    }
}

/// Incrementally decodes the masked frames a browser sends and reassembles fragmented messages.
public struct WebSocketDecoder: Sendable {
    public let maxMessageBytes: Int
    private var buffer = Data()
    private var messageOpcode: WebSocketOpcode?
    private var messageBytes = Data()

    public init(maxMessageBytes: Int = 4 * 1024 * 1024) {
        self.maxMessageBytes = maxMessageBytes
    }

    public mutating func append(_ data: Data) {
        buffer.append(data)
    }

    /// The next complete event, or `nil` when more bytes are needed. After an error the connection must close.
    public mutating func next() throws -> WebSocketEvent? {
        while true {
            guard let frame = try readFrame() else { return nil }
            if frame.opcode.isControl {
                switch frame.opcode {
                case .ping: return .ping(frame.payload)
                case .pong: return .pong(frame.payload)
                default:
                    let code = frame.payload.count >= 2
                        ? UInt16(frame.payload[frame.payload.startIndex]) << 8
                            | UInt16(frame.payload[frame.payload.startIndex + 1])
                        : nil
                    return .close(code: code)
                }
            }

            if frame.opcode == .continuation {
                guard messageOpcode != nil else { throw WebSocketError.unexpectedContinuation }
            } else {
                guard messageOpcode == nil else { throw WebSocketError.interruptedMessage }
                messageOpcode = frame.opcode
            }
            messageBytes.append(frame.payload)
            guard messageBytes.count <= maxMessageBytes else { throw WebSocketError.messageTooLarge }

            if frame.fin, let opcode = messageOpcode {
                let message = messageBytes
                messageOpcode = nil
                messageBytes = Data()
                return opcode == .text ? .text(message) : .binary(message)
            }
        }
    }

    private struct Frame {
        var fin: Bool
        var opcode: WebSocketOpcode
        var payload: Data
    }

    private mutating func readFrame() throws -> Frame? {
        guard buffer.count >= 2 else { return nil }
        let base = buffer.startIndex
        let b0 = buffer[base], b1 = buffer[base + 1]
        guard b0 & 0x70 == 0 else { throw WebSocketError.reservedBitsSet }
        guard let opcode = WebSocketOpcode(rawValue: b0 & 0x0F) else { throw WebSocketError.invalidOpcode }
        let fin = b0 & 0x80 != 0
        guard b1 & 0x80 != 0 else { throw WebSocketError.unmaskedClientFrame }
        if opcode.isControl, !fin { throw WebSocketError.fragmentedControlFrame }

        var length = Int(b1 & 0x7F)
        var offset = 2
        if length == 126 {
            guard buffer.count >= offset + 2 else { return nil }
            length = Int(buffer[base + 2]) << 8 | Int(buffer[base + 3])
            offset += 2
        } else if length == 127 {
            guard buffer.count >= offset + 8 else { return nil }
            var wide: UInt64 = 0
            for index in 0 ..< 8 { wide = wide << 8 | UInt64(buffer[base + offset + index]) }
            guard wide <= UInt64(maxMessageBytes) else { throw WebSocketError.messageTooLarge }
            length = Int(wide)
            offset += 8
        }
        if opcode.isControl, length > 125 { throw WebSocketError.controlFrameTooLong }
        guard length <= maxMessageBytes else { throw WebSocketError.messageTooLarge }

        guard buffer.count >= offset + 4 + length else { return nil }
        let mask = [buffer[base + offset], buffer[base + offset + 1], buffer[base + offset + 2], buffer[base + offset + 3]]
        offset += 4
        var payload = Data(count: length)
        for index in 0 ..< length {
            payload[index] = buffer[base + offset + index] ^ mask[index & 3]
        }
        buffer.removeSubrange(base ..< base + offset + length)
        return Frame(fin: fin, opcode: opcode, payload: payload)
    }
}
