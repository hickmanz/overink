import AppKit
import OverInkCore

enum EditorTool: String, CaseIterable {
    case select
    case pen
    case eraser
    case line
    case arrow
    case rectangle
    case ellipse

    var sketch: SketchTool? {
        switch self {
        case .select: nil
        case .pen: .pen
        case .eraser: .eraser
        case .line: .line
        case .arrow: .arrow
        case .rectangle: .rectangle
        case .ellipse: .ellipse
        }
    }

    var title: String {
        switch self {
        case .select: "Select (V)"
        case .pen: "Pen (P)"
        case .eraser: "Eraser (E)"
        case .line: "Line (L)"
        case .arrow: "Arrow (A)"
        case .rectangle: "Rectangle (R)"
        case .ellipse: "Ellipse (O)"
        }
    }

    var icon: LucideIcon {
        switch self {
        case .select: .pointer
        case .pen: .pencil
        case .eraser: .eraser
        case .line: .line
        case .arrow: .arrow
        case .rectangle: .rectangle
        case .ellipse: .ellipse
        }
    }

    static func forLetter(_ letter: String) -> EditorTool? {
        switch letter {
        case "v": .select
        case "p": .pen
        case "e": .eraser
        case "l": .line
        case "a": .arrow
        case "r": .rectangle
        case "o": .ellipse
        default: nil
        }
    }
}

/// The editable capture: background image, veil, and strokes in canvas points (top-left origin).
/// It lives inside a magnifying scroll view, so all geometry here is in canvas coordinates.
@MainActor
final class EditorCanvasView: NSView {
    private(set) var canvasSize = CGSize(width: 1, height: 1)
    private(set) var veilOpacity = 0.0
    private(set) var history = EditHistory<InkStroke>()
    private(set) var selection: Set<UUID> = []

    var tool = EditorTool.pen {
        didSet {
            guard tool != oldValue else { return }
            if tool != .select { selection = [] }
            drag = .none
            refreshCursor()
            needsDisplay = true
            onStateChange?()
        }
    }
    var colorHex = "#E53935"
    var penWidth = 4.0 {
        didSet { refreshCursor() }
    }
    var pressureSensitive = true

    /// The document was edited (drives autosave).
    var onChange: (() -> Void)?
    /// Selection or undo/redo availability changed (drives toolbar state).
    var onStateChange: (() -> Void)?
    var onToolShortcut: ((EditorTool) -> Void)?

    private enum Drag {
        case none
        case drawing(SketchTool, start: InkPoint)
        case erasing
        case marquee(start: CGPoint, base: Set<UUID>)
        case moving(start: CGPoint, originals: [InkStroke])
        case resizing(handle: SelectionOps.Handle, oldTight: PixelRect, pad: Double, originals: [InkStroke])
    }

    private var backgroundImage: NSImage?
    private var drag = Drag.none
    private var activeStroke: InkStroke?
    private var preview: [UUID: InkStroke] = [:]
    private var erased: Set<UUID> = []
    private var marqueeRect: CGRect?

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    var zoom: CGFloat { enclosingScrollView?.magnification ?? 1 }
    var canUndo: Bool { history.canUndo }
    var canRedo: Bool { history.canRedo }
    var eraserRadius: Double { max(8, penWidth * 2) }

    /// The document as it would be saved.
    var document: CaptureDocument {
        CaptureDocument(
            canvasWidth: canvasSize.width,
            canvasHeight: canvasSize.height,
            veilOpacity: veilOpacity,
            strokes: history.items
        )
    }

    // MARK: Loading

    func load(image: CGImage, document: CaptureDocument) {
        canvasSize = CGSize(width: max(document.canvasWidth, 1), height: max(document.canvasHeight, 1))
        backgroundImage = NSImage(cgImage: image, size: NSSize(width: canvasSize.width, height: canvasSize.height))
        veilOpacity = document.veilOpacity
        history = EditHistory<InkStroke>()
        history.load(document.strokes)
        selection = []
        drag = .none
        activeStroke = nil
        preview = [:]
        erased = []
        marqueeRect = nil
        setFrameSize(canvasSize)
        needsDisplay = true
        onStateChange?()
    }

