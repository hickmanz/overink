import AppKit

@MainActor
final class RegionPickerController {
    struct Selection {
        let screen: NSScreen
        let rect: NSRect
    }

    private var windows: [RegionPickerWindow] = []
    private var completion: ((Selection?) -> Void)?

    func begin(completion: @escaping (Selection?) -> Void) {
        cancel()
        self.completion = completion
        windows = NSScreen.screens.map { screen in
            let window = RegionPickerWindow(screen: screen)
            window.onSelection = { [weak self] rect in
                self?.finish(Selection(screen: screen, rect: rect))
            }
            window.onCancel = { [weak self] in
                self?.finish(nil)
            }
            window.orderFrontRegardless()
            return window
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
        windows.first?.makeKey()
    }

    func cancel() {
        windows.forEach { $0.close() }
        windows.removeAll()
        completion = nil
    }

    private func finish(_ selection: Selection?) {
        let callback = completion
        completion = nil
        windows.forEach { $0.close() }
        windows.removeAll()
        callback?(selection)
    }
}

@MainActor
private final class RegionPickerWindow: NSPanel {
    var onSelection: ((NSRect) -> Void)? {
        didSet { picker.onSelection = onSelection }
    }
    var onCancel: (() -> Void)?
    private let picker = RegionPickerView()

    init(screen: NSScreen) {
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
        contentView = picker
        picker.onCancel = { [weak self] in self?.onCancel?() }
    }

    override var canBecomeKey: Bool { true }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }
}

@MainActor
private final class RegionPickerView: NSView {
    var onSelection: ((NSRect) -> Void)?
    var onCancel: (() -> Void)?
    private var dragStart: NSPoint?
    private var selection: NSRect?

    override var isFlipped: Bool { true }

    override func mouseDown(with event: NSEvent) {
        dragStart = convert(event.locationInWindow, from: nil)
        selection = nil
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStart else { return }
        selection = normalizedRect(from: dragStart, to: convert(event.locationInWindow, from: nil))
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let dragStart else { return }
        let rect = normalizedRect(from: dragStart, to: convert(event.locationInWindow, from: nil))
        self.dragStart = nil
        guard rect.width >= 4, rect.height >= 4 else {
            selection = nil
            needsDisplay = true
            return
        }
        onSelection?(rect)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.28).setFill()
        bounds.fill()

        if let selection {
            NSColor.white.withAlphaComponent(0.12).setFill()
            NSBezierPath(roundedRect: selection, xRadius: 5, yRadius: 5).fill()
            NSColor.systemBlue.setStroke()
            let border = NSBezierPath(roundedRect: selection, xRadius: 5, yRadius: 5)
            border.lineWidth = 2
            border.stroke()

            let size = "\(Int(selection.width)) × \(Int(selection.height))"
            drawLabel(size, at: NSPoint(x: selection.minX, y: max(12, selection.minY - 30)))
        } else {
            drawLabel(
                "Drag to select a capture area  ·  Esc to cancel",
                at: NSPoint(x: bounds.midX - 145, y: 32)
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

    private func normalizedRect(from start: NSPoint, to end: NSPoint) -> NSRect {
        NSRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }
}
