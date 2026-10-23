import Foundation

/// Pure helpers behind the editor's Select tool: picking, marquee, move, resize, recolor.
public enum SelectionOps {
    public enum Handle: CaseIterable, Sendable {
        case topLeft, top, topRight, right, bottomRight, bottom, bottomLeft, left

        /// Position of the handle on `bounds`.
        public func point(on bounds: PixelRect) -> PixelPoint {
            switch self {
            case .topLeft: PixelPoint(x: bounds.minX, y: bounds.minY)
            case .top: PixelPoint(x: bounds.midX, y: bounds.minY)
            case .topRight: PixelPoint(x: bounds.maxX, y: bounds.minY)
            case .right: PixelPoint(x: bounds.maxX, y: bounds.midY)
            case .bottomRight: PixelPoint(x: bounds.maxX, y: bounds.maxY)
            case .bottom: PixelPoint(x: bounds.midX, y: bounds.maxY)
            case .bottomLeft: PixelPoint(x: bounds.minX, y: bounds.maxY)
            case .left: PixelPoint(x: bounds.minX, y: bounds.midY)
            }
        }

        var movesLeft: Bool { self == .topLeft || self == .left || self == .bottomLeft }
        var movesRight: Bool { self == .topRight || self == .right || self == .bottomRight }
        var movesTop: Bool { self == .topLeft || self == .top || self == .topRight }
        var movesBottom: Bool { self == .bottomLeft || self == .bottom || self == .bottomRight }
        var isCorner: Bool { self == .topLeft || self == .topRight || self == .bottomLeft || self == .bottomRight }
    }

    // MARK: Picking

    /// The topmost stroke within `tolerance` of `point`.
    public static func pick(at point: InkPoint, in strokes: [InkStroke], tolerance: Double) -> InkStroke? {
        strokes.last { StrokeGeometry.hit($0, point: point, radius: tolerance) }
    }

    /// Strokes whose padded bounds intersect `marquee`.
    public static func strokes(in marquee: PixelRect, from strokes: [InkStroke]) -> [UUID] {
        strokes.compactMap { stroke in
            guard let box = StrokeGeometry.paddedBounds(of: stroke),
                  box.intersection(marquee) != nil else { return nil }
            return stroke.id
        }
    }

    /// Adds every stroke that shares a group with a selected stroke.
    public static func expandGroups(_ ids: Set<UUID>, in strokes: [InkStroke]) -> Set<UUID> {
        let groups = Set(strokes.filter { ids.contains($0.id) }.compactMap(\.groupId))
        guard !groups.isEmpty else { return ids }
        return ids.union(strokes.filter { $0.groupId.map(groups.contains) ?? false }.map(\.id))
    }

    /// Tight bounds of the selected strokes' points (a straight line has zero height or width).
    public static func bounds(of ids: Set<UUID>, in strokes: [InkStroke]) -> PixelRect? {
        var result: PixelRect?
        for stroke in strokes where ids.contains(stroke.id) {
            guard let box = StrokeGeometry.bounds(of: stroke) else { continue }
            result = result.map { union($0, box) } ?? box
        }
        return result
    }

    /// Like `PixelRect.union`, but keeps zero-sized rects (which `union` treats as empty).
    private static func union(_ a: PixelRect, _ b: PixelRect) -> PixelRect {
        let left = min(a.minX, b.minX)
        let top = min(a.minY, b.minY)
        return PixelRect(x: left, y: top, width: max(a.maxX, b.maxX) - left, height: max(a.maxY, b.maxY) - top)
    }

    // MARK: Transforms (each returns new strokes with the same ids)

    public static func move(_ strokes: [InkStroke], dx: Double, dy: Double) -> [InkStroke] {
        strokes.map { StrokeGeometry.translated($0, dx: dx, dy: dy) }
    }

    /// Maps points from `old` bounds to `new` bounds. Stroke widths are left alone. An axis where `old`
    /// has no extent (a straight line) is translated rather than scaled.
    public static func scale(_ strokes: [InkStroke], from old: PixelRect, to new: PixelRect) -> [InkStroke] {
        func map(_ value: Double, oldOrigin: Double, oldSize: Double, newOrigin: Double, newSize: Double) -> Double {
            oldSize > 0
                ? newOrigin + (value - oldOrigin) * newSize / oldSize
                : value + (newOrigin - oldOrigin)
        }
        return strokes.map { stroke in
            var copy = stroke
            copy.points = stroke.points.map {
                InkPoint(
                    x: map($0.x, oldOrigin: old.x, oldSize: old.width, newOrigin: new.x, newSize: new.width),
                    y: map($0.y, oldOrigin: old.y, oldSize: old.height, newOrigin: new.y, newSize: new.height),
                    pressure: $0.pressure
                )
            }
            return copy
        }
    }

    public static func recolor(_ strokes: [InkStroke], hex: String) -> [InkStroke] {
        strokes.map {
            var copy = $0
            copy.colorHex = hex
            return copy
        }
    }

    // MARK: Resizing

    /// Which handle (if any) is within `radius` of `point`.
    public static func handle(at point: PixelPoint, on bounds: PixelRect, radius: Double) -> Handle? {
        Handle.allCases.first {
            let p = $0.point(on: bounds)
            return hypot(p.x - point.x, p.y - point.y) <= radius
        }
    }

    /// The new bounds when `handle` is dragged to `point`. `keepAspect` (Shift) preserves proportions on
    /// corner handles. The result never collapses below `minimumSize`.
    public static func resized(
        _ bounds: PixelRect,
        dragging handle: Handle,
        to point: PixelPoint,
        keepAspect: Bool = false,
        minimumSize: Double = 4
    ) -> PixelRect {
        var left = bounds.minX
        var right = bounds.maxX
        var top = bounds.minY
        var bottom = bounds.maxY

        if handle.movesLeft { left = min(point.x, right - minimumSize) }
        if handle.movesRight { right = max(point.x, left + minimumSize) }
        if handle.movesTop { top = min(point.y, bottom - minimumSize) }
        if handle.movesBottom { bottom = max(point.y, top + minimumSize) }

        if keepAspect, handle.isCorner, bounds.width > 0, bounds.height > 0 {
            let ratio = bounds.width / bounds.height
            let width = right - left
            let height = bottom - top
            if width / height > ratio {
                let adjusted = width / ratio
                if handle.movesTop { top = bottom - adjusted } else { bottom = top + adjusted }
            } else {
                let adjusted = height * ratio
                if handle.movesLeft { left = right - adjusted } else { right = left + adjusted }
            }
        }
        return PixelRect(x: left, y: top, width: right - left, height: bottom - top)
    }
}

extension PixelRect {
    public var midX: Double { x + width / 2 }
    public var midY: Double { y + height / 2 }
}
