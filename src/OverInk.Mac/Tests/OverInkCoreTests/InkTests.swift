import Foundation
import Testing
@testable import OverInkCore

@Suite struct InkTests {
    @Test func inkDocumentRoundTrips() throws {
        let stroke = InkStroke(
            id: UUID(uuidString: "D218537D-6F1E-48A4-B548-423BFE77E15A")!,
            colorHex: "#E53935",
            width: 4,
            points: [
                InkPoint(x: 10, y: 20, pressure: 0.25),
                InkPoint(x: 30, y: 40, pressure: 0.75)
            ]
        )
        let document = InkDocument(strokes: [stroke])

        let data = try JSONEncoder().encode(document)
        #expect(try JSONDecoder().decode(InkDocument.self, from: data) == document)
    }

    @Test func historyInvalidatesRedoAfterNewStroke() {
        var history = InkHistory()
        let first = InkStroke(colorHex: "#000000", width: 2)
        let replacement = InkStroke(colorHex: "#FFFFFF", width: 3)

        history.append(first)
        #expect(history.undo() == first)
        history.append(replacement)

        #expect(history.redo() == nil)
        #expect(history.strokes == [replacement])
    }

    @Test func editableCaptureDocumentRoundTrips() throws {
        let document = CaptureDocument(
            canvasWidth: 1440,
            canvasHeight: 900,
            veilOpacity: 0.25,
            strokes: [InkStroke(colorHex: "#E53935", width: 4)]
        )

        let data = try JSONEncoder().encode(document)
        #expect(try JSONDecoder().decode(CaptureDocument.self, from: data) == document)
    }
}
