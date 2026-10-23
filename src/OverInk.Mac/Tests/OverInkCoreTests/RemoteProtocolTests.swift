import Foundation
import Testing
@testable import OverInkCore

@Suite struct RemoteProtocolTests {
    // MARK: Frames

    @Test func frameRoundTripsHeaderAndPayload() throws {
        let jpeg = Data([0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9])
        let header = RemoteFrameHeader(sequence: 0xDEADBEEF, inkRevision: 1_234_567_890_123, width: 3840, height: 2160)
        let data = RemoteFrame.encode(header: header, jpeg: jpeg)

        #expect(data.count == RemoteProtocol.frameHeaderSize + jpeg.count)
        let decoded = try #require(RemoteFrame.decode(data))
        #expect(decoded.header == header)
        #expect(decoded.jpeg == jpeg)
        #expect(decoded.jpeg.startIndex == 0)
    }

    @Test func frameLayoutMatchesWebClientOffsets() throws {
        // app.js reads: type @0, inkRevision i64 LE @5, width u16 LE @13, height u16 LE @15, JPEG @17.
        let data = RemoteFrame.encode(
            header: RemoteFrameHeader(sequence: 1, inkRevision: 0x0102, width: 0x0304, height: 0x0506),
            jpeg: Data([0xFF, 0xD8])
        )
        let bytes = [UInt8](data)
        #expect(bytes[0] == 1)
        #expect(Array(bytes[1 ..< 5]) == [1, 0, 0, 0])
        #expect(Array(bytes[5 ..< 13]) == [0x02, 0x01, 0, 0, 0, 0, 0, 0])
        #expect(Array(bytes[13 ..< 15]) == [0x04, 0x03])
        #expect(Array(bytes[15 ..< 17]) == [0x06, 0x05])
        #expect(Array(bytes[17...]) == [0xFF, 0xD8])
    }

    @Test func frameDecodeRejectsShortOrWrongType() {
        #expect(RemoteFrame.decode(Data(count: 16)) == nil)
        var wrong = Data(count: 20)
        wrong[0] = 2
        #expect(RemoteFrame.decode(wrong) == nil)
    }

    @Test func negativeInkRevisionSurvivesRoundTrip() throws {
        let header = RemoteFrameHeader(sequence: 0, inkRevision: -5, width: 1, height: 1)
        let decoded = try #require(RemoteFrame.decode(RemoteFrame.encode(header: header, jpeg: Data([0xFF]))))
        #expect(decoded.header.inkRevision == -5)
    }

    // MARK: Wrapping

    @Test func wrapPutsTypeFirstAndKeepsPayloadKeys() throws {
        let data = try RemoteJSON.wrap("ack", payload: RemoteAck(id: "abc", inkRevision: 7))
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.hasPrefix(##"{"t":"ack","##))
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["id"] as? String == "abc")
        #expect(object["inkRevision"] as? Int == 7)
    }

    @Test func wrapOfEmptyPayloadIsJustTheType() throws {
        let text = try #require(String(data: RemoteJSON.wrap("cleared"), encoding: .utf8))
        #expect(text == ##"{"t":"cleared"}"##)
    }

