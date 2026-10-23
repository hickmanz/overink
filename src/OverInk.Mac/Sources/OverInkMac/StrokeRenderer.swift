import AppKit
import CoreGraphics
import OverInkCore

/// Shared Core Graphics stroke drawing for the overlay, editor, and capture flattening.
/// Strokes are drawn in document coordinates; callers set up the context transform
/// (scale / flip) beforehand so line widths scale with the content.
enum StrokeRenderer {
    static func draw(_ stroke: InkStroke, in context: CGContext) {
        guard let first = stroke.points.first else { return }
        let color = NSColor(hex: stroke.colorHex).cgColor

        context.saveGState()
        context.setStrokeColor(color)
        context.setFillColor(color)
        context.setLineCap(.round)
        context.setLineJoin(.round)

        if stroke.points.count == 1 {
            let diameter = width(at: first, stroke: stroke)
            context.fillEllipse(
                in: CGRect(
                    x: first.x - diameter / 2,
                    y: first.y - diameter / 2,
                    width: diameter,
                    height: diameter
                )
            )
        } else if stroke.ignoresPressure {
            // Constant width: one path is faster and joins cleanly.
            context.setLineWidth(stroke.width)
            context.beginPath()
            context.move(to: CGPoint(x: first.x, y: first.y))
            for point in stroke.points.dropFirst() {
                context.addLine(to: CGPoint(x: point.x, y: point.y))
            }
            context.strokePath()
        } else {
            for (start, end) in zip(stroke.points, stroke.points.dropFirst()) {
                context.setLineWidth(
                    (width(at: start, stroke: stroke) + width(at: end, stroke: stroke)) / 2
                )
                context.beginPath()
                context.move(to: CGPoint(x: start.x, y: start.y))
                context.addLine(to: CGPoint(x: end.x, y: end.y))
                context.strokePath()
            }
        }
        context.restoreGState()
    }

    static func width(at point: InkPoint, stroke: InkStroke) -> Double {
        guard !stroke.ignoresPressure else { return stroke.width }
        return stroke.width * (0.35 + 0.65 * Double(point.pressure))
    }
}
