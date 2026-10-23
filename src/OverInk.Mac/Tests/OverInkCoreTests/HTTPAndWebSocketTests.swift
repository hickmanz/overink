import Foundation
import Testing
@testable import OverInkCore

@Suite struct HTTPAndWebSocketTests {
    private func data(_ text: String) -> Data { Data(text.utf8) }

    // MARK: HTTP parsing

    @Test func parsesASimpleGet() throws {
        let raw = data("GET /lib?k=abc%2Bdef&x=1 HTTP/1.1\r\nHost: mac.local\r\nX-Test:  yes \r\n\r\n")
        guard case let .request(request, consumed) = HTTPRequestParser.parse(raw) else {
            Issue.record("expected a request")
            return
        }
        #expect(request.method == "GET")
        #expect(request.path == "/lib")
        #expect(request.query["k"] == "abc+def")
        #expect(request.query["x"] == "1")
        #expect(request.header("HOST") == "mac.local")
        #expect(request.header("x-test") == "yes")
        #expect(consumed == raw.count)
    }

    @Test func decodesPercentEncodedPaths() throws {
        let raw = data("GET /lib/My%20Capture/thumb HTTP/1.1\r\n\r\n")
        guard case let .request(request, _) = HTTPRequestParser.parse(raw) else {
            Issue.record("expected a request")
            return
        }
        #expect(request.path == "/lib/My Capture/thumb")
    }

    @Test func waitsForTheWholeHeaderAndBody() {
        #expect(HTTPRequestParser.parse(data("GET / HTTP/1.1\r\nHost: x")) == .needMore)

        let head = "POST /lib/a/strokes HTTP/1.1\r\nContent-Length: 10\r\n\r\n"
        #expect(HTTPRequestParser.parse(data(head + "12345")) == .needMore)

        guard case let .request(request, consumed) = HTTPRequestParser.parse(data(head + "1234567890EXTRA")) else {
            Issue.record("expected a request")
            return
        }
        #expect(request.body == data("1234567890"))
        #expect(consumed == head.utf8.count + 10)
    }

    @Test func rejectsBadRequests() {
        #expect(HTTPRequestParser.parse(data("GARBAGE\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("GET / HTTP/2\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("GET relative HTTP/1.1\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("GET / HTTP/1.1\r\nbroken header\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n")) == .invalid(status: 400))
        #expect(HTTPRequestParser.parse(data("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")) == .invalid(status: 501))
        let tooBig = HTTPRequestParser.maxBodyBytes + 1
        #expect(HTTPRequestParser.parse(data("POST / HTTP/1.1\r\nContent-Length: \(tooBig)\r\n\r\n")) == .invalid(status: 413))
    }

    @Test func oversizedHeadersAreRejected() {
        let huge = "GET / HTTP/1.1\r\nX: " + String(repeating: "a", count: HTTPRequestParser.maxHeaderBytes + 10)
        #expect(HTTPRequestParser.parse(data(huge)) == .invalid(status: 431))
    }

    @Test func parserHandlesDataSlicesWithNonZeroStart() {
        let padded = Data(repeating: 0, count: 5) + data("GET /health HTTP/1.1\r\n\r\n")
        let slice = padded.dropFirst(5)
        guard case let .request(request, consumed) = HTTPRequestParser.parse(slice) else {
            Issue.record("expected a request")
            return
        }
        #expect(request.path == "/health")
        #expect(consumed == slice.count)
    }

    // MARK: HTTP responses

    @Test func responseSerializationAddsLengthAndClose() throws {
        let response = HTTPResponse.text(404, "nope")
        let text = try #require(String(data: response.serialized(), encoding: .utf8))
        #expect(text.hasPrefix("HTTP/1.1 404 Not Found\r\n"))
        #expect(text.contains("Content-Length: 4\r\n"))
        #expect(text.contains("Connection: close\r\n"))
        #expect(text.hasSuffix("\r\n\r\nnope"))

        let head = try #require(String(data: response.serialized(includeBody: false), encoding: .utf8))
        #expect(!head.hasSuffix("nope"))
        #expect(head.contains("Content-Length: 4"))
    }

    @Test func jsonResponsesAreNoStore() throws {
        let response = HTTPResponse.json(RemoteAck(id: "a", inkRevision: 1))
        #expect(response.headers.contains { $0.0 == "Cache-Control" && $0.1 == "no-store" })
        let object = try #require(JSONSerialization.jsonObject(with: response.body) as? [String: Any])
        #expect(object["id"] as? String == "a")
    }