    func setVeil(_ value: Double) {
        let clamped = min(max(value, 0), 0.6)
        guard clamped != veilOpacity else { return }
        veilOpacity = clamped
        needsDisplay = true
        onChange?()
    }

    func refreshCursor() {
        window?.invalidateCursorRects(for: self)
    }

    override func resetCursorRects() {
        if tool == .select {
            addCursorRect(bounds, cursor: .arrow)
        } else {
            let base = tool == .eraser ? eraserRadius * 2 : max(penWidth, 6)
            addCursorRect(bounds, cursor: PenCursor.cursor(diameter: base * Double(zoom)))
        }
    }

    // MARK: History

    private func didMutate() {
        needsDisplay = true
        onChange?()
        onStateChange?()
    }

    private func prune() {
        let existing = Set(history.items.map(\.id))
        selection = selection.intersection(existing)
    }

    func undo() {
        guard history.undo() else { return }
        prune()
        didMutate()
    }

    func redo() {
        guard history.redo() else { return }
        prune()
        didMutate()
    }

    // MARK: Selection commands

    func selectAll() {
        tool = .select
        selection = Set(history.items.map(\.id))
        needsDisplay = true
        onStateChange?()
    }

    func clearSelection() {
        guard !selection.isEmpty else { return }
        selection = []
        needsDisplay = true
        onStateChange?()
    }

    func deleteSelection() {
        guard !selection.isEmpty else { return }
        history.remove(ids: Array(selection))
        selection = []
        didMutate()
    }

    func recolorSelection(_ hex: String) {
        let targets = history.items.filter { selection.contains($0.id) }
        guard !targets.isEmpty else { return }
        history.replaceAll(SelectionOps.recolor(targets, hex: hex))
        didMutate()
    }

    private func nudgeSelection(dx: Double, dy: Double) {
        let targets = history.items.filter { selection.contains($0.id) }
        guard !targets.isEmpty else { return }
        history.replaceAll(SelectionOps.move(targets, dx: dx, dy: dy))
        didMutate()
    }

    // MARK: Mouse

    private func inkPoint(for event: NSEvent) -> InkPoint {
        let point = convert(event.locationInWindow, from: nil)
        let pressure: Float = event.pressure > 0 ? event.pressure : 1
        return InkPoint(x: point.x, y: point.y, pressure: pressure)
    }

    private var visibleStrokes: [InkStroke] {
        history.items.compactMap { stroke in
            erased.contains(stroke.id) ? nil : (preview[stroke.id] ?? stroke)
        }
    }

    private var selectionPad: Double { 3 / Double(max(zoom, 0.05)) }

    /// Selection bounds including the padding the handles sit on.
    private func displayBounds(of strokes: [InkStroke]) -> PixelRect? {
        guard let tight = SelectionOps.bounds(of: selection, in: strokes) else { return nil }
        let pad = selectionPad
        return PixelRect(x: tight.x - pad, y: tight.y - pad, width: tight.width + pad * 2, height: tight.height + pad * 2)
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let point = inkPoint(for: event)
        let location = CGPoint(x: point.x, y: point.y)
        let shift = event.modifierFlags.contains(.shift)

        if tool == .select {
            beginSelectInteraction(at: point, location: location, shift: shift)
            return
        }
        if tool == .eraser {
            drag = .erasing
            erase(near: point)
            return
        }
        guard let sketch = tool.sketch else { return }
        drag = .drawing(sketch, start: point)
        activeStroke = InkStroke(
            colorHex: colorHex,
            width: penWidth,
            points: [point],
            ignoresPressure: !pressureSensitive || sketch != .pen
        )
        needsDisplay = true
    }

