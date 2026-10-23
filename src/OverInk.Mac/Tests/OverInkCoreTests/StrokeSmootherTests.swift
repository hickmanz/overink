import Foundation
import Testing
@testable import OverInkCore

@Suite struct StrokeSmootherTests {
    @Test func shortStrokesAreLeftAlone() {
        let points = [InkPoint(x: 0, y: 0), InkPoint(x: 5, y: 5), InkPoint(x: 10, y: 0)]
        #expect(StrokeSmoother.smooth(points, penWidth: 4) == nil)
    }

    @Test func endpointsStayPinned() throws {
        let points = noisyLine()
        let smoothed = try #require(StrokeSmoother.smooth(points, penWidth: 4))

        #expect(abs(smoothed[0].x - points[0].x) < 1e-6)
        #expect(abs(smoothed[0].y - points[0].y) < 1e-6)
        let last = try #require(points.last)
        let smoothedLast = try #require(smoothed.last)
        #expect(abs(smoothedLast.x - last.x) < 1e-6)
        #expect(abs(smoothedLast.y - last.y) < 1e-6)
    }

    @Test func smoothingReducesJitterWithoutLeavingThePath() throws {
        let points = noisyLine()
        let smoothed = try #require(StrokeSmoother.smooth(points, penWidth: 4))

        let rawRoughness = roughness(points)
        let smoothRoughness = roughness(smoothed)
        #expect(smoothRoughness < rawRoughness)

        // The underlying line is y = 50; deviation is bounded by the jitter amplitude.
        for point in smoothed {
            #expect(abs(point.y - 50) <= 3.5)
        }
    }

    @Test func pressureStaysNormalized() throws {
        var points = noisyLine()
        for index in points.indices {
            points[index].pressure = Float(index % 2 == 0 ? 0 : 1)
        }
        let smoothed = try #require(StrokeSmoother.smooth(points, penWidth: 4))
        #expect(smoothed.allSatisfy { $0.pressure >= 0 && $0.pressure <= 1 })
    }

    @Test func applyReplacesStrokePoints() {
        var stroke = InkStroke(colorHex: "#000000", width: 4, points: noisyLine())
        let before = stroke.points.count
        StrokeSmoother.apply(to: &stroke)
        #expect(stroke.points.count != before)
    }

    /// A horizontal line at y = 50 with +/- 2px alternating jitter.
    private func noisyLine() -> [InkPoint] {
        (0 ..< 40).map { index in
            InkPoint(x: Double(index) * 4, y: 50 + (index % 2 == 0 ? 2 : -2))
        }
    }

    /// Sum of absolute second differences in y; lower is smoother.
    private func roughness(_ points: [InkPoint]) -> Double {
        guard points.count > 2 else { return 0 }
        return (1 ..< points.count - 1).reduce(0) { total, index in
            total + abs(points[index - 1].y - 2 * points[index].y + points[index + 1].y)
        }
    }
}
