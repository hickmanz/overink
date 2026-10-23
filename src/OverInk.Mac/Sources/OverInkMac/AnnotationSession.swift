import AppKit
import OverInkCore

@MainActor
final class AnnotationSession {
    struct PlacedStroke: Equatable, Identifiable, Sendable {
        let displayID: CGDirectDisplayID
        let stroke: InkStroke

        var id: UUID { stroke.id }
    }

    private var history = EditHistory<PlacedStroke>()

    /// Bumped whenever the committed strokes change, so views can cache rendered ink.
    private(set) var revision = 0

    var strokes: [PlacedStroke] { history.items }
    var canUndo: Bool { history.canUndo }
    var canRedo: Bool { history.canRedo }

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
        history.items.lazy
            .filter { $0.displayID == displayID }
            .map(\.stroke)
    }

    func append(_ stroke: InkStroke, on displayID: CGDirectDisplayID) {
        history.append(PlacedStroke(displayID: displayID, stroke: stroke))
        notifyChanged()
    }

    func undo() {
        if history.undo() { notifyChanged() }
    }

    func redo() {
        if history.redo() { notifyChanged() }
    }

    func clear() {
        guard !history.items.isEmpty else { return }
        history.clear()
        notifyChanged()
    }

    func erase(on displayID: CGDirectDisplayID, near point: InkPoint, radius: Double) {
        guard let target = history.items.last(where: {
            $0.displayID == displayID && StrokeGeometry.hit($0.stroke, point: point, radius: radius)
        }) else {
            return
        }
        history.remove(id: target.id)
        notifyChanged()
    }

    private func notifyChanged() {
        revision += 1
        changed?()
    }
}

extension NSScreen {
    var overInkDisplayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
            .map { CGDirectDisplayID($0.uint32Value) }
    }
}