    private func beginSelectInteraction(at point: InkPoint, location: CGPoint, shift: Bool) {
        let strokes = visibleStrokes
        let zoomValue = Double(max(zoom, 0.05))

        if !selection.isEmpty, let display = displayBounds(of: strokes),
           let handle = SelectionOps.handle(
               at: PixelPoint(x: point.x, y: point.y), on: display, radius: 9 / zoomValue
           ),
           let tight = SelectionOps.bounds(of: selection, in: strokes) {
            drag = .resizing(
                handle: handle,
                oldTight: tight,
                pad: selectionPad,
                originals: strokes.filter { selection.contains($0.id) }
            )
            return
        }

        if let hit = SelectionOps.pick(at: point, in: strokes, tolerance: 6 / zoomValue) {
            let group = SelectionOps.expandGroups([hit.id], in: strokes)
            if shift {
                if selection.isSuperset(of: group) {
                    selection.subtract(group)
                } else {
                    selection.formUnion(group)
                }
            } else if !selection.contains(hit.id) {
                selection = group
            }
            onStateChange?()
            needsDisplay = true
            if selection.contains(hit.id) {
                drag = .moving(start: location, originals: strokes.filter { selection.contains($0.id) })
            }
            return
        }

        let base = shift ? selection : []
        if !shift, !selection.isEmpty {
            selection = []
            onStateChange?()
        }
        drag = .marquee(start: location, base: base)
        marqueeRect = CGRect(origin: location, size: .zero)
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        let point = inkPoint(for: event)
        let location = CGPoint(x: point.x, y: point.y)
        switch drag {
        case .none:
            break
        case .erasing:
            erase(near: point)
        case let .drawing(sketch, start):
            if sketch == .pen {
                activeStroke?.points.append(point)
            } else {
                activeStroke?.points = StrokeGeometry.shapePoints(
                    tool: sketch, from: start, to: point, constrain: event.modifierFlags.contains(.shift)
                )
            }
            needsDisplay = true
        case let .marquee(start, base):
            let rect = CGRect(
                x: min(start.x, location.x), y: min(start.y, location.y),
                width: abs(location.x - start.x), height: abs(location.y - start.y)
            )
            marqueeRect = rect
            let ids = SelectionOps.strokes(
                in: PixelRect(x: rect.minX, y: rect.minY, width: rect.width, height: rect.height),
                from: history.items
            )
            selection = base.union(SelectionOps.expandGroups(Set(ids), in: history.items))
            needsDisplay = true
        case let .moving(start, originals):
            let moved = SelectionOps.move(originals, dx: location.x - start.x, dy: location.y - start.y)
            preview = Dictionary(uniqueKeysWithValues: moved.map { ($0.id, $0) })
            needsDisplay = true
        case let .resizing(handle, oldTight, pad, originals):
            let oldDisplay = PixelRect(
                x: oldTight.x - pad, y: oldTight.y - pad,
                width: oldTight.width + pad * 2, height: oldTight.height + pad * 2
            )
            let newDisplay = SelectionOps.resized(
                oldDisplay,
                dragging: handle,
                to: PixelPoint(x: point.x, y: point.y),
                keepAspect: event.modifierFlags.contains(.shift),
                minimumSize: 4 + pad * 2
            )
            let newTight = PixelRect(
                x: newDisplay.x + pad, y: newDisplay.y + pad,
                width: max(newDisplay.width - pad * 2, 0), height: max(newDisplay.height - pad * 2, 0)
            )
            let scaled = SelectionOps.scale(originals, from: oldTight, to: newTight)
            preview = Dictionary(uniqueKeysWithValues: scaled.map { ($0.id, $0) })
            needsDisplay = true
        }
    }

    override func mouseUp(with event: NSEvent) {
        let point = inkPoint(for: event)
        defer {
            drag = .none
            activeStroke = nil
            marqueeRect = nil
            preview = [:]
            erased = []
            needsDisplay = true
        }
        switch drag {
        case .none:
            break
        case .erasing:
            if !erased.isEmpty {
                history.remove(ids: Array(erased))
                prune()
                erased = []
                didMutate()
            }
        case let .drawing(sketch, start):
            guard var stroke = activeStroke else { return }
            if sketch == .pen {
                if let last = stroke.points.last, last.x != point.x || last.y != point.y {
                    stroke.points.append(point)
                }
                StrokeSmoother.apply(to: &stroke)
            } else {
                stroke.points = StrokeGeometry.shapePoints(
                    tool: sketch, from: start, to: point, constrain: event.modifierFlags.contains(.shift)
                )
            }
            history.append(stroke)
            activeStroke = nil
            didMutate()
        case .marquee:
            onStateChange?()
        case .moving, .resizing:
            let changed = Array(preview.values)
            let moved = changed.contains { new in
                history.items.first { $0.id == new.id }?.points != new.points
            }
            if moved {
                history.replaceAll(changed)
                preview = [:]
                didMutate()
            }
        }
    }

