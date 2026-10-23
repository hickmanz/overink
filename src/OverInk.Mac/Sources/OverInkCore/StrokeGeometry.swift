import Foundation

public enum StrokeGeometry {
    // MARK: Hit testing

    /// True when `point` lies within `radius` (plus half the stroke width) of the stroke's path.
    public static func hit(_ stroke: InkStroke, point: InkPoint, radius: Double) -> Bool {
        guard let first = stroke.points.first else { return false }
        let threshold = radius + stroke.width / 2
        if stroke.points.count == 1 {
            return distance(first, point) <= threshold
        }
        return zip(stroke.points, stroke.points.dropFirst()).contains {
            distanceToSegment(point, start: $0.0, end: $0.1) <= threshold
        }
    }

    public static func distance(_ a: InkPoint, _ b: InkPoint) -> Double {
        hypot(a.x - b.x, a.y - b.y)
    }

    public static func distanceToSegment(_ point: InkPoint, start: InkPoint, end: InkPoint) -> Double {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return distance(point, start) }
        let projection = ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared
        let t = min(max(projection, 0), 1)
        return hypot(point.x - (start.x + t * dx), point.y - (start.y + t * dy))
    }

    // MARK: Bounds

    /// Tight bounds of the stroke's points (no width padding). `nil` for an empty stroke.
    public static func bounds(of stroke: InkStroke) -> PixelRect? {
        guard let first = stroke.points.first else { return nil }
        var minX = first.x, maxX = first.x, minY = first.y, maxY = first.y
        for point in stroke.points.dropFirst() {
            minX = min(minX, point.x)
            maxX = max(maxX, point.x)
            minY = min(minY, point.y)
            maxY = max(maxY, point.y)
        }
        return PixelRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    /// Bounds padded by half the stroke width, with a minimum size of 1 so single points are selectable.
    public static func paddedBounds(of stroke: InkStroke) -> PixelRect? {
        guard let box = bounds(of: stroke) else { return nil }
        let pad = stroke.width / 2
        return PixelRect(
            x: box.x - pad,
            y: box.y - pad,
            width: max(box.width, 1) + pad * 2,
            height: max(box.height, 1) + pad * 2
        )
    }

    // MARK: Shapes

    /// Points describing a shape dragged from `start` to `end`.
    /// `constrain` (the Shift key) snaps lines and arrows to 45-degree steps and makes
    /// rectangles and ellipses square.
    public static func shapePoints(
        tool: SketchTool,
        from start: InkPoint,
        to rawEnd: InkPoint,
        constrain: Bool = false
    ) -> [InkPoint] {
        let end = constrain ? constrained(tool: tool, from: start, to: rawEnd) : rawEnd
        switch tool {
        case .pen, .eraser, .line:
            return [start, end]
        case .arrow:
            return arrowPoints(from: start, to: end)
        case .rectangle:
            return [
                start,
                InkPoint(x: end.x, y: start.y),
                end,
                InkPoint(x: start.x, y: end.y),
                start
            ]
        case .ellipse:
            return ellipsePoints(from: start, to: end)
        }
    }

    public static func constrained(tool: SketchTool, from start: InkPoint, to end: InkPoint) -> InkPoint {
        let dx = end.x - start.x
        let dy = end.y - start.y
        switch tool {
        case .line, .arrow:
            let length = hypot(dx, dy)
            guard length > 0 else { return end }
            let step = Double.pi / 4
            let angle = (atan2(dy, dx) / step).rounded() * step
            return InkPoint(
                x: start.x + length * cos(angle),
                y: start.y + length * sin(angle),
                pressure: end.pressure
            )
        case .rectangle, .ellipse:
            let side = max(abs(dx), abs(dy))
            return InkPoint(
                x: start.x + (dx < 0 ? -side : side),
                y: start.y + (dy < 0 ? -side : side),
                pressure: end.pressure
            )
        case .pen, .eraser:
            return end
        }
    }

    public static func arrowPoints(from start: InkPoint, to end: InkPoint) -> [InkPoint] {
        let angle = atan2(end.y - start.y, end.x - start.x)
        let length = min(24, max(10, hypot(end.x - start.x, end.y - start.y) * 0.25))
        let spread = Double.pi / 7
        let left = InkPoint(
            x: end.x - length * cos(angle - spread),
            y: end.y - length * sin(angle - spread)
        )
        let right = InkPoint(
            x: end.x - length * cos(angle + spread),
            y: end.y - length * sin(angle + spread)
        )
        return [start, end, left, end, right]
    }

    public static func ellipsePoints(from start: InkPoint, to end: InkPoint, segments: Int = 48) -> [InkPoint] {
        let centerX = (start.x + end.x) / 2
        let centerY = (start.y + end.y) / 2
        let radiusX = abs(end.x - start.x) / 2
        let radiusY = abs(end.y - start.y) / 2
        return (0 ... segments).map { index in
            let angle = Double(index) / Double(segments) * Double.pi * 2
            return InkPoint(
                x: centerX + cos(angle) * radiusX,
                y: centerY + sin(angle) * radiusY
            )
        }
    }

    // MARK: Capture cropping

    /// Strokes that touch `region`, translated so `region`'s origin becomes (0, 0).
    public static func crop(_ strokes: [InkStroke], to region: PixelRect) -> [InkStroke] {
        strokes.compactMap { stroke in
            guard let box = paddedBounds(of: stroke), box.intersection(region) != nil else { return nil }
            var shifted = stroke
            shifted.points = stroke.points.map {
                InkPoint(x: $0.x - region.minX, y: $0.y - region.minY, pressure: $0.pressure)
            }
            return shifted
        }
    }

    /// Translates every point of `stroke` by (`dx`, `dy`).
    public static func translated(_ stroke: InkStroke, dx: Double, dy: Double) -> InkStroke {
        var moved = stroke
        moved.points = stroke.points.map { InkPoint(x: $0.x + dx, y: $0.y + dy, pressure: $0.pressure) }
        return moved
    }
}
