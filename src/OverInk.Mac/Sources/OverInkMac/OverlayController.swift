import AppKit
import OverInkCore

@MainActor
final class OverlayController {
    enum InteractionMode {
        case draw
        case passThrough
    }

    private var windows: [OverlayWindow] = []
    private var screenObserver: NSObjectProtocol?
    let session = AnnotationSession()
    private(set) var mode: InteractionMode = .passThrough
    var modeDidChange: ((InteractionMode) -> Void)?
    private var modeObservers: [(InteractionMode) -> Void] = []

    /// Extra listeners for mode changes, in addition to `modeDidChange`.
    func addModeObserver(_ observer: @escaping (InteractionMode) -> Void) {
        modeObservers.append(observer)
    }
    private lazy var drawShortcuts = DrawShortcuts(
        selectTool: { [weak self] tool in self?.session.tool = tool },
        undo: { [weak self] in self?.undo() },
        redo: { [weak self] in self?.redo() },
        exitDrawing: { [weak self] in self?.setMode(.passThrough) }
    )

    init() {
        session.changed = { [weak self] in
            self?.windows.forEach { $0.refreshInk() }
        }
        session.observeSettings { [weak self] in
            self?.windows.forEach { $0.refreshCursor() }
        }
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.rebuildWindows()
            }
        }
    }

    func show() {
        rebuildWindows()
    }

    func toggleMode() {
        setMode(mode == .draw ? .passThrough : .draw)
    }

    func setMode(_ newMode: InteractionMode) {
        mode = newMode
        for window in windows {
            window.isDrawingEnabled = newMode == .draw
        }
        drawShortcuts.setEnabled(newMode == .draw)
        modeDidChange?(newMode)
        modeObservers.forEach { $0(newMode) }
    }

    func clear() {
        session.clear()
    }

    func undo() {
        session.undo()
    }

    func redo() {
        session.redo()
    }

    func shutdown() {
        drawShortcuts.shutdown()
    }

    private func showContextMenu(for event: NSEvent, in view: NSView) {
        let menu = NSMenu()
        for entry in DrawShortcutMap.entries {
            let item = NSMenuItem(
                title: Self.title(for: entry.tool),
                action: #selector(ContextMenuTarget.selectTool(_:)),
                keyEquivalent: entry.letter.lowercased()
            )
            item.keyEquivalentModifierMask = []
            item.representedObject = entry.tool.rawValue
            item.state = session.tool == entry.tool ? .on : .off
            item.image = Self.icon(for: entry.tool).image(size: 16, label: Self.title(for: entry.tool))
            item.target = contextTarget
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let clear = NSMenuItem(
            title: "Clear all",
            action: #selector(ContextMenuTarget.clearAll),
            keyEquivalent: ""
        )
        clear.target = contextTarget
        menu.addItem(clear)
        let exit = NSMenuItem(
            title: "Exit draw mode",
            action: #selector(ContextMenuTarget.exitDrawing),
            keyEquivalent: "\u{1b}"
        )
        exit.keyEquivalentModifierMask = []
        exit.target = contextTarget
        menu.addItem(exit)

        // Escape and the tool letters belong to the menu while it is open.
        drawShortcuts.setEnabled(false)
        NSMenu.popUpContextMenu(menu, with: event, for: view)
        drawShortcuts.setEnabled(mode == .draw)
    }

    private lazy var contextTarget = ContextMenuTarget(
        selectTool: { [weak self] tool in self?.session.tool = tool },
        clearAll: { [weak self] in self?.clear() },
        exitDrawing: { [weak self] in self?.setMode(.passThrough) }
    )

    static func title(for tool: SketchTool) -> String {
        switch tool {
        case .pen: "Pen"
        case .eraser: "Eraser"
        case .line: "Line"
        case .arrow: "Arrow"
        case .rectangle: "Rectangle"
        case .ellipse: "Ellipse"
        }
    }

    static func icon(for tool: SketchTool) -> LucideIcon {
        switch tool {
        case .pen: .pencil
        case .eraser: .eraser
        case .line: .line
        case .arrow: .arrow
        case .rectangle: .rectangle
        case .ellipse: .ellipse
        }
    }

    var windowIDs: Set<CGWindowID> {
        Set(windows.map { CGWindowID($0.windowNumber) })
    }

    private func rebuildWindows() {
        windows.forEach { $0.close() }
        windows = NSScreen.screens.compactMap { screen in
            guard let displayID = screen.overInkDisplayID else { return nil }
            let window = OverlayWindow(
                screen: screen,
                displayID: displayID,
                session: session,
                onContextMenu: { [weak self] event, view in
                    self?.showContextMenu(for: event, in: view)
                }
            )
            window.isDrawingEnabled = mode == .draw
            window.orderFrontRegardless()
            return window
        }
    }
}

