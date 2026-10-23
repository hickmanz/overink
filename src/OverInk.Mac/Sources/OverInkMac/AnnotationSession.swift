import AppKit
import OverInkCore

@MainActor
final class AnnotationSession {
    struct PlacedStroke: Equatable {
        let displayID: CGDirectDisplayID
        let stroke: InkStroke
    }

    private enum Edit {
        case add(PlacedStroke)
        case remove(PlacedStroke, index: Int)
        case clear([PlacedStroke])
    }

    private(set) var strokes: [PlacedStroke] = []
    private var undoStack: [Edit] = []
    private var redoStack: [Edit] = []

    var colorHex = "#E53935" {
        didSet { settingsChanged?() }
    }
    var penWidth = 4.0 {
        didSet {
            penWidth = min(max(penWidth, 1), 24)
            settingsChanged?()
        }
    }
    var tool = SketchTool.pen {
        didSet { settingsChanged?() }
    }
    var pressureSensitive = true {
        didSet { settingsChanged?() }
    }
    var veilOpacity = 0.0 {
        didSet {
            veilOpacity = min(max(veilOpacity, 0), 0.6)
            changed?()
            settingsChanged?()
        }
    }
    var changed: (() -> Void)?
    var settingsChanged: (() -> Void)?

    func strokes(for displayID: CGDirectDisplayID) -> [InkStroke] {
        strokes.lazy
            .filter { $0.displayID == displayID }
            .map(\.stroke)
    }

    func append(_ stroke: InkStroke, on displayID: CGDirectDisplayID) {
        let placed = PlacedStroke(displayID: displayID, stroke: stroke)
        strokes.append(placed)
        undoStack.append(.add(placed))
        redoStack.removeAll()
        changed?()
    }

    func undo() {
        guard let edit = undoStack.popLast() else { return }
        applyInverse(edit)
        redoStack.append(edit)
        changed?()
    }

    func redo() {
        guard let edit = redoStack.popLast() else { return }
        apply(edit)
        undoStack.append(edit)
        changed?()
    }

    func clear() {
        guard !strokes.isEmpty else { return }
        let removed = strokes
        strokes.removeAll()
        undoStack.append(.clear(removed))
        redoStack.removeAll()
        changed?()
    }

    func erase(on displayID: CGDirectDisplayID, near point: InkPoint, radius: Double) {
        guard let index = strokes.lastIndex(where: {
            $0.displayID == displayID && Self.hitTest($0.stroke, point: point, radius: radius)
        }) else {
            return
        }

        let removed = strokes.remove(at: index)
        undoStack.append(.remove(removed, index: index))
        redoStack.removeAll()
        changed?()
    }

    private func apply(_ edit: Edit) {
        switch edit {
        case let .add(stroke):
            strokes.append(stroke)
        case let .remove(stroke, _):
            strokes.removeAll { $0.stroke.id == stroke.stroke.id }
        case .clear:
            strokes.removeAll()
        }
    }

    private func applyInverse(_ edit: Edit) {
        switch edit {
        case let .add(stroke):
            strokes.removeAll { $0.stroke.id == stroke.stroke.id }
        case let .remove(stroke, index):
            strokes.insert(stroke, at: min(index, strokes.count))
        case let .clear(removed):
            strokes = removed
        }
    }

    private static func hitTest(_ stroke: InkStroke, point: InkPoint, radius: Double) -> Bool {
        guard let first = stroke.points.first else { return false }
        let threshold = radius + stroke.width / 2
        if stroke.points.count == 1 {
            return distance(first, point) <= threshold
        }
        return zip(stroke.points, stroke.points.dropFirst()).contains {
            distanceToSegment(point, start: $0.0, end: $0.1) <= threshold
        }
    }

    private static func distance(_ a: InkPoint, _ b: InkPoint) -> Double {
        hypot(a.x - b.x, a.y - b.y)
    }

    private static func distanceToSegment(_ point: InkPoint, start: InkPoint, end: InkPoint) -> Double {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return distance(point, start) }
        let projection = ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared
        let t = min(max(projection, 0), 1)
        return hypot(point.x - (start.x + t * dx), point.y - (start.y + t * dy))
    }
}

extension NSScreen {
    var overInkDisplayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
            .map { CGDirectDisplayID($0.uint32Value) }
    }
}
