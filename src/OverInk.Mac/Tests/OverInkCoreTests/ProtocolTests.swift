import Foundation
import XCTest
@testable import OverInkCore

final class ProtocolTests: XCTestCase {
    func testHelloMatchesWindowsCamelCaseShape() throws {
        let hello = RemoteHello(
            deviceKey: "device-key",
            pair: "123456",
            deviceName: "MacBook Pro"
        )
        let data = try RemoteJSON.encoder().encode(hello)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["v"] as? Int, 1)
        XCTAssertEqual(object["deviceKey"] as? String, "device-key")
        XCTAssertEqual(object["pair"] as? String, "123456")
        XCTAssertEqual(object["deviceName"] as? String, "MacBook Pro")
    }

    func testWrappedMessageUsesFlatTypeDiscriminator() throws {
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
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["t"] as? String, "state")
        XCTAssertEqual(object["tool"] as? String, "pen")
        XCTAssertEqual(object["pressureSensitive"] as? Bool, true)
        XCTAssertEqual(object["streamEpoch"] as? Int, 0)
        XCTAssertEqual(try RemoteJSON.messageType(in: data), "state")
    }
}