@MainActor
final class OverlayWindow: NSPanel {
    private let canvas: InkCanvasView

    var isDrawingEnabled: Bool = false {
        didSet {
            ignoresMouseEvents = !isDrawingEnabled
            canvas.isDrawingEnabled = isDrawingEnabled
        }
    }

    init(
        screen: NSScreen,
        displayID: CGDirectDisplayID,
        session: AnnotationSession,
        onContextMenu: @escaping (NSEvent, NSView) -> Void
    ) {
        canvas = InkCanvasView(displayID: displayID, session: session)
        canvas.onContextMenu = onContextMenu
        super.init(
            contentRect: screen.frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )

        setFrame(screen.frame, display: true)
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        contentView = canvas
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func refreshInk() {
        canvas.needsDisplay = true
    }

    func refreshCursor() {
        canvas.refreshCursor()
    }
}

/// Objective-C target for the overlay's right-click menu items.
@MainActor
final class ContextMenuTarget: NSObject {
    private let selectToolAction: (SketchTool) -> Void
    private let clearAllAction: () -> Void
    private let exitDrawingAction: () -> Void

    init(
        selectTool: @escaping (SketchTool) -> Void,
        clearAll: @escaping () -> Void,
        exitDrawing: @escaping () -> Void
    ) {
        selectToolAction = selectTool
        clearAllAction = clearAll
        exitDrawingAction = exitDrawing
    }

    @objc func selectTool(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let tool = SketchTool(rawValue: raw) else {
            return
        }
        selectToolAction(tool)
    }

    @objc func clearAll() { clearAllAction() }
    @objc func exitDrawing() { exitDrawingAction() }
}

@MainActor
final class InkCanvasView: NSView {
    var isDrawingEnabled = false {
        didSet { refreshCursor() }
    }
    var onContextMenu: ((NSEvent, NSView) -> Void)?
    private let displayID: CGDirectDisplayID
    private let session: AnnotationSession
    private var activeStroke: InkStroke?
    private var activeTool = SketchTool.pen
    private var activeStart: InkPoint?

    // Committed ink is rendered once into a bitmap and re-rendered only when the session's
    // strokes change, so dragging a new stroke never re-strokes everything already drawn.
    private var cachedInk: NSImage?
    private var cachedRevision = -1
    private var cachedScale: CGFloat = 0
    private var cachedSize = NSSize.zero

    init(displayID: CGDirectDisplayID, session: AnnotationSession) {
        self.displayID = displayID
        self.session = session
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(
            NSTrackingArea(
                rect: bounds,
                options: [.activeAlways, .cursorUpdate, .mouseMoved, .inVisibleRect],
                owner: self
            )
        )
    }

    override func cursorUpdate(with event: NSEvent) {
        applyCursor()
    }

    override func mouseMoved(with event: NSEvent) {
        applyCursor()
    }

    override func rightMouseDown(with event: NSEvent) {
        guard isDrawingEnabled else { return }
        onContextMenu?(event, self)
    }

    func refreshCursor() {
        window?.invalidateCursorRects(for: self)
        guard let window else { return }
        let mouse = window.mouseLocationOutsideOfEventStream
        if bounds.contains(convert(mouse, from: nil)) {
            applyCursor()
        }
    }

    private func applyCursor() {
        if isDrawingEnabled {
            PenCursor.cursor(diameter: session.cursorDiameter).set()
        } else {
            NSCursor.arrow.set()
        }
    }

