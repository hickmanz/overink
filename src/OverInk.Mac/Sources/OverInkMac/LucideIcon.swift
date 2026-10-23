import AppKit

enum LucideIcon {
    case pointer
    case pencil
    case eraser
    case line
    case arrow
    case rectangle
    case ellipse
    case undo
    case redo
    case trash
    case camera
    case images
    case minus
    case palette
    case contrast
    case chevronDown
    case settings

    func image(size: CGFloat = 18, label: String) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { bounds in
            let scale = bounds.width / 24
            NSColor.black.setStroke()
            let context = IconContext(scale: scale, height: bounds.height)
            draw(in: context)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = label
        return image
    }

    private func draw(in context: IconContext) {
        switch self {
        case .pointer:
            context.polyline([(4, 4), (20, 10.5), (13.9, 12.1), (12.2, 19.8), (4, 4)])
        case .pencil:
            context.polyline([(3, 21), (4.7, 15.8), (16.7, 3.8), (20.2, 7.3), (8.2, 19.3), (3, 21)])
            context.line((14.8, 5.7), (18.3, 9.2))
        case .eraser:
            context.roundedPolygon([(3, 15), (13.5, 4.5), (21, 12), (12, 21), (8, 21), (3, 16)])
            context.line((6, 12), (15, 21))
            context.line((12, 21), (21, 21))
        case .line:
            context.line((5, 19), (19, 5))
        case .arrow:
            context.line((5, 19), (19, 5))
            context.polyline([(11, 5), (19, 5), (19, 13)])
        case .rectangle:
            context.roundedRect(x: 4, y: 5, width: 16, height: 14, radius: 2)
        case .ellipse:
            context.ellipse(x: 3, y: 5, width: 18, height: 14)
        case .undo:
            context.polyline([(9, 14), (4, 9), (9, 4)])
            context.cubic(
                from: (4, 9),
                control1: (10, 9),
                control2: (20, 7),
                to: (20, 15)
            )
            context.cubic(
                from: (20, 15),
                control1: (20, 20),
                control2: (15, 21),
                to: (11, 20)
            )
        case .redo:
            context.polyline([(15, 14), (20, 9), (15, 4)])
            context.cubic(
                from: (20, 9),
                control1: (14, 9),
                control2: (4, 7),
                to: (4, 15)
            )
            context.cubic(
                from: (4, 15),
                control1: (4, 20),
                control2: (9, 21),
                to: (13, 20)
            )
        case .trash:
            context.line((4, 7), (20, 7))
            context.line((9, 3), (15, 3))
            context.roundedRect(x: 6, y: 7, width: 12, height: 14, radius: 2)
            context.line((10, 11), (10, 17))
            context.line((14, 11), (14, 17))
        case .camera:
            context.roundedRect(x: 2, y: 7, width: 20, height: 13, radius: 2)
            context.polyline([(7, 7), (9, 4), (15, 4), (17, 7)])
            context.ellipse(x: 9, y: 10, width: 6, height: 6)
        case .images:
            context.roundedRect(x: 3, y: 5, width: 16, height: 14, radius: 2)
            context.polyline([(5, 16), (9, 12), (12, 15), (15, 11), (19, 15)])
            context.ellipse(x: 7, y: 8, width: 2, height: 2)
            context.polyline([(7, 3), (21, 3), (21, 17)])
        case .minus:
            context.line((5, 12), (19, 12))
        case .palette:
            context.ellipse(x: 2, y: 2, width: 20, height: 20)
            context.ellipse(x: 6, y: 7, width: 1, height: 1)
            context.ellipse(x: 9, y: 4, width: 1, height: 1)
            context.ellipse(x: 14, y: 4, width: 1, height: 1)
            context.ellipse(x: 17, y: 8, width: 1, height: 1)
        case .contrast:
            context.ellipse(x: 2, y: 2, width: 20, height: 20)
            context.cubic(from: (12, 2), control1: (20, 5), control2: (20, 19), to: (12, 22))
            context.line((12, 2), (12, 22))
        case .chevronDown:
            context.polyline([(6, 9), (12, 15), (18, 9)])
        case .settings:
            context.ellipse(x: 9, y: 9, width: 6, height: 6)
            context.ellipse(x: 4, y: 4, width: 16, height: 16)
            for angle in stride(from: 0.0, to: Double.pi * 2, by: Double.pi / 2) {
                context.line(
                    (12 + cos(angle) * 8, 12 + sin(angle) * 8),
                    (12 + cos(angle) * 10, 12 + sin(angle) * 10)
                )
            }
        }
    }
}

private struct IconContext {
    let scale: CGFloat
    let height: CGFloat

    func point(_ value: (Double, Double)) -> NSPoint {
        NSPoint(x: CGFloat(value.0) * scale, y: height - CGFloat(value.1) * scale)
    }

    func path() -> NSBezierPath {
        let path = NSBezierPath()
        path.lineWidth = 2 * scale
        path.lineCapStyle = .round
        path.lineJoinStyle = .round
        return path
    }

    func line(_ start: (Double, Double), _ end: (Double, Double)) {
        let path = path()
        path.move(to: point(start))
        path.line(to: point(end))
        path.stroke()
    }

    func polyline(_ points: [(Double, Double)]) {
        guard let first = points.first else { return }
        let path = path()
        path.move(to: point(first))
        points.dropFirst().forEach { path.line(to: point($0)) }
        path.stroke()
    }

    func roundedPolygon(_ points: [(Double, Double)]) {
        polyline(points)
    }

    func roundedRect(x: Double, y: Double, width: Double, height rectHeight: Double, radius: Double) {
        let rect = NSRect(
            x: CGFloat(x) * scale,
            y: height - CGFloat(y + rectHeight) * scale,
            width: CGFloat(width) * scale,
            height: CGFloat(rectHeight) * scale
        )
        let path = NSBezierPath(
            roundedRect: rect,
            xRadius: CGFloat(radius) * scale,
            yRadius: CGFloat(radius) * scale
        )
        path.lineWidth = 2 * scale
        path.stroke()
    }

    func ellipse(x: Double, y: Double, width: Double, height ellipseHeight: Double) {
        let rect = NSRect(
            x: CGFloat(x) * scale,
            y: height - CGFloat(y + ellipseHeight) * scale,
            width: CGFloat(width) * scale,
            height: CGFloat(ellipseHeight) * scale
        )
        let path = NSBezierPath(ovalIn: rect)
        path.lineWidth = 2 * scale
        path.stroke()
    }

    func cubic(
        from: (Double, Double),
        control1: (Double, Double),
        control2: (Double, Double),
        to: (Double, Double)
    ) {
        let path = path()
        path.move(to: point(from))
        path.curve(
            to: point(to),
            controlPoint1: point(control1),
            controlPoint2: point(control2)
        )
        path.stroke()
    }
}
