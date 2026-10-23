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

    init() {
        session.changed = { [weak self] in
            self?.windows.forEach { $0.refreshInk() }
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
        modeDidChange?(newMode)
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

    var windowIDs: Set<CGWindowID> {
        Set(windows.map { CGWindowID($0.windowNumber) })
    }

    private func rebuildWindows() {
        windows.forEach { $0.close() }
        windows = NSScreen.screens.compactMap { screen in
            guard let displayID = screen.overInkDisplayID else { return nil }
            let window = OverlayWindow(screen: screen, displayID: displayID, session: session)
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

    init(screen: NSScreen, displayID: CGDirectDisplayID, session: AnnotationSession) {
        canvas = InkCanvasView(displayID: displayID, session: session)
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
}

@MainActor
final class InkCanvasView: NSView {
    var isDrawingEnabled = false
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

    override func mouseDown(with event: NSEvent) {
        guard isDrawingEnabled else { return }
        let inkPoint = inkPoint(for: event)
        activeTool = session.tool
        if activeTool == .eraser {
            session.erase(on: displayID, near: inkPoint, radius: max(8, session.penWidth * 2))
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
            session.erase(on: displayID, near: current, radius: max(8, session.penWidth * 2))
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