    override func resetCursorRects() {
        super.resetCursorRects()
        if isDrawingEnabled {
            addCursorRect(bounds, cursor: PenCursor.cursor(diameter: session.cursorDiameter))
        }
    }

    override func mouseDown(with event: NSEvent) {
        guard isDrawingEnabled else { return }
        let inkPoint = inkPoint(for: event)
        activeTool = session.tool
        if activeTool == .eraser {
            session.erase(on: displayID, near: inkPoint, radius: session.eraserRadius)
            return
        }

        activeStart = inkPoint
        activeStroke = InkStroke(
            colorHex: session.colorHex,
            width: session.penWidth,
            points: [inkPoint],
            ignoresPressure: !session.pressureSensitive || activeTool != .pen
        )
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard isDrawingEnabled else { return }
        let current = inkPoint(for: event)
        if activeTool == .eraser {
            session.erase(on: displayID, near: current, radius: session.eraserRadius)
            return
        }
        guard activeStroke != nil, let activeStart else { return }
        if activeTool == .pen {
            activeStroke?.points.append(current)
        } else {
            activeStroke?.points = StrokeGeometry.shapePoints(
                tool: activeTool,
                from: activeStart,
                to: current,
                constrain: event.modifierFlags.contains(.shift)
            )
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard activeTool != .eraser else { return }
        guard var stroke = activeStroke else { return }
        let current = inkPoint(for: event)
        if activeTool == .pen {
            if let last = stroke.points.last, last.x != current.x || last.y != current.y {
                stroke.points.append(current)
            }
            StrokeSmoother.apply(to: &stroke)
        } else if let activeStart {
            stroke.points = StrokeGeometry.shapePoints(
                tool: activeTool,
                from: activeStart,
                to: current,
                constrain: event.modifierFlags.contains(.shift)
            )
        }
        activeStroke = nil
        activeStart = nil
        session.append(stroke, on: displayID)
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        guard let context = NSGraphicsContext.current?.cgContext else { return }
        if session.veilOpacity > 0 {
            context.setFillColor(NSColor.white.withAlphaComponent(session.veilOpacity).cgColor)
            context.fill(bounds)
        }
        if let image = committedInkImage() {
            image.draw(
                in: bounds,
                from: .zero,
                operation: .sourceOver,
                fraction: 1,
                respectFlipped: true,
                hints: nil
            )
        }
        for stroke in session.liveStrokes(for: displayID) {
            StrokeRenderer.draw(stroke, in: context)
        }
        if let activeStroke {
            StrokeRenderer.draw(activeStroke, in: context)
        }
    }

    private func inkPoint(for event: NSEvent) -> InkPoint {
        let point = convert(event.locationInWindow, from: nil)
        let reportedPressure = event.pressure
        let pressure: Float = reportedPressure > 0 ? reportedPressure : 1
        return InkPoint(x: point.x, y: point.y, pressure: pressure)
    }

    private func committedInkImage() -> NSImage? {
        let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2
        if cachedRevision == session.revision, cachedScale == scale, cachedSize == bounds.size {
            return cachedInk
        }
        cachedRevision = session.revision
        cachedScale = scale
        cachedSize = bounds.size

        let strokes = session.strokes(for: displayID)
        guard !strokes.isEmpty, bounds.width > 0, bounds.height > 0 else {
            cachedInk = nil
            return nil
        }

        let pixelWidth = Int((bounds.width * scale).rounded(.up))
        let pixelHeight = Int((bounds.height * scale).rounded(.up))
        guard let context = CGContext(
            data: nil,
            width: pixelWidth,
            height: pixelHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            cachedInk = nil
            return nil
        }
        // Match the flipped (top-left origin) coordinates strokes are stored in.
        context.translateBy(x: 0, y: CGFloat(pixelHeight))
        context.scaleBy(x: scale, y: -scale)
        strokes.forEach { StrokeRenderer.draw($0, in: context) }

        guard let image = context.makeImage() else {
            cachedInk = nil
            return nil
        }
        cachedInk = NSImage(cgImage: image, size: bounds.size)
        return cachedInk
    }
}
