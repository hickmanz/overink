import Foundation

public enum WindowPlacement {
    /// Keeps a floating window reachable after displays change. If at least `minVisibleWidth` x
    /// `minVisibleHeight` of `frame` is still on a display it is returned unchanged; otherwise it is
    /// moved fully inside the display nearest to it.
    public static func recover(
        frame: PixelRect,
        visibleFrames: [PixelRect],
        minVisibleWidth: Double = 64,
        minVisibleHeight: Double = 32
    ) -> PixelRect {
        guard !visibleFrames.isEmpty else { return frame }

        let needW = min(frame.width, minVisibleWidth)
        let needH = min(frame.height, minVisibleHeight)
        let reachable = visibleFrames.contains { visible in
            guard let overlap = visible.intersection(frame) else { return false }
            return overlap.width >= needW && overlap.height >= needH
        }
        if reachable { return frame }

        let centerX = frame.x + frame.width / 2
        let centerY = frame.y + frame.height / 2
        let target = visibleFrames.min {
            distanceSquared(from: (centerX, centerY), to: $0) < distanceSquared(from: (centerX, centerY), to: $1)
        } ?? visibleFrames[0]
        return clamp(frame, inside: target)
    }

    /// Moves `frame` the minimum distance so it lies inside `bounds` (top/left wins if it doesn't fit).
    public static func clamp(_ frame: PixelRect, inside bounds: PixelRect) -> PixelRect {
        let maxX = bounds.maxX - frame.width
        let maxY = bounds.maxY - frame.height
        return PixelRect(
            x: max(bounds.minX, min(frame.x, maxX)),
            y: max(bounds.minY, min(frame.y, maxY)),
            width: frame.width,
            height: frame.height
        )
    }

    private static func distanceSquared(from point: (Double, Double), to rect: PixelRect) -> Double {
        let dx = max(max(rect.minX - point.0, 0), point.0 - rect.maxX)
        let dy = max(max(rect.minY - point.1, 0), point.1 - rect.maxY)
        return dx * dx + dy * dy
    }
}
