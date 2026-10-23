import Foundation
import OverInkCore

enum CheckFailure: Error, CustomStringConvertible {
    case failed(String)

    var description: String {
        switch self {
        case let .failed(message): message
        }
    }
}

func require(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    guard try condition() else {
        throw CheckFailure.failed(message)
    }
}

func runChecks() throws {
    let monitors = [
        MonitorGeometry(
            id: "left",
            pixels: PixelRect(x: -1280, y: 0, width: 1280, height: 1024),
            points: PixelRect(x: -1280, y: 0, width: 1280, height: 1024),
            scaleX: 1,
            scaleY: 1,
            isPrimary: false
        ),
        MonitorGeometry(
            id: "primary",
            pixels: PixelRect(x: 0, y: 0, width: 2560, height: 1440),
            points: PixelRect(x: 0, y: 0, width: 1280, height: 720),
            scaleX: 2,
            scaleY: 2,
            isPrimary: true
        )
    ]
    try require(
        DesktopLayout.virtualDesktop(monitors)
            == PixelRect(x: -1280, y: 0, width: 3840, height: 1440),
        "Virtual desktop union changed"
    )

    let area = PixelRect(x: 100, y: 200, width: 800, height: 600)
    try require(
        DesktopLayout.normalizedToPixels(PixelPoint(x: -1, y: 2), in: area)
            == PixelPoint(x: 100, y: 800),
        "Normalized coordinates are not clamped"
    )

    let hello = RemoteHello(
        deviceKey: "device-key",
        pair: "123456",
        deviceName: "MacBook Pro"
    )
    let helloData = try RemoteJSON.wrap("hello", payload: hello)
    let helloObject = try JSONSerialization.jsonObject(with: helloData) as? [String: Any]
    try require(helloObject?["t"] as? String == "hello", "Remote envelope type changed")
    try require(helloObject?["deviceKey"] as? String == "device-key", "Remote camelCase shape changed")

    let stroke = InkStroke(
        colorHex: "#E53935",
        width: 4,
        points: [InkPoint(x: 10, y: 20, pressure: 0.5)]
    )
    let document = InkDocument(strokes: [stroke])
    let documentData = try JSONEncoder().encode(document)
    try require(
        try JSONDecoder().decode(InkDocument.self, from: documentData) == document,
        "Ink document did not round-trip"
    )

    let capture = CaptureDocument(
        canvasWidth: 1440,
        canvasHeight: 900,
        veilOpacity: 0.3,
        strokes: [stroke]
    )
    let captureData = try JSONEncoder().encode(capture)
    try require(
        try JSONDecoder().decode(CaptureDocument.self, from: captureData) == capture,
        "Editable capture document did not round-trip"
    )
}

do {
    try runChecks()
    print("OverInkCoreChecks: all checks passed")
} catch {
    fputs("OverInkCoreChecks: \(error)\n", stderr)
    exit(1)
}
