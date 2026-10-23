import AppKit
import OverInkCore

@MainActor
final class AnnotationSession {
    struct PlacedStroke: Equatable, Identifiable, Sendable {
        let displayID: CGDirectDisplayID
        let stroke: InkStroke

        var id: UUID { stroke.id }
    }

    /// What an edit changed, for the remote host (so the iPad can drop or restore its own copies).
    struct InkDiff: Equatable {
        var removed: [UUID] = []
        var added: [PlacedStroke] = []
        var cleared = false

        var isEmpty: Bool { removed.isEmpty && added.isEmpty && !cleared }
    }

    /// A stroke an iPad is drawing right now. It shows on the overlay but is not part of the history until
    /// the stroke ends.
    struct LiveStroke: Equatable {
        let displayID: CGDirectDisplayID
        var stroke: InkStroke
        let author: String
    }

    private var history = EditHistory<PlacedStroke>()
    private(set) var liveStrokes: [String: LiveStroke] = [:]

    /// Bumped whenever the committed strokes change, so views can cache rendered ink.
    private(set) var revision = 0

    var strokes: [PlacedStroke] { history.items }
    var canUndo: Bool { history.canUndo }
    var canRedo: Bool { history.canRedo }

    var colorHex = "#E53935" {
        didSet { notifySettings() }
    }
    var penWidth = 4.0 {
        didSet {
            penWidth = min(max(penWidth, 1), 24)
            notifySettings()
        }
    }
    var tool = SketchTool.pen {
        didSet { notifySettings() }
    }
    var pressureSensitive = true {
        didSet { notifySettings() }
    }
    var veilOpacity = 0.0 {
        didSet {
            veilOpacity = min(max(veilOpacity, 0), 0.6)
            changed?()
            notifySettings()
        }
    }
    /// Hit radius of the whole-stroke eraser, in points.
    var eraserRadius: Double { max(8, penWidth * 2) }

    /// Diameter of the on-screen cursor ring for the active tool.
    var cursorDiameter: Double {
        tool == .eraser ? eraserRadius * 2 : max(penWidth, 6)
    }

    var changed: (() -> Void)?
    private var changeObservers: [() -> Void] = []
    private var settingsObservers: [() -> Void] = []

    /// Called by every host of the session after any committed or live ink change (including from other observers).
    func observeChanges(_ observer: @escaping () -> Void) {
        changeObservers.append(observer)
    }

    /// Called after undo, redo, erase and clear with what changed. Not called for newly drawn strokes.
    var inkDiffHandler: ((InkDiff) -> Void)?

    /// Called whenever tool, color, width, pressure, or veil change.
    func observeSettings(_ observer: @escaping () -> Void) {
        settingsObservers.append(observer)
    }

    private func notifySettings() {
        settingsObservers.forEach { $0() }
    }

    func strokes(for displayID: CGDirectDisplayID) -> [InkStroke] {
        history.items.lazy
            .filter { $0.displayID == displayID }
            .map(\.stroke)
    }

    func append(_ stroke: InkStroke, on displayID: CGDirectDisplayID, author: String? = nil) {
        history.append(PlacedStroke(displayID: displayID, stroke: stroke), author: author)
        notifyChanged()
    }

    func undo(author: String? = nil) {
        edit { $0.undo(author: author) }
    }

    func redo(author: String? = nil) {
        edit { $0.redo(author: author) }
    }

    func clear(author: String? = nil) {
        guard !history.items.isEmpty else { return }
        history.clear(author: author)
        notifyChanged()
        inkDiffHandler?(InkDiff(cleared: true))
    }

    func erase(on displayID: CGDirectDisplayID, near point: InkPoint, radius: Double) {
        guard let target = history.items.last(where: {
            $0.displayID == displayID && StrokeGeometry.hit($0.stroke, point: point, radius: radius)
        }) else {
            return
        }
        edit { history in
            history.remove(id: target.id)
            return true
        }
    }

    /// Applies the outcome of an eraser sweep as one undo step.
    func apply(_ erase: StrokeEraser.Result, on displayID: CGDirectDisplayID, author: String?) {
        guard !erase.isEmpty else { return }
        edit { history in
            history.edit(
                removing: erase.removed,
                adding: erase.added.map { PlacedStroke(displayID: displayID, stroke: $0) },
                author: author
            )
            return true
        }
    }

    /// Whether `author` has an edit to undo or redo (a remote client's undo only touches its own work).
    func canUndo(author: String?) -> Bool { history.canUndo(author: author) }
    func canRedo(author: String?) -> Bool { history.canRedo(author: author) }

    // MARK: Live remote strokes

    func beginLive(id: String, displayID: CGDirectDisplayID, stroke: InkStroke, author: String) {
        liveStrokes[id] = LiveStroke(displayID: displayID, stroke: stroke, author: author)
        changed?()
    }

    func extendLive(id: String, with points: [InkPoint]) {
        guard var live = liveStrokes[id], !points.isEmpty else { return }
        live.stroke.points.append(contentsOf: points)
        liveStrokes[id] = live
        changed?()
    }

    @discardableResult
    func endLive(id: String) -> LiveStroke? {
        guard let live = liveStrokes.removeValue(forKey: id) else { return nil }
        changed?()
        return live
    }

    func liveStrokes(for displayID: CGDirectDisplayID) -> [InkStroke] {
        liveStrokes.values.filter { $0.displayID == displayID }.map(\.stroke)
    }

    /// Runs `mutation` on the history; if it changed anything, reports what.
    private func edit(_ mutation: (inout EditHistory<PlacedStroke>) -> Bool) {
        let before = history.items
        guard mutation(&history) else { return }
        let beforeIDs = Set(before.map(\.id))
        let afterIDs = Set(history.items.map(\.id))
        let diff = InkDiff(
            removed: before.map(\.id).filter { !afterIDs.contains($0) },
            added: history.items.filter { !beforeIDs.contains($0.id) }
        )
        notifyChanged()
        if !diff.isEmpty { inkDiffHandler?(diff) }
    }

    private func notifyChanged() {
        revision += 1
        changed?()
        changeObservers.forEach { $0() }
    }
}

extension NSScreen {
    var overInkDisplayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
            .map { CGDirectDisplayID($0.uint32Value) }
    }
}