    @Test func callerSuppliedLengthHeadersAreReplaced() throws {
        let response = HTTPResponse(status: 200, headers: [("Content-Length", "999"), ("Connection", "keep-alive")], body: data("ab"))
        let text = try #require(String(data: response.serialized(), encoding: .utf8))
        #expect(!text.contains("999"))
        #expect(!text.contains("keep-alive"))
    }

    // MARK: Static files

    @Test func staticResolverOnlyServesListedFiles() {
        let allowed: Set<String> = ["index.html", "app.js", "sub/x.css"]
        #expect(StaticFileResolver.relativePath(for: "/", allowed: allowed) == "index.html")
        #expect(StaticFileResolver.relativePath(for: "/app.js", allowed: allowed) == "app.js")
        #expect(StaticFileResolver.relativePath(for: "/sub/x.css", allowed: allowed) == "sub/x.css")
        #expect(StaticFileResolver.relativePath(for: "/missing.js", allowed: allowed) == nil)
        #expect(StaticFileResolver.relativePath(for: "/../app.js", allowed: allowed) == nil)
        #expect(StaticFileResolver.relativePath(for: "/sub/../app.js", allowed: allowed) == nil)
        #expect(StaticFileResolver.relativePath(for: "/.env", allowed: allowed) == nil)
        #expect(StaticFileResolver.relativePath(for: "/sub\\x.css", allowed: allowed) == nil)
    }

    @Test func mimeTypes() {
        #expect(MIMEType.forPath("index.html").hasPrefix("text/html"))
        #expect(MIMEType.forPath("app.js").hasPrefix("text/javascript"))
        #expect(MIMEType.forPath("styles.css").hasPrefix("text/css"))
        #expect(MIMEType.forPath("unknown.bin") == "application/octet-stream")
    }

    // MARK: WebSocket handshake

    private func upgradeRequest(key: String = "dGhlIHNhbXBsZSBub25jZQ==", version: String = "13") -> HTTPRequest {
        HTTPRequest(method: "GET", path: "/ws", headers: [
            "upgrade": "websocket", "connection": "keep-alive, Upgrade",
            "sec-websocket-key": key, "sec-websocket-version": version
        ])
    }

    @Test func acceptKeyMatchesRFC6455Example() {
        #expect(WebSocketHandshake.acceptKey(for: "dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")
    }

    @Test func handshakeBuildsA101Response() throws {
        guard case let .upgrade(response) = WebSocketHandshake.respond(to: upgradeRequest()) else {
            Issue.record("expected upgrade")
            return
        }
        let text = try #require(String(data: response, encoding: .utf8))
        #expect(text.hasPrefix("HTTP/1.1 101 Switching Protocols\r\n"))
        #expect(text.contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n"))
        #expect(text.hasSuffix("\r\n\r\n"))
    }

    @Test func handshakeRejectsBadUpgrades() {
        #expect(WebSocketHandshake.respond(to: upgradeRequest(version: "8")) == .reject(status: 400))
        #expect(WebSocketHandshake.respond(to: upgradeRequest(key: "short")) == .reject(status: 400))
        var plain = upgradeRequest()
        plain.headers["upgrade"] = nil
        #expect(WebSocketHandshake.respond(to: plain) == .reject(status: 400))
        var post = upgradeRequest()
        post.method = "POST"
        #expect(WebSocketHandshake.respond(to: post) == .reject(status: 400))
    }

    // MARK: WebSocket frames

    /// Builds a masked client frame.
    private func clientFrame(
        _ opcode: WebSocketOpcode, _ payload: Data, fin: Bool = true, mask: [UInt8] = [0x37, 0xFA, 0x21, 0x3D]
    ) -> Data {
        var frame = Data([(fin ? 0x80 : 0) | opcode.rawValue])
        if payload.count < 126 {
            frame.append(0x80 | UInt8(payload.count))
        } else if payload.count <= 0xFFFF {
            frame.append(0x80 | 126)
            frame.append(UInt8(payload.count >> 8))
            frame.append(UInt8(payload.count & 0xFF))
        } else {
            frame.append(0x80 | 127)
            for shift in stride(from: 56, through: 0, by: -8) { frame.append(UInt8((UInt64(payload.count) >> UInt64(shift)) & 0xFF)) }
        }
        frame.append(contentsOf: mask)
        for (index, byte) in payload.enumerated() { frame.append(byte ^ mask[index & 3]) }
        return frame
    }

    @Test func decodesTheRFCMaskedHelloExample() throws {
        var decoder = WebSocketDecoder()
        decoder.append(Data([0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58]))
        #expect(try decoder.next() == .text(data("Hello")))
        #expect(try decoder.next() == nil)
    }

