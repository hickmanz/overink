import Foundation
import Testing
@testable import OverInkCore

@Suite struct StrokeGeometryTests {
    private func stroke(_ coordinates: [(Double, Double)], width: Double = 4) -> InkStroke {
        InkStroke(
            colorHex: "#000000",
            width: width,
            points: coordinates.map { InkPoint(x: $0.0, y: $0.1) }
        )
    }

    @Test func hitTestUsesSegmentDistanceAndWidth() {
        let line = stroke([(0, 0), (100, 0)], width: 4)

        #expect(StrokeGeometry.hit(line, point: InkPoint(x: 50, y: 7), radius: 6))
        #expect(!StrokeGeometry.hit(line, point: InkPoint(x: 50, y: 9), radius: 6))
        #expect(!StrokeGeometry.hit(line, point: InkPoint(x: 120, y: 0), radius: 6))
    }

    @Test func singlePointStrokeIsHittable() {
        let dot = stroke([(10, 10)], width: 6)
        #expect(StrokeGeometry.hit(dot, point: InkPoint(x: 12, y: 10), radius: 2))
    }

    @Test func emptyStrokeNeverHit() {
        #expect(!StrokeGeometry.hit(stroke([]), point: InkPoint(x: 0, y: 0), radius: 100))
    }

    @Test func rectangleHasClosedOutline() {
        let points = StrokeGeometry.shapePoints(
            tool: .rectangle,
            from: InkPoint(x: 0, y: 0),
            to: InkPoint(x: 10, y: 20)
        )
        #expect(points.count == 5)
        #expect(points.first == points.last)
    }

    @Test func arrowHasHeadFromEndPoint() {
        let points = StrokeGeometry.shapePoints(
            tool: .arrow,
            from: InkPoint(x: 0, y: 0),
            to: InkPoint(x: 100, y: 0)
        )
        #expect(points.count == 5)
        #expect(points[1] == points[3])
        // Head wings sit behind the tip.
        #expect(points[2].x < 100)
        #expect(points[4].x < 100)
    }

    @Test func ellipseIsClosedAndWithinBounds() {
        let points = StrokeGeometry.ellipsePoints(
            from: InkPoint(x: 0, y: 0),
            to: InkPoint(x: 100, y: 50)
        )
        #expect(abs(points[0].x - points[points.count - 1].x) < 1e-9)
        #expect(points.allSatisfy { $0.x >= -1e-9 && $0.x <= 100 + 1e-9 && $0.y >= -1e-9 && $0.y <= 50 + 1e-9 })
    }

    @Test func shiftSnapsLinesToFortyFiveDegrees() {
        let end = StrokeGeometry.constrained(
            tool: .line,
            from: InkPoint(x: 0, y: 0),
            to: InkPoint(x: 100, y: 8)
        )
        #expect(abs(end.y) < 1e-9)
        #expect(abs(end.x - hypot(100, 8)) < 1e-9)

        let diagonal = StrokeGeometry.constrained(
            tool: .arrow,
            from: InkPoint(x: 0, y: 0),
            to: InkPoint(x: 50, y: 60)
        )
        #expect(abs(diagonal.x - diagonal.y) < 1e-9)
    }

    @Test func shiftMakesRectanglesAndEllipsesSquare() {
        let end = StrokeGeometry.constrained(
            tool: .rectangle,
            from: InkPoint(x: 10, y: 10),
            to: InkPoint(x: -30, y: 15)
        )
        #expect(end.x == -30)
        #expect(end.y == 50)
    }

    @Test func cropTranslatesAndDropsOutsideStrokes() {
        let inside = stroke([(110, 120), (150, 160)])
        let outside = stroke([(500, 500), (520, 520)])
        let region = PixelRect(x: 100, y: 100, width: 100, height: 100)

        let cropped = StrokeGeometry.crop([inside, outside], to: region)

        #expect(cropped.count == 1)
        #expect(cropped[0].points.first?.x == 10)
        #expect(cropped[0].points.first?.y == 20)
    }

    @Test func cropKeepsStrokesStraddlingTheEdge() {
        let straddling = stroke([(90, 150), (110, 150)])
        let cropped = StrokeGeometry.crop([straddling], to: PixelRect(x: 100, y: 100, width: 100, height: 100))
        #expect(cropped.count == 1)
    }

    @Test func boundsPadByHalfWidth() throws {
        let box = try #require(StrokeGeometry.paddedBounds(of: stroke([(10, 10), (30, 20)], width: 4)))
        #expect(box == PixelRect(x: 8, y: 8, width: 24, height: 14))
    }
}

@Suite struct DrawShortcutMapTests {
    @Test func everyToolHasAUniqueLetter() {
        let letters = DrawShortcutMap.entries.map(\.letter)
        #expect(Set(letters).count == letters.count)
        #expect(Set(DrawShortcutMap.entries.map(\.tool)) == Set(SketchTool.allCases))
    }

    @Test func lookupsRoundTrip() {
        #expect(DrawShortcutMap.tool(forKeyCode: KeyCodes.p) == .pen)
        #expect(DrawShortcutMap.tool(forLetter: "o") == .ellipse)
        #expect(DrawShortcutMap.letter(for: .eraser) == "E")
        #expect(DrawShortcutMap.tool(forKeyCode: KeyCodes.z) == nil)
    }
}
