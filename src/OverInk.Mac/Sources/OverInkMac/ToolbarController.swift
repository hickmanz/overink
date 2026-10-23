import AppKit
import OverInkCore

@MainActor
final class ToolbarController: NSObject {
    private let panel: NSPanel
    private let modeButton: NSButton
    private let toolButton: NSButton
    private let colorButton: ColorSwatchButton
    private let strokeButton = NSButton()
    private let veilButton = NSButton()
    private let overlayController: OverlayController
    private let onCapture: () -> Void
    private let onOpenLibrary: () -> Void

    init(
        overlayController: OverlayController,
        onCapture: @escaping () -> Void,
        onOpenLibrary: @escaping () -> Void
    ) {
        self.overlayController = overlayController
        self.onCapture = onCapture
        self.onOpenLibrary = onOpenLibrary

        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 672, height: 58),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        modeButton = NSButton()
        toolButton = NSButton()
        colorButton = ColorSwatchButton()
        super.init()

        configurePanel()
        configureContent()
        updateToolButton()
        colorButton.color = NSColor(hex: overlayController.session.colorHex)
        updateMode(overlayController.mode)
    }

    func show() {
        positionOnMainScreen()
        panel.orderFrontRegardless()
    }

    var windowID: CGWindowID {
        CGWindowID(panel.windowNumber)
    }

    func updateMode(_ mode: OverlayController.InteractionMode) {
        switch mode {
        case .draw:
            modeButton.title = "Drawing"
            modeButton.image = LucideIcon.pencil.image(label: "Drawing")
            modeButton.contentTintColor = .white
            modeButton.layer?.backgroundColor = NSColor.systemBlue.cgColor
            modeButton.layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
            modeButton.toolTip = "Drawing mode · Escape to pass through"
        case .passThrough:
            modeButton.title = "Draw"
            modeButton.image = LucideIcon.pointer.image(label: "Start drawing")
            modeButton.contentTintColor = .labelColor
            modeButton.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.07).cgColor
            modeButton.layer?.borderColor = NSColor.white.withAlphaComponent(0.16).cgColor
            modeButton.toolTip = "Pass-through mode — click to draw"
        }
    }

    private func configurePanel() {
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1)
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.sharingType = .none
    }

    private func configureContent() {
        stylePill(modeButton)
        modeButton.target = self
        modeButton.action = #selector(toggleDrawing)

        styleIconButton(toolButton, icon: .pencil, label: "Drawing tool")
        toolButton.target = self
        toolButton.action = #selector(showToolMenu)

        colorButton.toolTip = "Color"
        colorButton.target = self
        colorButton.action = #selector(showColorMenu)

        styleIconButton(strokeButton, icon: .line, label: "Stroke width")
        strokeButton.target = self
        strokeButton.action = #selector(showStrokeMenu)
        styleIconButton(veilButton, icon: .contrast, label: "Screen veil")
        veilButton.target = self
        veilButton.action = #selector(showVeilMenu)
        let undoButton = makeButton(icon: .undo, label: "Undo", action: #selector(undo))
        let redoButton = makeButton(icon: .redo, label: "Redo", action: #selector(redo))
        let clearButton = makeButton(icon: .trash, label: "Clear annotations", action: #selector(clear))
        let captureButton = makeCaptureButton()
        let libraryButton = makeButton(icon: .images, label: "Capture library", action: #selector(openLibrary))
        let moreButton = makeButton(icon: .settings, label: "More", action: #selector(showMoreMenu))

        let stack = NSStackView(
            views: [
                modeButton,
                divider(),
                toolButton,
                makeButton(icon: .eraser, label: "Eraser", action: #selector(selectEraser)),
                divider(),
                colorButton,
                strokeButton,
                veilButton,
                divider(),
                undoButton,
                redoButton,
                clearButton,
                divider(),
                captureButton,
                libraryButton,
                moreButton
            ]
        )
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.distribution = .gravityAreas
        stack.spacing = 4
        stack.translatesAutoresizingMaskIntoConstraints = false

        let content = NSView(frame: panel.contentLayoutRect)
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -8),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 6),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -6)
        ])

        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.contentView = content
            glass.cornerRadius = 19
            glass.style = .regular
            if #available(macOS 27.0, *) {
                glass.effectIsInteractive = true
            }
            glass.tintColor = NSColor.black.withAlphaComponent(0.08)
            panel.contentView = glass
        } else {
            let material = NSVisualEffectView(frame: panel.contentLayoutRect)
            material.material = .popover
            material.blendingMode = .behindWindow
            material.state = .active
            material.wantsLayer = true
            material.layer?.cornerRadius = 19
            material.layer?.masksToBounds = true
            material.layer?.borderWidth = 0.75
            material.layer?.borderColor = NSColor.white.withAlphaComponent(0.22).cgColor
            content.frame = material.bounds
            content.autoresizingMask = [.width, .height]
            material.addSubview(content)
            panel.contentView = material
        }
    }

    private func stylePill(_ button: NSButton) {
        button.bezelStyle = .regularSquare
        button.isBordered = false
        button.imagePosition = .imageLeading
        button.font = .systemFont(ofSize: 12, weight: .semibold)
        button.wantsLayer = true
        button.layer?.cornerRadius = 11
        button.layer?.borderWidth = 1
        button.widthAnchor.constraint(equalToConstant: 88).isActive = true
        button.heightAnchor.constraint(equalToConstant: 36).isActive = true
    }

    private func styleIconButton(_ button: NSButton, icon: LucideIcon, label: String) {
        button.image = icon.image(label: label)
        button.toolTip = label
        button.bezelStyle = .regularSquare
        button.isBordered = false
        button.imagePosition = .imageOnly
        button.contentTintColor = .secondaryLabelColor
        button.wantsLayer = true
        button.layer?.cornerRadius = 10
        button.widthAnchor.constraint(equalToConstant: 36).isActive = true
        button.heightAnchor.constraint(equalToConstant: 36).isActive = true
    }

    private func makeButton(icon: LucideIcon, label: String, action: Selector) -> NSButton {
        let button = NSButton()
        styleIconButton(button, icon: icon, label: label)
        button.target = self
        button.action = action
        return button
    }

    private func makeCaptureButton() -> NSButton {
        let button = NSButton(title: "Capture", target: self, action: #selector(capture))
        button.image = LucideIcon.camera.image(size: 16, label: "Capture")
        button.imagePosition = .imageLeading
        button.font = .systemFont(ofSize: 12, weight: .semibold)
        button.contentTintColor = .white
        button.bezelStyle = .regularSquare
        button.isBordered = false
        button.wantsLayer = true
        button.layer?.cornerRadius = 11
        button.layer?.backgroundColor = NSColor.systemBlue.cgColor
        button.layer?.borderWidth = 0.75
        button.layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
        button.widthAnchor.constraint(equalToConstant: 92).isActive = true
        button.heightAnchor.constraint(equalToConstant: 36).isActive = true
        return button
    }

    private func divider() -> NSView {
        let box = NSBox()
        box.boxType = .separator
        box.alphaValue = 0.45
        box.heightAnchor.constraint(equalToConstant: 24).isActive = true
        return box
    }

    private func positionOnMainScreen() {
        guard let visibleFrame = NSScreen.main?.visibleFrame else { return }
        let frame = panel.frame
        panel.setFrameOrigin(
            NSPoint(
                x: visibleFrame.midX - frame.width / 2,
                y: visibleFrame.maxY - frame.height - 24
            )
        )
    }

    @objc private func toggleDrawing() {
        overlayController.toggleMode()
    }

    @objc private func showToolMenu() {
        let menu = NSMenu()
        addTool("Pen", icon: .pencil, tool: .pen, to: menu)
        addTool("Line", icon: .line, tool: .line, to: menu)
        addTool("Arrow", icon: .arrow, tool: .arrow, to: menu)
        addTool("Rectangle", icon: .rectangle, tool: .rectangle, to: menu)
        addTool("Ellipse", icon: .ellipse, tool: .ellipse, to: menu)
        menu.addItem(.separator())
        addTool("Eraser", icon: .eraser, tool: .eraser, to: menu)
        popUp(menu, from: toolButton)
    }

    private func addTool(_ title: String, icon: LucideIcon, tool: SketchTool, to menu: NSMenu) {
        let item = NSMenuItem(title: title, action: #selector(selectTool(_:)), keyEquivalent: "")
        item.target = self
        item.image = icon.image(size: 16, label: title)
        item.representedObject = tool.rawValue
        item.state = overlayController.session.tool == tool ? .on : .off
        menu.addItem(item)
    }

    @objc private func selectTool(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let tool = SketchTool(rawValue: raw) else {
            return
        }
        overlayController.session.tool = tool
        updateToolButton()
    }

    @objc private func selectEraser() {
        overlayController.session.tool = .eraser
        updateToolButton()
    }

    private func updateToolButton() {
        let tool = overlayController.session.tool
        let icon: LucideIcon
        switch tool {
        case .pen: icon = .pencil
        case .eraser: icon = .eraser
        case .line: icon = .line
        case .arrow: icon = .arrow
        case .rectangle: icon = .rectangle
        case .ellipse: icon = .ellipse
        }
        toolButton.image = icon.image(label: tool.rawValue.capitalized)
        toolButton.toolTip = tool.rawValue.capitalized
        toolButton.contentTintColor = .systemBlue
        toolButton.layer?.backgroundColor = NSColor.systemBlue.withAlphaComponent(0.13).cgColor
    }

    @objc private func showColorMenu() {
        let menu = NSMenu()
        let colors = [
            ("Red", "#E53935"),
            ("Orange", "#FB8C00"),
            ("Yellow", "#FDD835"),
            ("Green", "#43A047"),
            ("Blue", "#1E88E5"),
            ("Purple", "#8E24AA"),
            ("White", "#FFFFFF"),
            ("Black", "#000000")
        ]
        for (name, hex) in colors {
            let item = NSMenuItem(title: name, action: #selector(selectColor(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = hex
            item.image = ColorSwatchButton.swatchImage(color: NSColor(hex: hex), size: 14)
            item.state = overlayController.session.colorHex == hex ? .on : .off
            menu.addItem(item)
        }
        popUp(menu, from: colorButton)
    }

    @objc private func selectColor(_ sender: NSMenuItem) {
        guard let hex = sender.representedObject as? String else { return }
        overlayController.session.colorHex = hex
        colorButton.color = NSColor(hex: hex)
    }

    @objc private func showStrokeMenu() {
        let menu = NSMenu()
        [2.0, 4.0, 8.0, 12.0, 18.0].forEach { width in
            let item = NSMenuItem(
                title: "\(Int(width)) pt",
                action: #selector(selectStrokeWidth(_:)),
                keyEquivalent: ""
            )
            item.target = self
            item.representedObject = width
            item.state = overlayController.session.penWidth == width ? .on : .off
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let pressure = NSMenuItem(
            title: "Pressure sensitivity",
            action: #selector(togglePressure),
            keyEquivalent: ""
        )
        pressure.target = self
        pressure.state = overlayController.session.pressureSensitive ? .on : .off
        menu.addItem(pressure)
        popUp(menu, from: strokeButton)
    }

    @objc private func selectStrokeWidth(_ sender: NSMenuItem) {
        guard let width = sender.representedObject as? Double else { return }
        overlayController.session.penWidth = width
    }

    @objc private func togglePressure() {
        overlayController.session.pressureSensitive.toggle()
    }

    @objc private func showVeilMenu() {
        let menu = NSMenu()
        [(0.0, "Off"), (0.15, "15%"), (0.3, "30%"), (0.45, "45%"), (0.6, "60%")]
            .forEach { opacity, title in
                let item = NSMenuItem(title: title, action: #selector(selectVeil(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = opacity
                item.state = abs(overlayController.session.veilOpacity - opacity) < 0.01 ? .on : .off
                menu.addItem(item)
            }
        popUp(menu, from: veilButton)
    }

    @objc private func selectVeil(_ sender: NSMenuItem) {
        guard let opacity = sender.representedObject as? Double else { return }
        overlayController.session.veilOpacity = opacity
    }

    @objc private func undo() {
        overlayController.undo()
    }

    @objc private func redo() {
        overlayController.redo()
    }

    @objc private func clear() {
        overlayController.clear()
    }

    @objc private func capture() {
        onCapture()
    }

    @objc private func openLibrary() {
        onOpenLibrary()
    }

    @objc private func showMoreMenu() {
        let menu = NSMenu()
        let permission = NSMenuItem(
            title: "Screen Recording Settings…",
            action: #selector(openScreenRecordingSettings),
            keyEquivalent: ""
        )
        permission.target = self
        menu.addItem(permission)
        menu.addItem(.separator())
        let hideItem = NSMenuItem(title: "Hide Toolbar", action: #selector(hide), keyEquivalent: "")
        hideItem.target = self
        hideItem.image = LucideIcon.minus.image(size: 16, label: "Hide")
        menu.addItem(hideItem)
        popUp(menu, from: panel.contentView ?? NSView())
    }

    @objc private func openScreenRecordingSettings() {
        guard let url = URL(
            string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
        ) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func hide() {
        panel.orderOut(nil)
    }

    private func popUp(_ menu: NSMenu, from view: NSView) {
        menu.popUp(
            positioning: nil,
            at: NSPoint(x: 0, y: view.bounds.height + 6),
            in: view
        )
    }
}

@MainActor
private final class ColorSwatchButton: NSButton {
    var color = NSColor.systemRed {
        didSet { needsDisplay = true }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        title = ""
        isBordered = false
        bezelStyle = .regularSquare
        toolTip = "Color"
        widthAnchor.constraint(equalToConstant: 36).isActive = true
        heightAnchor.constraint(equalToConstant: 36).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let circle = bounds.insetBy(dx: 8, dy: 8)
        color.setFill()
        NSBezierPath(ovalIn: circle).fill()
        NSColor.white.withAlphaComponent(0.8).setStroke()
        let border = NSBezierPath(ovalIn: circle)
        border.lineWidth = 1.5
        border.stroke()
    }

    static func swatchImage(color: NSColor, size: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: false) { bounds in
            color.setFill()
            NSBezierPath(ovalIn: bounds.insetBy(dx: 1, dy: 1)).fill()
            return true
        }
    }
}

extension NSColor {
    convenience init(hex: String) {
        let value = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard value.count == 6, let rgb = Int(value, radix: 16) else {
            self.init(srgbRed: 1, green: 0, blue: 0, alpha: 1)
            return
        }
        self.init(
            srgbRed: CGFloat((rgb >> 16) & 0xff) / 255,
            green: CGFloat((rgb >> 8) & 0xff) / 255,
            blue: CGFloat(rgb & 0xff) / 255,
            alpha: 1
        )
    }
}
