import Foundation

/// Maps between the iPad's 0-1 coordinates (over the streamed rectangle) and the Mac's ink, which is stored
/// per display in that display's local points (top-left origin).
public struct RemoteInkMapper: Equatable, Sendable {
    /// The streamed rectangle in global points (top-left origin).
    public var stream: PixelRect
    public var displays: [CaptureDisplay]

    public init(stream: PixelRect, displays: [CaptureDisplay]) {
        self.stream = stream
        self.displays = displays
    }

    /// The global point a 0-1 position refers to (clamped to the stream).
    public func globalPoint(x: Double, y: Double) -> PixelPoint {
        DesktopLayout.normalizedToPixels(PixelPoint(x: x, y: y), in: stream)
    }

    /// The display a 0-1 position lands on (the nearest one when it falls in a gap between displays).
    public func display(x: Double, y: Double) -> CaptureDisplay? {
        CaptureLayout.display(at: globalPoint(x: x, y: y), in: displays)
    }

    /// The position in `display`'s local points.
    public func localPoint(x: Double, y: Double, on display: CaptureDisplay) -> PixelPoint {
        let global = globalPoint(x: x, y: y)
        return PixelPoint(x: global.x - display.bounds.x, y: global.y - display.bounds.y)
    }

    /// A display-local point back as 0-1 fractions of the stream (may leave 0-1 for ink outside the stream).
    public func normalized(local point: PixelPoint, on display: CaptureDisplay) -> PixelPoint {
        guard stream.width > 0, stream.height > 0 else { return PixelPoint(x: 0, y: 0) }
        return PixelPoint(
            x: (point.x + display.bounds.x - stream.x) / stream.width,
            y: (point.y + display.bounds.y - stream.y) / stream.height
        )
    }

    /// An ink stroke as the iPad wants to see it: `x, y, pressure` triples in 0-1 stream fractions.
    public func snapshot(of stroke: InkStroke, on display: CaptureDisplay) -> RemoteStrokeSnapshot {
        var pts: [Float] = []
        pts.reserveCapacity(stroke.points.count * 3)
        for point in stroke.points {
            let n = normalized(local: PixelPoint(x: point.x, y: point.y), on: display)
            pts.append(Float(n.x))
            pts.append(Float(n.y))
            pts.append(point.pressure)
        }
        return RemoteStrokeSnapshot(
            id: RemoteID.wire(stroke.id),
            color: stroke.colorHex,
            size: stroke.width,
            pts: pts,
            ignorePressure: stroke.ignoresPressure
        )
    }

    /// The size of the stream in the iPad's logical units (points), as `welcome.screenW/H`.
    public var logicalSize: (width: Double, height: Double) { (stream.width, stream.height) }
}

public enum StrokeEraser {
    public struct Result: Equatable, Sendable {
        /// Strokes that were hit and no longer exist.
        public var removed: [UUID] = []
        /// What is left of partially erased strokes (new ids).
        public var added: [InkStroke] = []

        public var isEmpty: Bool { removed.isEmpty }
    }

    /// Erases along `path` (display-local points). One point is a tap; more are the ends of swept segments.
    ///
    /// A stroke is hit when any part of it comes within `diameter / 2 + width / 2` of the path. With
    /// `whole` the stroke is removed outright. Otherwise the points the eraser did not touch stay as new
    /// strokes: each run needs at least two points and `0.75 × width` of length, like the Windows host.
    public static func erase(
        strokes: [InkStroke],
        path: [PixelPoint],
        diameter: Double,
        whole: Bool,
        makeID: () -> UUID = UUID.init
    ) -> Result {
        guard !path.isEmpty, diameter > 0 else { return Result() }
        let segments = path.count == 1
            ? [(path[0], path[0])]
            : zip(path, path.dropFirst()).map { ($0, $1) }
        var result = Result()

        for stroke in strokes {
            let reach = diameter / 2 + stroke.width / 2
            guard touches(stroke, segments: segments, reach: reach) else { continue }
            result.removed.append(stroke.id)
            guard !whole else { continue }

            let dense = densified(stroke, maxStep: max(1, stroke.width / 2))
            let hit = dense.points.map { point in
                segments.contains { distance(from: point, to: $0.0, $0.1) <= reach }
            }
            let minimumLength = stroke.width * 0.75
            var run: [InkPoint] = []

            func flush() {
                defer { run.removeAll(keepingCapacity: true) }
                guard run.count >= 2, length(of: run) >= minimumLength else { return }
                var piece = stroke
                piece.id = makeID()
                piece.points = run
                result.added.append(piece)
            }

            for (index, point) in dense.points.enumerated() {
                if hit[index] { flush() } else { run.append(point) }
            }
            flush()
        }
        return result
    }

