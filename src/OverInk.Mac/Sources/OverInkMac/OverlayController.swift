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
        let point = convert(event.locationInWindow, from: nil)
        let inkPoint = inkPoint(point, event: event)
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
        let point = convert(event.locationInWindow, from: nil)
        let current = inkPoint(point, event: event)
        if activeTool == .eraser {
            session.erase(on: displayID, near: current, radius: max(8, session.penWidth * 2))
            return
        }
        guard activeStroke != nil, let activeStart else { return }
        if activeTool == .pen {
            activeStroke?.points.append(current)
        } else {
            activeStroke?.points = shapePoints(tool: activeTool, from: activeStart, to: current)
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard activeTool != .eraser else { return }
        guard var stroke = activeStroke else { return }
        let point = convert(event.locationInWindow, from: nil)
        let current = inkPoint(point, event: event)
        if activeTool == .pen {
            if stroke.points.last.map({ $0.x != point.x || $0.y != point.y }) ?? true {
                stroke.points.append(current)
            }
        } else if let activeStart {
            stroke.points = shapePoints(tool: activeTool, from: activeStart, to: current)
        }
        session.append(stroke, on: displayID)
        activeStroke = nil
        activeStart = nil
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        guard let context = NSGraphicsContext.current?.cgContext else { return }
        if session.veilOpacity > 0 {
            context.setFillColor(NSColor.white.withAlphaComponent(session.veilOpacity).cgColor)
            context.fill(bounds)
        }
        for stroke in session.strokes(for: displayID) {
            draw(stroke, in: context)
        }
        if let activeStroke {
            draw(activeStroke, in: context)
        }
    }

    private func inkPoint(_ point: NSPoint, event: NSEvent) -> InkPoint {
        let reportedPressure = event.pressure
        let pressure: Float = reportedPressure > 0 ? reportedPressure : 1
        return InkPoint(x: point.x, y: point.y, pressure: pressure)
    }

    private func draw(_ stroke: InkStroke, in context: CGContext) {
        guard let first = stroke.points.first else { return }

        context.saveGState()
        context.setStrokeColor(NSColor(hex: stroke.colorHex).cgColor)
        context.setFillColor(NSColor(hex: stroke.colorHex).cgColor)
        context.setLineCap(.round)
        context.setLineJoin(.round)

        if stroke.points.count == 1 {
            let diameter = pressureWidth(for: first, stroke: stroke)
            context.fillEllipse(
                in: CGRect(
                    x: first.x - diameter / 2,
                    y: first.y - diameter / 2,
                    width: diameter,
                    height: diameter
                )
            )
        } else {
            for (start, end) in zip(stroke.points, stroke.points.dropFirst()) {
                let width = (
                    pressureWidth(for: start, stroke: stroke)
                        + pressureWidth(for: end, stroke: stroke)
                ) / 2
                context.setLineWidth(width)
                context.beginPath()
                context.move(to: CGPoint(x: start.x, y: start.y))
                context.addLine(to: CGPoint(x: end.x, y: end.y))
                context.strokePath()
            }
        }
        context.restoreGState()
    }

    private func pressureWidth(for point: InkPoint, stroke: InkStroke) -> Double {
        guard !stroke.ignoresPressure else { return stroke.width }
        return stroke.width * (0.35 + 0.65 * Double(point.pressure))
    }

    private func shapePoints(tool: SketchTool, from start: InkPoint, to end: InkPoint) -> [InkPoint] {
        switch tool {
        case .pen, .eraser:
            [start, end]
        case .line:
            [start, end]
        case .arrow:
            arrowPoints(from: start, to: end)
        case .rectangle:
            [
                start,
                InkPoint(x: end.x, y: start.y),
                end,
                InkPoint(x: start.x, y: end.y),
                start
            ]
        case .ellipse:
            ellipsePoints(from: start, to: end)
        }
    }

    private func arrowPoints(from start: InkPoint, to end: InkPoint) -> [InkPoint] {
        let angle = atan2(end.y - start.y, end.x - start.x)
        let length = min(24, max(10, hypot(end.x - start.x, end.y - start.y) * 0.25))
        let spread = Double.pi / 7
        let left = InkPoint(
            x: end.x - length * cos(angle - spread),
            y: end.y - length * sin(angle - spread)
        )
        let right = InkPoint(
            x: end.x - length * cos(angle + spread),
            y: end.y - length * sin(angle + spread)
        )
        return [start, end, left, end, right]
    }

    private func ellipsePoints(from start: InkPoint, to end: InkPoint) -> [InkPoint] {
        let centerX = (start.x + end.x) / 2
        let centerY = (start.y + end.y) / 2
        let radiusX = abs(end.x - start.x) / 2
        let radiusY = abs(end.y - start.y) / 2
        return (0 ... 48).map { index in
            let angle = Double(index) / 48 * Double.pi * 2
            return InkPoint(
                x: centerX + cos(angle) * radiusX,
                y: centerY + sin(angle) * radiusY
            )
        }
    }
}
