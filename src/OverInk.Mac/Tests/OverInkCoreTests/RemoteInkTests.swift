import Foundation
import Testing
@testable import OverInkCore

@Suite struct RemoteInkTests {
    // Two displays side by side; the stream covers both.
    private let left = CaptureDisplay(id: 1, bounds: PixelRect(x: 0, y: 0, width: 1000, height: 600))
    private let right = CaptureDisplay(id: 2, bounds: PixelRect(x: 1000, y: 0, width: 1000, height: 600))
    private var mapper: RemoteInkMapper {
        RemoteInkMapper(stream: PixelRect(x: 0, y: 0, width: 2000, height: 600), displays: [left, right])
    }

    // MARK: Mapper

    @Test func mapsFractionsToDisplaysAndLocalPoints() throws {
        #expect(mapper.display(x: 0.25, y: 0.5)?.id == 1)
        #expect(mapper.display(x: 0.75, y: 0.5)?.id == 2)

        let local = mapper.localPoint(x: 0.75, y: 0.5, on: right)
        #expect(local == PixelPoint(x: 500, y: 300))
    }

    @Test func fractionsOutsideTheStreamAreClamped() {
        #expect(mapper.globalPoint(x: -3, y: 9) == PixelPoint(x: 0, y: 600))
    }

    @Test func strokeSnapshotsRoundTripThroughTheMapper() throws {
        let stroke = InkStroke(
            colorHex: "#112233", width: 5,
            points: [InkPoint(x: 100, y: 60, pressure: 0.5), InkPoint(x: 900, y: 540, pressure: 1)],
            ignoresPressure: true
        )
        let snapshot = mapper.snapshot(of: stroke, on: right)
        #expect(snapshot.id == stroke.id.uuidString.lowercased())
        #expect(snapshot.color == "#112233")
        #expect(snapshot.size == 5)
        #expect(snapshot.ignorePressure)
        #expect(snapshot.pts.count == 6)
        // x = (100 + 1000) / 2000 on the right display.
        #expect(abs(Double(snapshot.pts[0]) - 0.55) < 1e-6)
        #expect(abs(Double(snapshot.pts[1]) - 0.1) < 1e-6)
        #expect(snapshot.pts[2] == 0.5)

        let back = mapper.localPoint(x: Double(snapshot.pts[0]), y: Double(snapshot.pts[1]), on: right)
        #expect(abs(back.x - 100) < 0.01 && abs(back.y - 60) < 0.01)
    }

    @Test func regionStreamsMapRelativeToTheRegion() {
        let region = RemoteInkMapper(
            stream: PixelRect(x: 200, y: 100, width: 400, height: 200), displays: [left, right]
        )
        #expect(region.localPoint(x: 0.5, y: 0.5, on: left) == PixelPoint(x: 400, y: 200))
        let n = region.normalized(local: PixelPoint(x: 400, y: 200), on: left)
        #expect(n == PixelPoint(x: 0.5, y: 0.5))
    }

    // MARK: Eraser

    private func line(width: Double = 4, from x1: Double, to x2: Double, y: Double = 100, step: Double = 10) -> InkStroke {
        var points: [InkPoint] = []
        var x = x1
        while x <= x2 {
            points.append(InkPoint(x: x, y: y))
            x += step
        }
        return InkStroke(colorHex: "#E53935", width: width, points: points)
    }

    @Test func wholeEraseRemovesTheStrokeOutright() {
        let stroke = line(from: 0, to: 200)
        let result = StrokeEraser.erase(
            strokes: [stroke], path: [PixelPoint(x: 100, y: 100)], diameter: 10, whole: true
        )
        #expect(result.removed == [stroke.id])
        #expect(result.added.isEmpty)
    }

    @Test func missesLeaveStrokesAlone() {
        let stroke = line(from: 0, to: 200)
        let result = StrokeEraser.erase(
            strokes: [stroke], path: [PixelPoint(x: 100, y: 160)], diameter: 10, whole: false
        )
        #expect(result.isEmpty)
        #expect(result.added.isEmpty)
    }

