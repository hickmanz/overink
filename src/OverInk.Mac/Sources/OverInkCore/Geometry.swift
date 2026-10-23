import Foundation

public struct PixelPoint: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

public struct PixelRect: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var minX: Double { x }
    public var minY: Double { y }
    public var maxX: Double { x + width }
    public var maxY: Double { y + height }
    public var isEmpty: Bool { width <= 0 || height <= 0 }

    public func contains(_ point: PixelPoint) -> Bool {
        point.x >= minX && point.x < maxX && point.y >= minY && point.y < maxY
    }

    public func intersection(_ other: PixelRect) -> PixelRect? {
        let left = max(minX, other.minX)
        let top = max(minY, other.minY)
        let right = min(maxX, other.maxX)
        let bottom = min(maxY, other.maxY)
        guard right > left, bottom > top else { return nil }
        return PixelRect(x: left, y: top, width: right - left, height: bottom - top)
    }

    public func union(_ other: PixelRect) -> PixelRect {
        guard !isEmpty else { return other }
        guard !other.isEmpty else { return self }
        let left = min(minX, other.minX)
        let top = min(minY, other.minY)
        let right = max(maxX, other.maxX)
        let bottom = max(maxY, other.maxY)
        return PixelRect(x: left, y: top, width: right - left, height: bottom - top)
    }
}

public struct MonitorGeometry: Codable, Equatable, Sendable {
    public var id: String
    public var pixels: PixelRect
    public var points: PixelRect
    public var scaleX: Double
    public var scaleY: Double
    public var isPrimary: Bool

    public init(
        id: String,
        pixels: PixelRect,
        points: PixelRect,
        scaleX: Double,
        scaleY: Double,
        isPrimary: Bool
    ) {
        self.id = id
        self.pixels = pixels
        self.points = points
        self.scaleX = scaleX
        self.scaleY = scaleY
        self.isPrimary = isPrimary
    }
}

public enum DesktopLayout {
    public static func union<S: Sequence>(_ rects: S) -> PixelRect? where S.Element == PixelRect {
        rects.reduce(nil as PixelRect?) { result, rect in
            guard !rect.isEmpty else { return result }
            return result?.union(rect) ?? rect
        }
    }

    public static func virtualDesktop(_ monitors: [MonitorGeometry]) -> PixelRect? {
        union(monitors.map(\.pixels))
    }

    public static func monitor(at pixel: PixelPoint, in monitors: [MonitorGeometry]) -> MonitorGeometry? {
        monitors.first { $0.pixels.contains(pixel) }
    }

    public static func nearestMonitor(
        to pixel: PixelPoint,
        in monitors: [MonitorGeometry]
    ) -> MonitorGeometry? {
        if let hit = monitor(at: pixel, in: monitors) {
            return hit
        }

        return monitors.min { lhs, rhs in
            squaredDistance(from: pixel, to: lhs.pixels) < squaredDistance(from: pixel, to: rhs.pixels)
        }
    }

    public static func monitor(for rect: PixelRect, in monitors: [MonitorGeometry]) -> MonitorGeometry? {
        let overlapping = monitors.compactMap { monitor -> (MonitorGeometry, Double)? in
            guard let overlap = monitor.pixels.intersection(rect) else { return nil }
            return (monitor, overlap.width * overlap.height)
        }
        if let best = overlapping.max(by: { $0.1 < $1.1 }) {
            return best.0
        }

        return nearestMonitor(
            to: PixelPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2),
            in: monitors
        )
    }

    public static func pixelsToNormalized(_ pixel: PixelPoint, in area: PixelRect) -> PixelPoint {
        guard area.width > 0, area.height > 0 else { return PixelPoint(x: 0, y: 0) }
        return PixelPoint(x: (pixel.x - area.minX) / area.width, y: (pixel.y - area.minY) / area.height)
    }

    public static func normalizedToPixels(_ point: PixelPoint, in area: PixelRect) -> PixelPoint {
        PixelPoint(
            x: area.minX + point.x.clamped(to: 0 ... 1) * area.width,
            y: area.minY + point.y.clamped(to: 0 ... 1) * area.height
        )
    }

    private static func squaredDistance(from point: PixelPoint, to rect: PixelRect) -> Double {
        let dx = max(max(rect.minX - point.x, 0), point.x - rect.maxX)
        let dy = max(max(rect.minY - point.y, 0), point.y - rect.maxY)
        return dx * dx + dy * dy
    }
}

private extension Comparable {
    func clamped(to range: ClosedRange<Self>) -> Self {
        min(max(self, range.lowerBound), range.upperBound)
    }
}
