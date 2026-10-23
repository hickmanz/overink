import AppKit
import OverInkCore

/// Full-desktop picker: drag to select any area (even across displays), or hover a window and click
/// to snap to it. Selections are in global point coordinates with a top-left origin.
@MainActor
final class RegionPickerController {
    struct Selection {
        let rect: CGRect
    }

    private var windows: [RegionPickerWindow] = []
    private var completion: ((Selection?) -> Void)?
    private var windowFrames: [PixelRect] = []
    private var dragStart: CGPoint?
    private var didDrag = false

    private(set) var selection: CGRect?
    private(set) var hoveredWindow: CGRect?
    private(set) var pointer: CGPoint = .zero

    func begin(completion: @escaping (Selection?) -> Void) {
        cancel()
        self.completion = completion
        windowFrames = WindowLister.onScreenWindowFrames()
        selection = nil
        hoveredWindow = nil
        dragStart = nil
        didDrag = false
        pointer = Self.globalPoint(fromCocoa: NSEvent.mouseLocation)
        hoveredWindow = WindowSnap.topmost(at: PixelPoint(x: pointer.x, y: pointer.y), in: windowFrames)?.cgRect

        windows = NSScreen.screens.compactMap { screen in
            guard let displayID = screen.overInkDisplayID else { return nil }
            let window = RegionPickerWindow(screen: screen, origin: CGDisplayBounds(displayID).origin)
            window.picker = self
            window.orderFrontRegardless()
            return window
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
        let mouse = NSEvent.mouseLocation
        (windows.first { $0.frame.contains(mouse) } ?? windows.first)?.makeKey()
        refresh()
    }

    func cancel() {
        windows.forEach { $0.close() }
        windows.removeAll()
        completion = nil
    }

    // MARK: Pointer input (global top-left coordinates)

    func pointerMoved(_ point: CGPoint) {
        pointer = point
        hoveredWindow = WindowSnap.topmost(at: PixelPoint(x: point.x, y: point.y), in: windowFrames)?.cgRect
        refresh()
    }

    func pointerDown(_ point: CGPoint) {
        dragStart = point
        didDrag = false
        selection = nil
        pointer = point
        refresh()
    }

    func pointerDragged(_ point: CGPoint) {
        guard let dragStart else { return }
        pointer = point
        if hypot(point.x - dragStart.x, point.y - dragStart.y) >= 4 { didDrag = true }
        if didDrag {
            selection = Self.normalizedRect(from: dragStart, to: point)
            hoveredWindow = nil
        }
        refresh()
    }

    func pointerUp(_ point: CGPoint) {
        guard let dragStart else { return }
        self.dragStart = nil
        if didDrag {
            let rect = Self.normalizedRect(from: dragStart, to: point)
            if rect.width >= 4, rect.height >= 4 {
                finish(Selection(rect: rect))
                return
            }
            selection = nil
        } else if let window = WindowSnap.topmost(
            at: PixelPoint(x: point.x, y: point.y),
            in: windowFrames
        ) {
            finish(Selection(rect: window.cgRect))
            return
        }
        refresh()
    }

    func userCancelled() {
        finish(nil)
    }

    private func finish(_ selection: Selection?) {
        let callback = completion
        completion = nil
        windows.forEach { $0.close() }
        windows.removeAll()
        callback?(selection)
    }

    private func refresh() {
        windows.forEach { $0.needsRefresh() }
    }

    // MARK: Coordinates

    static func globalPoint(fromCocoa point: NSPoint) -> CGPoint {
        CGPoint(x: point.x, y: CGDisplayBounds(CGMainDisplayID()).height - point.y)
    }

    private static func normalizedRect(from start: CGPoint, to end: CGPoint) -> CGRect {
        CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }
}

@MainActor
private final class RegionPickerWindow: NSPanel {
    weak var picker: RegionPickerController? {
        didSet { view.controller = picker }
    }
    private let view: RegionPickerView

    init(screen: NSScreen, origin: CGPoint) {
        view = RegionPickerView(displayOrigin: origin)
        super.init(
            contentRect: screen.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        setFrame(screen.frame, display: true)
        level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 2)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        contentView = view
    }

    override var canBecomeKey: Bool { true }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            picker?.userCancelled()
        } else {
            super.keyDown(with: event)
        }
    }

    func needsRefresh() {
        view.needsRefresh()
    }
}