    @Test func decodesFramesArrivingInPieces() throws {
        let frame = clientFrame(.text, data("split across reads"))
        var decoder = WebSocketDecoder()
        for byte in frame.dropLast() {
            decoder.append(Data([byte]))
            #expect(try decoder.next() == nil)
        }
        decoder.append(Data([frame.last!]))
        #expect(try decoder.next() == .text(data("split across reads")))
    }

    @Test func decodesSeveralFramesFromOneRead() throws {
        var decoder = WebSocketDecoder()
        decoder.append(clientFrame(.text, data("one")) + clientFrame(.binary, Data([1, 2, 3])) + clientFrame(.ping, data("p")))
        #expect(try decoder.next() == .text(data("one")))
        #expect(try decoder.next() == .binary(Data([1, 2, 3])))
        #expect(try decoder.next() == .ping(data("p")))
        #expect(try decoder.next() == nil)
    }

    @Test func extendedLengthsDecode() throws {
        let medium = Data(repeating: 0x41, count: 300)
        let large = Data(repeating: 0x42, count: 70_000)
        var decoder = WebSocketDecoder()
        decoder.append(clientFrame(.binary, medium) + clientFrame(.binary, large))
        #expect(try decoder.next() == .binary(medium))
        #expect(try decoder.next() == .binary(large))
    }

    @Test func fragmentedMessagesAreReassembledAroundControlFrames() throws {
        var decoder = WebSocketDecoder()
        decoder.append(clientFrame(.text, data("Hel"), fin: false))
        decoder.append(clientFrame(.ping, data("!")))
        decoder.append(clientFrame(.continuation, data("lo"), fin: true))
        #expect(try decoder.next() == .ping(data("!")))
        #expect(try decoder.next() == .text(data("Hello")))
    }

    @Test func closeFrameCarriesItsCode() throws {
        var decoder = WebSocketDecoder()
        decoder.append(clientFrame(.close, Data([0x03, 0xE9])))
        #expect(try decoder.next() == .close(code: 1001))
        decoder.append(clientFrame(.close, Data()))
        #expect(try decoder.next() == .close(code: nil))
    }

    @Test func protocolViolationsThrow() {
        func decode(_ bytes: Data, max: Int = 1024) throws -> WebSocketEvent? {
            var decoder = WebSocketDecoder(maxMessageBytes: max)
            decoder.append(bytes)
            return try decoder.next()
        }
        #expect(throws: WebSocketError.unmaskedClientFrame) { try decode(Data([0x81, 0x01, 0x41])) }
        #expect(throws: WebSocketError.reservedBitsSet) { try decode(Data([0xC1, 0x80, 0, 0, 0, 0])) }
        #expect(throws: WebSocketError.invalidOpcode) { try decode(Data([0x83, 0x80, 0, 0, 0, 0])) }
        #expect(throws: WebSocketError.fragmentedControlFrame) { try decode(clientFrame(.ping, Data(), fin: false)) }
        #expect(throws: WebSocketError.controlFrameTooLong) { try decode(clientFrame(.ping, Data(count: 126))) }
        #expect(throws: WebSocketError.unexpectedContinuation) { try decode(clientFrame(.continuation, Data([1]))) }
        #expect(throws: WebSocketError.messageTooLarge) { try decode(clientFrame(.binary, Data(count: 2000)), max: 1000) }
    }

    @Test func newMessageCannotInterruptAFragmentedOne() {
        var decoder = WebSocketDecoder()
        decoder.append(clientFrame(.text, data("a"), fin: false) + clientFrame(.text, data("b")))
        #expect(throws: WebSocketError.interruptedMessage) { try decoder.next() }
    }

    @Test func fragmentsCannotGrowPastTheLimit() {
        var decoder = WebSocketDecoder(maxMessageBytes: 100)
        decoder.append(
            clientFrame(.text, Data(count: 60), fin: false) + clientFrame(.continuation, Data(count: 60), fin: false)
        )
        #expect(throws: WebSocketError.messageTooLarge) { try decoder.next() }
    }

    @Test func serverFramesUseTheShortestLengthForm() {
        #expect(Array(WebSocketFrameEncoder.text(data("hi"))) == [0x81, 2, 0x68, 0x69])
        #expect(Array(WebSocketFrameEncoder.binary(Data(count: 200)).prefix(4)) == [0x82, 126, 0, 200])
        let big = WebSocketFrameEncoder.binary(Data(count: 70_000))
        #expect(Array(big.prefix(10)) == [0x82, 127, 0, 0, 0, 0, 0, 1, 0x11, 0x70])
        #expect(big.count == 70_010)
        #expect(Array(WebSocketFrameEncoder.close(code: 1001)) == [0x88, 2, 0x03, 0xE9])
        #expect(Array(WebSocketFrameEncoder.pong(data("x"))) == [0x8A, 1, 0x78])
    }
}
