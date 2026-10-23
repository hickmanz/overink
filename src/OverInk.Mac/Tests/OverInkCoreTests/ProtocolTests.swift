import Foundation
import Testing
@testable import OverInkCore

@Suite struct ProtocolTests {
    @Test func helloMatchesWindowsCamelCaseShape() throws {
        let hello = RemoteHello(
            deviceKey: "device-key",
            pair: "123456",
            deviceName: "MacBook Pro"
        )
        let data = try RemoteJSON.encoder().encode(hello)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])

        #expect(object["v"] as? Int == 1)
        #expect(object["deviceKey"] as? String == "device-key")
        #expect(object["pair"] as? String == "123456")
        #expect(object["deviceName"] as? String == "MacBook Pro")
    }

    @Test func wrappedMessageUsesFlatTypeDiscriminator() throws {
        let state = RemoteState(
            tool: "pen",
            color: "#E53935",
            size: 4,
            mode: "draw",
            screenW: 1920,
            screenH: 1080,
            regionActive: false
        )

        let data = try RemoteJSON.wrap("state", payload: state)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])

        #expect(object["t"] as? String == "state")
        #expect(object["tool"] as? String == "pen")
        #expect(object["pressureSensitive"] as? Bool == true)
        #expect(object["streamEpoch"] as? Int == 0)
        #expect(try RemoteJSON.messageType(in: data) == "state")
    }
}