    private func erase(near point: InkPoint) {
        for stroke in history.items.reversed()
        where !erased.contains(stroke.id) && StrokeGeometry.hit(stroke, point: point, radius: eraserRadius) {
            erased.insert(stroke.id)
            needsDisplay = true
            break
        }
    }

    // MARK: Keyboard

    override func keyDown(with event: NSEvent) {
        let blocked: NSEvent.ModifierFlags = [.command, .control, .option]
        if event.modifierFlags.isDisjoint(with: blocked),
           let letter = event.charactersIgnoringModifiers?.lowercased(),
           let tool = EditorTool.forLetter(letter) {
            onToolShortcut?(tool)
            return
        }
        let step = event.modifierFlags.contains(.shift) ? 10.0 : 1.0
        switch event.keyCode {
        case 51, 117: deleteSelection()
        case 53: clearSelection()
        case 123: nudgeSelection(dx: -step, dy: 0)
        case 124: nudgeSelection(dx: step, dy: 0)
        case 125: nudgeSelection(dx: 0, dy: step)
        case 126: nudgeSelection(dx: 0, dy: -step)
        default: super.keyDown(with: event)
        }
    }

    // MARK: Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        backgroundImage?.draw(
            in: bounds,
            from: .zero,
            operation: .copy,
            fraction: 1,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high]
        )
        if veilOpacity > 0 {
            context.setFillColor(NSColor.white.withAlphaComponent(veilOpacity).cgColor)
            context.fill(bounds)
        }

        context.saveGState()
        context.clip(to: bounds)
        let strokes = visibleStrokes
        strokes.forEach { StrokeRenderer.draw($0, in: context) }
        if let activeStroke {
            StrokeRenderer.draw(activeStroke, in: context)
        }
        context.restoreGState()

        drawSelection(strokes, in: context)
    }

    private func drawSelection(_ strokes: [InkStroke], in context: CGContext) {
        let scale = CGFloat(max(zoom, 0.05))
        let accent = NSColor.controlAccentColor

        if !selection.isEmpty, let display = displayBounds(of: strokes) {
            let rect = CGRect(x: display.x, y: display.y, width: display.width, height: display.height)
            context.saveGState()
            context.setStrokeColor(accent.cgColor)
            context.setLineWidth(1 / scale)
            context.setLineDash(phase: 0, lengths: [4 / scale, 3 / scale])
            context.stroke(rect)
            context.setLineDash(phase: 0, lengths: [])

            let side = 8 / scale
            for handle in SelectionOps.Handle.allCases {
                let center = handle.point(on: display)
                let square = CGRect(x: center.x - side / 2, y: center.y - side / 2, width: side, height: side)
                context.setFillColor(NSColor.white.cgColor)
                context.fill(square)
                context.setStrokeColor(accent.cgColor)
                context.stroke(square)
            }
            context.restoreGState()
        }

        if let marqueeRect {
            context.saveGState()
            context.setFillColor(accent.withAlphaComponent(0.12).cgColor)
            context.fill(marqueeRect)
            context.setStrokeColor(accent.cgColor)
            context.setLineWidth(1 / scale)
            context.stroke(marqueeRect)
            context.restoreGState()
        }
    }
}

/// Keeps the canvas centered when it is smaller than the visible area.
final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var rect = super.constrainBoundsRect(proposedBounds)
        guard let documentView else { return rect }
        if rect.width > documentView.frame.width {
            rect.origin.x = (documentView.frame.width - rect.width) / 2
        }
        if rect.height > documentView.frame.height {
            rect.origin.y = (documentView.frame.height - rect.height) / 2
        }
        return rect
    }
}