@MainActor
private final class RegionPickerView: NSView {
    weak var controller: RegionPickerController?
    private let displayOrigin: CGPoint

    init(displayOrigin: CGPoint) {
        self.displayOrigin = displayOrigin
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func needsRefresh() {
        needsDisplay = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(
            NSTrackingArea(
                rect: bounds,
                options: [.activeAlways, .mouseMoved, .inVisibleRect],
                owner: self
            )
        )
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    private func globalPoint(for event: NSEvent) -> CGPoint {
        let local = convert(event.locationInWindow, from: nil)
        return CGPoint(x: local.x + displayOrigin.x, y: local.y + displayOrigin.y)
    }

    private func local(_ global: CGRect) -> CGRect {
        global.offsetBy(dx: -displayOrigin.x, dy: -displayOrigin.y)
    }

    override func mouseMoved(with event: NSEvent) {
        controller?.pointerMoved(globalPoint(for: event))
    }

    override func mouseDown(with event: NSEvent) {
        controller?.pointerDown(globalPoint(for: event))
    }

    override func mouseDragged(with event: NSEvent) {
        controller?.pointerDragged(globalPoint(for: event))
    }

    override func mouseUp(with event: NSEvent) {
        controller?.pointerUp(globalPoint(for: event))
    }

    override func rightMouseUp(with event: NSEvent) {
        controller?.userCancelled()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            controller?.userCancelled()
        } else {
            super.keyDown(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let controller else { return }
        let highlight = controller.selection ?? controller.hoveredWindow
        let highlightLocal = highlight.map(local)

        // Dim everything except the highlighted area.
        let dim = NSBezierPath(rect: bounds)
        if let highlightLocal, highlightLocal.intersects(bounds) {
            dim.append(NSBezierPath(roundedRect: highlightLocal, xRadius: 5, yRadius: 5))
            dim.windingRule = .evenOdd
        }
        NSColor.black.withAlphaComponent(0.28).setFill()
        dim.fill()

        if let highlightLocal {
            let isSnap = controller.selection == nil
            NSColor.white.withAlphaComponent(isSnap ? 0.10 : 0.06).setFill()
            NSBezierPath(roundedRect: highlightLocal, xRadius: 5, yRadius: 5).fill()
            NSColor.systemBlue.setStroke()
            let border = NSBezierPath(roundedRect: highlightLocal, xRadius: 5, yRadius: 5)
            border.lineWidth = isSnap ? 3 : 2
            border.stroke()
        }

        // Labels belong to the display the pointer is on.
        let pointerLocal = CGPoint(
            x: controller.pointer.x - displayOrigin.x,
            y: controller.pointer.y - displayOrigin.y
        )
        guard bounds.contains(pointerLocal) else { return }

        if let selection = controller.selection {
            let text = "\(Int(selection.width)) × \(Int(selection.height))"
            let anchor = local(selection)
            drawLabel(
                text,
                at: NSPoint(
                    x: min(max(anchor.minX, 12), bounds.maxX - 120),
                    y: min(max(anchor.minY - 30, 12), bounds.maxY - 36)
                )
            )
        } else if let hovered = controller.hoveredWindow {
            let anchor = local(hovered)
            drawLabel(
                "Click to capture this window  ·  drag for an area  ·  Esc to cancel",
                at: NSPoint(
                    x: min(max(anchor.minX + 12, 12), max(12, bounds.maxX - 440)),
                    y: min(max(anchor.minY + 12, 12), bounds.maxY - 36)
                )
            )
        } else {
            drawLabel(
                "Drag to select an area, or click a window  ·  Esc to cancel",
                at: NSPoint(x: bounds.midX - 190, y: 32)
            )
        }
    }

    private func drawLabel(_ text: String, at origin: NSPoint) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: NSColor.white
        ]
        let attributed = NSAttributedString(string: text, attributes: attributes)
        let size = attributed.size()
        let background = NSRect(
            x: origin.x - 10,
            y: origin.y - 6,
            width: size.width + 20,
            height: size.height + 12
        )
        NSColor.black.withAlphaComponent(0.68).setFill()
        NSBezierPath(roundedRect: background, xRadius: 9, yRadius: 9).fill()
        attributed.draw(at: origin)
    }
}