    @Test func wrapKeepsFloatsCompact() throws {
        let data = try RemoteJSON.wrap(
            "peerStrokePoints",
            payload: RemotePeerStrokePoints(author: "a", id: "b", pts: [0.25, 0.5, 1])
        )
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.contains("[0.25,0.5,1]"))
    }

    @Test func welcomeOmitsAbsentOptionals() throws {
        let welcome = RemoteWelcome(
            clientId: "c1",
            screenW: 1440,
            screenH: 900,
            state: RemoteState(tool: "Pen", color: "#E53935", size: 4, mode: "Draw", screenW: 1440, screenH: 900, regionActive: false),
            inkRevision: 3
        )
        let object = try #require(
            JSONSerialization.jsonObject(with: RemoteJSON.wrap("welcome", payload: welcome)) as? [String: Any]
        )
        #expect(object["t"] as? String == "welcome")
        #expect(object["v"] as? Int == 1)
        #expect(object["deviceKey"] == nil)
        #expect(object["libToken"] == nil)
        #expect((object["state"] as? [String: Any])?["tool"] as? String == "Pen")
    }

    @Test func pongEchoesLargeTimestampsExactly() throws {
        let data = try RemoteJSON.wrap("pong", payload: RemotePong(ms: 1_700_000_000_123))
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text.contains("1700000000123"))
    }

    @Test func wireIDsAreLowercase() {
        let id = UUID()
        #expect(RemoteID.wire(id) == id.uuidString.lowercased())
        #expect(RemoteID.uuid(RemoteID.wire(id)) == id)
    }

    // MARK: Client messages

    private func decode(_ json: String) -> RemoteClientMessage? {
        RemoteClientMessage.decode(Data(json.utf8))
    }

    @Test func decodesStrokeLifecycle() {
        #expect(decode(##"{"t":"strokeBegin","id":"a","color":"#112233","size":4,"x":0.1,"y":0.2,"p":0.5}"##)
            == .strokeBegin(RemoteStrokeBegin(id: "a", color: "#112233", size: 4, x: 0.1, y: 0.2, p: 0.5)))
        #expect(decode(##"{"t":"strokePoints","id":"a","pts":[0.1,0.2,0.5,0.3,0.4,0.5]}"##)
            == .strokePoints(RemoteStrokePoints(id: "a", pts: [0.1, 0.2, 0.5, 0.3, 0.4, 0.5])))
        #expect(decode(##"{"t":"strokeEnd","id":"a"}"##) == .strokeEnd(id: "a"))
        #expect(decode(##"{"t":"strokeCancel","id":"a"}"##) == .strokeCancel(id: "a"))
    }

    @Test func strokeBeginDefaultsMissingPressureAndSize() throws {
        guard case let .strokeBegin(begin)? = decode(##"{"t":"strokeBegin","id":"a","x":0,"y":1}"##) else {
            Issue.record("expected strokeBegin")
            return
        }
        #expect(begin.p == 1)
        #expect(begin.size == 0)
    }

    @Test func decodesShapesAndErase() {
        let shape = RemoteShape(id: "s", tool: "Arrow", x1: 0.1, y1: 0.2, x2: 0.3, y2: 0.4, color: "#FF0000", size: 6)
        #expect(decode(##"{"t":"shapeCommit","id":"s","tool":"Arrow","x1":0.1,"y1":0.2,"x2":0.3,"y2":0.4,"color":"#FF0000","size":6}"##)
            == .shapeCommit(shape))
        #expect(decode(##"{"t":"erase","pts":[0.5,0.5],"size":20,"whole":true}"##)
            == .erase(RemoteErase(pts: [0.5, 0.5], size: 20, whole: true)))
        #expect(decode(##"{"t":"erase","pts":[0.5,0.5],"size":20}"##)
            == .erase(RemoteErase(pts: [0.5, 0.5], size: 20, whole: nil)))
    }

    @Test func decodesSimpleCommands() {
        #expect(decode(##"{"t":"undo"}"##) == .undo)
        #expect(decode(##"{"t":"redo"}"##) == .redo)
        #expect(decode(##"{"t":"clear"}"##) == .clear)
        #expect(decode(##"{"t":"capture"}"##) == .capture)
        #expect(decode(##"{"t":"setTool","tool":"Eraser"}"##) == .setTool("Eraser"))
        #expect(decode(##"{"t":"setSize","size":8}"##) == .setSize(8))
        #expect(decode(##"{"t":"setPressure","enabled":false}"##) == .setPressure(false))
        #expect(decode(##"{"t":"setVeil","value":0.3}"##) == .setVeil(0.3))
        #expect(decode(##"{"t":"saveColor","color":"#00FF00"}"##) == .saveColor("#00FF00"))
        #expect(decode(##"{"t":"removeColor","color":"#00FF00"}"##) == .removeColor("#00FF00"))
        #expect(decode(##"{"t":"ping","ms":1700000000123}"##) == .ping(ms: 1_700_000_000_123))
        #expect(decode(##"{"t":"viewMode","mode":"doc"}"##) == .viewMode(doc: true))
        #expect(decode(##"{"t":"viewMode","mode":"live"}"##) == .viewMode(doc: false))
        #expect(decode(##"{"t":"hover","clientId":"c","x":0.5,"y":0.5}"##)
            == .hover(RemoteHover(clientId: "c", x: 0.5, y: 0.5)))
    }

    @Test func rejectsUnknownMalformedAndOversizedMessages() {
        #expect(decode(##"{"t":"nope"}"##) == nil)
        #expect(decode(##"{"id":"a"}"##) == nil)
        #expect(decode("not json") == nil)
        #expect(decode(##"{"t":"strokeBegin","id":"a"}"##) == nil)
        #expect(decode(##"{"t":"erase","pts":[0.5],"size":20}"##) == nil)
        #expect(decode(##"{"t":"setTool"}"##) == nil)

        let tooMany = (0 ... RemoteProtocol.maxPointValuesPerMessage).map { _ in "0" }.joined(separator: ",")
        #expect(decode(##"{"t":"strokePoints","id":"a","pts":[\##(tooMany)]}"##) == nil)

        let huge = Data(repeating: UInt8(ascii: " "), count: RemoteProtocol.maxClientMessageBytes + 1)
        #expect(RemoteClientMessage.decode(huge) == nil)
    }

    @Test func helloDecodesWithNullOptionals() throws {
        let json = ##"{"t":"hello","v":1,"deviceKey":null,"pair":"p","join":null,"deviceName":"iPad"}"##
        let hello = try RemoteJSON.decoder().decode(RemoteHello.self, from: Data(json.utf8))
        #expect(hello.pair == "p")
        #expect(hello.deviceKey == nil)
        #expect(hello.deviceName == "iPad")
    }
}
