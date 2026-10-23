import Foundation
import Testing
@testable import OverInkCore

@Suite struct SelectionOpsTests {
    private func line(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double, group: UUID? = nil) -> InkStroke {
        InkStroke(
            colorHex: "#FF0000",
            width: 4,
            points: [InkPoint(x: x1, y: y1), InkPoint(x: x2, y: y2)],
            groupId: group
        )
    }

    @Test func pickReturnsTopmost() {
        let below = line(0, 0, 100, 0)
        let above = line(0, 0, 100, 0)
        let picked = SelectionOps.pick(at: InkPoint(x: 50, y: 1), in: [below, above], tolerance: 5)
        #expect(picked?.id == above.id)
        #expect(SelectionOps.pick(at: InkPoint(x: 50, y: 40), in: [below], tolerance: 5) == nil)
    }

    @Test func marqueeSelectsIntersectingStrokes() {
        let inside = line(10, 10, 20, 20)
        let outside = line(300, 300, 320, 320)
        let ids = SelectionOps.strokes(
            in: PixelRect(x: 0, y: 0, width: 100, height: 100),
            from: [inside, outside]
        )
        #expect(ids == [inside.id])
    }

    @Test func groupsExpandSelection() {
        let group = UUID()
        let a = line(0, 0, 10, 10, group: group)
        let b = line(50, 50, 60, 60, group: group)
        let c = line(100, 100, 110, 110)
        let expanded = SelectionOps.expandGroups([a.id], in: [a, b, c])
        #expect(expanded == [a.id, b.id])
        #expect(SelectionOps.expandGroups([c.id], in: [a, b, c]) == [c.id])
    }

    @Test func boundsCoverSelectedStrokes() throws {
        let a = line(0, 0, 10, 10)
        let b = line(100, 100, 120, 130)
        let box = try #require(SelectionOps.bounds(of: [a.id, b.id], in: [a, b]))
        #expect(box == PixelRect(x: 0, y: 0, width: 120, height: 130))
        #expect(SelectionOps.bounds(of: [], in: [a, b]) == nil)
    }

    @Test func boundsOfStraightLineKeepZeroHeight() throws {
        let flat = line(0, 10, 50, 10)
        let box = try #require(SelectionOps.bounds(of: [flat.id], in: [flat]))
        #expect(box == PixelRect(x: 0, y: 10, width: 50, height: 0))
    }

    @Test func scalingAFlatLineOnlyStretchesHorizontally() {
        let flat = line(0, 10, 50, 10)
        let old = PixelRect(x: 0, y: 10, width: 50, height: 0)
        let new = PixelRect(x: 0, y: 20, width: 100, height: 8)
        let scaled = SelectionOps.scale([flat], from: old, to: new)
        #expect(scaled[0].points.map(\.x) == [0, 100])
        #expect(scaled[0].points.map(\.y) == [20, 20])
    }

    @Test func moveShiftsEveryPoint() {
        let moved = SelectionOps.move([line(0, 0, 10, 10)], dx: 5, dy: -3)
        #expect(moved[0].points.map(\.x) == [5, 15])
        #expect(moved[0].points.map(\.y) == [-3, 7])
    }

    @Test func scaleMapsBetweenBounds() {
        let stroke = line(10, 10, 30, 30)
        let old = PixelRect(x: 10, y: 10, width: 20, height: 20)
        let new = PixelRect(x: 0, y: 0, width: 40, height: 10)
        let scaled = SelectionOps.scale([stroke], from: old, to: new)
        #expect(scaled[0].points.map(\.x) == [0, 40])
        #expect(scaled[0].points.map(\.y) == [0, 10])
        #expect(scaled[0].id == stroke.id)
        #expect(scaled[0].width == stroke.width)
    }

    @Test func recolorKeepsGeometry() {
        let stroke = line(0, 0, 10, 10)
        let recolored = SelectionOps.recolor([stroke], hex: "#00FF00")
        #expect(recolored[0].colorHex == "#00FF00")
        #expect(recolored[0].points == stroke.points)
    }

    @Test func handleHitTesting() {
        let box = PixelRect(x: 0, y: 0, width: 100, height: 50)
        #expect(SelectionOps.handle(at: PixelPoint(x: 99, y: 49), on: box, radius: 6) == .bottomRight)
        #expect(SelectionOps.handle(at: PixelPoint(x: 50, y: 1), on: box, radius: 6) == .top)
        #expect(SelectionOps.handle(at: PixelPoint(x: 50, y: 25), on: box, radius: 6) == nil)
    }

    @Test func resizeEdgesAndCorners() {
        let box = PixelRect(x: 0, y: 0, width: 100, height: 50)
        let right = SelectionOps.resized(box, dragging: .right, to: PixelPoint(x: 150, y: 999))
        #expect(right == PixelRect(x: 0, y: 0, width: 150, height: 50))

        let topLeft = SelectionOps.resized(box, dragging: .topLeft, to: PixelPoint(x: 20, y: 10))
        #expect(topLeft == PixelRect(x: 20, y: 10, width: 80, height: 40))
    }

    @Test func resizeCannotCollapse() {
        let box = PixelRect(x: 0, y: 0, width: 100, height: 50)
        let squashed = SelectionOps.resized(box, dragging: .right, to: PixelPoint(x: -50, y: 0), minimumSize: 4)
        #expect(squashed.width == 4)
    }

    @Test func shiftKeepsAspectOnCorners() {
        let box = PixelRect(x: 0, y: 0, width: 100, height: 50)
        let resized = SelectionOps.resized(
            box, dragging: .bottomRight, to: PixelPoint(x: 200, y: 60), keepAspect: true
        )
        #expect(abs(resized.width / resized.height - 2) < 0.0001)
        #expect(resized.minX == 0 && resized.minY == 0)
    }
}