    // MARK: Geometry

    private static func touches(_ stroke: InkStroke, segments: [(PixelPoint, PixelPoint)], reach: Double) -> Bool {
        let points = stroke.points
        if points.count == 1 {
            return segments.contains { distance(from: points[0], to: $0.0, $0.1) <= reach }
        }
        for index in points.indices.dropFirst() {
            let a = points[index - 1], b = points[index]
            for segment in segments
                where segmentDistance(a: a, b: b, c: segment.0, d: segment.1) <= reach {
                return true
            }
        }
        return false
    }

    private static func distance(from point: InkPoint, to a: PixelPoint, _ b: PixelPoint) -> Double {
        let dx = b.x - a.x, dy = b.y - a.y
        let lengthSquared = dx * dx + dy * dy
        var t = lengthSquared > 0 ? ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared : 0
        t = min(max(t, 0), 1)
        return hypot(point.x - (a.x + t * dx), point.y - (a.y + t * dy))
    }

    /// Smallest distance between segments `ab` and `cd`.
    private static func segmentDistance(a: InkPoint, b: InkPoint, c: PixelPoint, d: PixelPoint) -> Double {
        if segmentsCross(a, b, c, d) { return 0 }
        let pa = PixelPoint(x: a.x, y: a.y), pb = PixelPoint(x: b.x, y: b.y)
        return min(
            distance(from: a, to: c, d),
            distance(from: b, to: c, d),
            distance(from: InkPoint(x: c.x, y: c.y), to: pa, pb),
            distance(from: InkPoint(x: d.x, y: d.y), to: pa, pb)
        )
    }

    private static func segmentsCross(_ a: InkPoint, _ b: InkPoint, _ c: PixelPoint, _ d: PixelPoint) -> Bool {
        func orient(_ px: Double, _ py: Double, _ qx: Double, _ qy: Double, _ rx: Double, _ ry: Double) -> Double {
            (qx - px) * (ry - py) - (qy - py) * (rx - px)
        }
        let o1 = orient(a.x, a.y, b.x, b.y, c.x, c.y)
        let o2 = orient(a.x, a.y, b.x, b.y, d.x, d.y)
        let o3 = orient(c.x, c.y, d.x, d.y, a.x, a.y)
        let o4 = orient(c.x, c.y, d.x, d.y, b.x, b.y)
        return (o1 > 0) != (o2 > 0) && (o3 > 0) != (o4 > 0) && o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0
    }

    /// Inserts points so consecutive ones are at most `maxStep` apart (long straight segments such as a
    /// line shape could otherwise only be erased whole). Left alone when that would add too many points.
    static func densified(_ stroke: InkStroke, maxStep: Double) -> InkStroke {
        let points = stroke.points
        guard points.count >= 2 else { return stroke }
        var total = 0
        for index in points.indices.dropFirst() {
            total += Int((hypot(points[index].x - points[index - 1].x, points[index].y - points[index - 1].y) / maxStep).rounded(.up))
        }
        guard total > 0, total <= 20_000 else { return stroke }

        var dense: [InkPoint] = [points[0]]
        for index in points.indices.dropFirst() {
            let a = points[index - 1], b = points[index]
            let steps = max(1, Int((hypot(b.x - a.x, b.y - a.y) / maxStep).rounded(.up)))
            for step in 1 ... steps {
                let t = Double(step) / Double(steps)
                dense.append(InkPoint(
                    x: a.x + (b.x - a.x) * t,
                    y: a.y + (b.y - a.y) * t,
                    pressure: a.pressure + (b.pressure - a.pressure) * Float(t)
                ))
            }
        }
        var copy = stroke
        copy.points = dense
        return copy
    }

    private static func length(of points: [InkPoint]) -> Double {
        var total = 0.0
        for index in points.indices.dropFirst() {
            total += hypot(points[index].x - points[index - 1].x, points[index].y - points[index - 1].y)
        }
        return total
    }
}