    @Test func partialEraseSplitsAStrokeIntoTwoPieces() throws {
        let stroke = line(from: 0, to: 200)
        var counter = 0
        let result = StrokeEraser.erase(
            strokes: [stroke], path: [PixelPoint(x: 100, y: 100)], diameter: 20, whole: false,
            makeID: { counter += 1; return UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", counter))! }
        )
        #expect(result.removed == [stroke.id])
        #expect(result.added.count == 2)
        let (first, second) = (result.added[0], result.added[1])
        #expect(first.id != stroke.id && second.id != stroke.id && first.id != second.id)
        #expect(first.colorHex == stroke.colorHex && first.width == stroke.width)
        #expect(first.points.last!.x < 100 && second.points.first!.x > 100)
        // Reach is diameter/2 + width/2 = 12: nothing within 12 of x = 100 survives.
        for piece in result.added {
            for point in piece.points { #expect(abs(point.x - 100) > 12 - 1e-9) }
        }
    }

    @Test func eraserSweepCutsEveryStrokeItCrosses() {
        let a = line(from: 0, to: 200, y: 100)
        let b = line(from: 0, to: 200, y: 140)
        let c = line(from: 0, to: 200, y: 300)
        let result = StrokeEraser.erase(
            strokes: [a, b, c],
            path: [PixelPoint(x: 100, y: 80), PixelPoint(x: 100, y: 160)],
            diameter: 10, whole: false
        )
        #expect(Set(result.removed) == [a.id, b.id])
        #expect(result.added.count == 4)
    }

    @Test func straightLineShapesCanBeCutInTheMiddle() {
        // A line shape has just two points; the eraser only touches the middle of the segment.
        let shape = InkStroke(
            colorHex: "#000000", width: 4,
            points: [InkPoint(x: 0, y: 0), InkPoint(x: 400, y: 0)]
        )
        let result = StrokeEraser.erase(
            strokes: [shape], path: [PixelPoint(x: 200, y: 0)], diameter: 20, whole: false
        )
        #expect(result.removed == [shape.id])
        #expect(result.added.count == 2)
        #expect(result.added[0].points.last!.x < 200)
        #expect(result.added[1].points.first!.x > 200)
    }

    @Test func crossingAShapeBetweenItsPointsStillHits() {
        let shape = InkStroke(
            colorHex: "#000000", width: 4,
            points: [InkPoint(x: 0, y: 0), InkPoint(x: 400, y: 0)]
        )
        let result = StrokeEraser.erase(
            strokes: [shape],
            path: [PixelPoint(x: 200, y: -50), PixelPoint(x: 200, y: 50)],
            diameter: 4, whole: true
        )
        #expect(result.removed == [shape.id])
    }

    @Test func tinyLeftoversAreDropped() {
        // Width 40 -> pieces shorter than 30 are dropped. Points 40...80 are hit; the left run
        // (0...30) is exactly 30 long and stays, the right run (90, 100) is only 10 long.
        let stroke = line(width: 40, from: 0, to: 100, step: 10)
        let result = StrokeEraser.erase(
            strokes: [stroke], path: [PixelPoint(x: 60, y: 100)], diameter: 2, whole: false
        )
        #expect(result.removed == [stroke.id])
        #expect(result.added.count == 1)
        #expect(result.added[0].points.first?.x == 0)
        #expect(result.added[0].points.last?.x == 30)
    }

    @Test func dotsAreErasedByATap() {
        let dot = InkStroke(colorHex: "#000000", width: 6, points: [InkPoint(x: 50, y: 50)])
        let hit = StrokeEraser.erase(strokes: [dot], path: [PixelPoint(x: 52, y: 52)], diameter: 4, whole: false)
        #expect(hit.removed == [dot.id])
        #expect(hit.added.isEmpty)
        let miss = StrokeEraser.erase(strokes: [dot], path: [PixelPoint(x: 90, y: 90)], diameter: 4, whole: false)
        #expect(miss.isEmpty)
    }

    @Test func degenerateInputsAreNoOps() {
        let stroke = line(from: 0, to: 100)
        #expect(StrokeEraser.erase(strokes: [stroke], path: [], diameter: 10, whole: false).isEmpty)
        #expect(StrokeEraser.erase(strokes: [stroke], path: [PixelPoint(x: 0, y: 100)], diameter: 0, whole: false).isEmpty)
        #expect(StrokeEraser.erase(strokes: [], path: [PixelPoint(x: 0, y: 0)], diameter: 10, whole: true).isEmpty)
    }

    @Test func piecesKeepPressureAndGroup() {
        let group = UUID()
        var stroke = line(from: 0, to: 200)
        stroke.groupId = group
        stroke.ignoresPressure = true
        let result = StrokeEraser.erase(
            strokes: [stroke], path: [PixelPoint(x: 100, y: 100)], diameter: 10, whole: false
        )
        #expect(result.added.allSatisfy { $0.groupId == group && $0.ignoresPressure })
    }

    // MARK: EditHistory.edit

    private struct Item: Identifiable, Equatable, Sendable {
        var id: Int
    }

    @Test func removeAndAddIsOneUndoStep() {
        var history = EditHistory<Item>()
        history.append(Item(id: 1))
        history.append(Item(id: 2))
        history.append(Item(id: 3))

        let removed = history.edit(removing: [2], adding: [Item(id: 20), Item(id: 21)], author: "ipad")
        #expect(removed == [Item(id: 2)])
        #expect(history.items.map(\.id) == [1, 3, 20, 21])

        let undone = history.undo(author: "ipad")
        #expect(undone)
        #expect(history.items.map(\.id) == [1, 2, 3])
        let redone = history.redo(author: "ipad")
        #expect(redone)
        #expect(history.items.map(\.id) == [1, 3, 20, 21])
    }

    @Test func editWithNothingToDoRecordsNothing() {
        var history = EditHistory<Item>()
        history.append(Item(id: 1))
        let removed = history.edit(removing: [9], adding: [])
        #expect(removed.isEmpty)
        let undone = history.undo()
        #expect(undone)
        #expect(!history.canUndo)
    }

    @Test func authorsUndoOnlyTheirOwnEdits() {
        var history = EditHistory<Item>()
        history.append(Item(id: 1), author: "a")
        history.append(Item(id: 2), author: "b")
        history.append(Item(id: 3), author: "a")

        let firstB = history.undo(author: "b")
        #expect(firstB)
        #expect(history.items.map(\.id) == [1, 3])
        let lastA = history.undo(author: "a")
        #expect(lastA)
        #expect(history.items.map(\.id) == [1])
        let secondB = history.undo(author: "b")
        #expect(!secondB)
    }
}
