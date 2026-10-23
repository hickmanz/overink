import AppKit

/// The draw-mode cursor: a reticle with a ring showing the pen (or eraser) footprint.
/// It is a real system cursor, so it never appears in captures or streamed frames.
@MainActor
enum PenCursor {
    private static var cache: [Int: NSCursor] = [:]

    static func cursor(diameter: Double) -> NSCursor {
        let clamped = min(max(diameter, 4), 96)
        let key = Int((clamped * 2).rounded())
        if let cached = cache[key] { return cached }

        let ring = CGFloat(clamped)
        let tick: CGFloat = 5
        let gap: CGFloat = 2
        let side = ring + 2 * (tick + gap) + 6
        let center = side / 2
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { _ in
            func stroke(_ color: NSColor, width: CGFloat) {
                color.setStroke()
                let circle = NSBezierPath(
                    ovalIn: NSRect(x: center - ring / 2, y: center - ring / 2, width: ring, height: ring)
                )
                circle.lineWidth = width
                circle.stroke()
                let ticks = NSBezierPath()
                let inner = ring / 2 + gap
                let outer = inner + tick
                ticks.move(to: NSPoint(x: center - outer, y: center))
                ticks.line(to: NSPoint(x: center - inner, y: center))
                ticks.move(to: NSPoint(x: center + inner, y: center))
                ticks.line(to: NSPoint(x: center + outer, y: center))
                ticks.move(to: NSPoint(x: center, y: center - outer))
                ticks.line(to: NSPoint(x: center, y: center - inner))
                ticks.move(to: NSPoint(x: center, y: center + inner))
                ticks.line(to: NSPoint(x: center, y: center + outer))
                ticks.lineWidth = width
                ticks.stroke()
            }
            // White halo under a dark line keeps the cursor visible on any background.
            stroke(NSColor.white.withAlphaComponent(0.9), width: 3)
            stroke(NSColor.black.withAlphaComponent(0.85), width: 1)
            return true
        }
        let cursor = NSCursor(image: image, hotSpot: NSPoint(x: center, y: center))
        cache[key] = cursor
        return cursor
    }
}
