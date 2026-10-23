import Foundation

/// One display's contribution to a capture.
public struct CapturePiece: Equatable, Sendable {
    public var displayID: UInt32
    /// The part of the display to copy, in that display's local point coordinates (top-left origin).
    public var source: PixelRect
    /// Where it lands in the output image, in output points (top-left origin).
    public var destination: PixelRect

    public init(displayID: UInt32, source: PixelRect, destination: PixelRect) {
        self.displayID = displayID
        self.source = source
        self.destination = destination
    }
}

public struct CaptureDisplay: Equatable, Sendable {
    public var id: UInt32
    /// Display bounds in global point coordinates with a top-left origin (as `CGDisplayBounds` reports).
    public var bounds: PixelRect

    public init(id: UInt32, bounds: PixelRect) {
        self.id = id
        self.bounds = bounds
    }
}

public enum CaptureLayout {
    /// Splits a global capture rectangle into per-display pieces. A rectangle that lies on one display
    /// yields one piece; one that spans displays yields one piece per display it touches.
    public static func plan(rect: PixelRect, displays: [CaptureDisplay]) -> [CapturePiece] {
        guard !rect.isEmpty else { return [] }
        return displays.compactMap { display in
            guard let overlap = display.bounds.intersection(rect) else { return nil }
            return CapturePiece(
                displayID: display.id,
                source: PixelRect(
                    x: overlap.x - display.bounds.x,
                    y: overlap.y - display.bounds.y,
                    width: overlap.width,
                    height: overlap.height
                ),
                destination: PixelRect(
                    x: overlap.x - rect.x,
                    y: overlap.y - rect.y,
                    width: overlap.width,
                    height: overlap.height
                )
            )
        }
    }

    /// The smallest rectangle covering every display (the "full screen" capture area).
    public static func fullDesktop(_ displays: [CaptureDisplay]) -> PixelRect? {
        DesktopLayout.union(displays.map(\.bounds))
    }

    /// The display containing `point`, or the nearest one when the point is in a gap.
    public static func display(at point: PixelPoint, in displays: [CaptureDisplay]) -> CaptureDisplay? {
        if let hit = displays.first(where: { $0.bounds.contains(point) }) { return hit }
        return displays.min { distanceSquared(point, $0.bounds) < distanceSquared(point, $1.bounds) }
    }

    /// Strokes drawn in a display's local coordinates, moved into a capture rectangle's coordinates
    /// and clipped to it.
    public static func localize(
        _ strokes: [InkStroke],
        displayBounds: PixelRect,
        captureRect: PixelRect
    ) -> [InkStroke] {
        let dx = displayBounds.x - captureRect.x
        let dy = displayBounds.y - captureRect.y
        let moved = strokes.map { StrokeGeometry.translated($0, dx: dx, dy: dy) }
        return StrokeGeometry.crop(
            moved,
            to: PixelRect(x: 0, y: 0, width: captureRect.width, height: captureRect.height)
        )
    }

    private static func distanceSquared(_ point: PixelPoint, _ rect: PixelRect) -> Double {
        let dx = max(max(rect.minX - point.x, 0), point.x - rect.maxX)
        let dy = max(max(rect.minY - point.y, 0), point.y - rect.maxY)
        return dx * dx + dy * dy
    }
}

public enum WindowSnap {
    /// The frontmost window frame under `point`. `frames` must be ordered front to back.
    public static func topmost(at point: PixelPoint, in frames: [PixelRect]) -> PixelRect? {
        frames.first { $0.contains(point) }
    }
}

public enum CaptureMode: String, Codable, CaseIterable, Sendable {
    /// The display under the cursor.
    case auto
    /// Every display stitched into one image.
    case fullScreen
}
