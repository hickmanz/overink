import AppKit
import OverInkCore

@MainActor
final class ToolbarController: NSObject, NSWindowDelegate {
    private let panel: NSPanel
    private let content = NSView()
    private let stack = NSStackView()
    private let grip = GripView()
    private let modeButton = NSButton()
    private let toolFlyoutButton = NSButton()
    private var toolButtons: [SketchTool: NSButton] = [:]
    private let eraserButton = NSButton()
    private let colorButton = ColorSwatchButton()
    private let strokeButton = NSButton()
    private let veilButton = NSButton()
    private let undoButton = NSButton()
    private let redoButton = NSButton()
    private let clearButton = NSButton()
    private let captureButton = NSButton()
    private let captureMenuButton = NSButton()
    private let libraryButton = NSButton()
    private let moreButton = NSButton()
    private var dividers: [DividerView] = []
    private var modeWidth: NSLayoutConstraint?
    private var captureWidth: NSLayoutConstraint?

    private let overlayController: OverlayController
    private let settings: SettingsModel
    private let onCapture: () -> Void
    private let onCaptureRegion: () -> Void
    private let onOpenLibrary: () -> Void

    private var laidOutVertical: Bool?
    private var laidOutCompact: Bool?
    private var lastShapeTool = SketchTool.pen
    private var savedColorsSnapshot: [String] = []
    private var colorPanelObserver: NSObjectProtocol?
    private var screenObserver: NSObjectProtocol?

    private static let shapeTools: [SketchTool] = [.pen, .line, .arrow, .rectangle, .ellipse]

    init(
        overlayController: OverlayController,
        settings: SettingsModel,
        onCapture: @escaping () -> Void,
        onCaptureRegion: @escaping () -> Void,
        onOpenLibrary: @escaping () -> Void
    ) {
        self.overlayController = overlayController
        self.settings = settings
        self.onCapture = onCapture
        self.onCaptureRegion = onCaptureRegion
        self.onOpenLibrary = onOpenLibrary

        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 700, height: 52),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        super.init()

        configurePanel()
        configureControls()
        configureContainer()
        if overlayController.session.tool != .eraser {
            lastShapeTool = overlayController.session.tool
        }
        applyLayout()
        refreshColor()
        refreshToolSelection()
        updateMode(overlayController.mode)

        overlayController.session.observeSettings { [weak self] in
            self?.refreshColor()
            self?.refreshToolSelection()
        }
        settings.observe { [weak self] in
            self?.applyLayout()
        }
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.recoverIfOffscreen()
            }
        }
    }

    // MARK: Public

    var isVisible: Bool { panel.isVisible }

    var windowID: CGWindowID {
        CGWindowID(panel.windowNumber)
    }

    func show() {
        if let x = settings.value.toolbarX, let y = settings.value.toolbarY {
            panel.setFrameOrigin(NSPoint(x: x, y: y))
        } else if !panel.isVisible {
            positionOnMainScreen()
        }
        recoverIfOffscreen()
        panel.orderFrontRegardless()
    }

    func hide() {
        panel.orderOut(nil)
    }

    func updateMode(_ mode: OverlayController.InteractionMode) {
        let compact = settings.value.toolbarVertical
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
        modeButton.imagePosition = compact ? .imageOnly : .imageLeading
    }

    // MARK: Setup

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
        panel.delegate = self
    }

    private func configureControls() {
        stylePill(modeButton, width: 88)
        modeWidth = modeButton.constraints.first { $0.firstAttribute == .width }
        modeButton.target = self
        modeButton.action = #selector(toggleDrawing)

        styleIconButton(toolFlyoutButton, icon: .pencil, label: "Drawing tool")
        toolFlyoutButton.target = self
        toolFlyoutButton.action = #selector(showToolMenu)

        for tool in Self.shapeTools {
            let button = NSButton()
            styleIconButton(button, icon: OverlayController.icon(for: tool), label: toolTooltip(tool))
            button.identifier = NSUserInterfaceItemIdentifier(tool.rawValue)
            button.target = self
            button.action = #selector(selectToolButton(_:))
            toolButtons[tool] = button
        }
        styleIconButton(eraserButton, icon: .eraser, label: toolTooltip(.eraser))
        eraserButton.target = self
        eraserButton.action = #selector(selectEraser)

        colorButton.toolTip = "Color"
        colorButton.target = self
        colorButton.action = #selector(showColorMenu)

        styleIconButton(strokeButton, icon: .line, label: "Stroke width")
        strokeButton.target = self
        strokeButton.action = #selector(showStrokeMenu)
        styleIconButton(veilButton, icon: .contrast, label: "Screen veil")
        veilButton.target = self
        veilButton.action = #selector(showVeilMenu)

        styleIconButton(undoButton, icon: .undo, label: "Undo (⌘Z)")
        undoButton.target = self
        undoButton.action = #selector(undo)
        styleIconButton(redoButton, icon: .redo, label: "Redo (⇧⌘Z)")
        redoButton.target = self
        redoButton.action = #selector(redo)
        styleIconButton(clearButton, icon: .trash, label: "Clear annotations")
        clearButton.target = self
        clearButton.action = #selector(clear)

        styleCaptureButton()
        styleIconButton(captureMenuButton, icon: .chevronDown, label: "Capture options")
        captureMenuButton.target = self
        captureMenuButton.action = #selector(showCaptureMenu)
        styleIconButton(libraryButton, icon: .images, label: "Capture library")
        libraryButton.target = self
        libraryButton.action = #selector(openLibrary)
        styleIconButton(moreButton, icon: .settings, label: "More")
        moreButton.target = self
        moreButton.action = #selector(showMoreMenu)

        grip.onDoubleClick = { [weak self] in
            guard let self else { return }
            self.settings.update { $0.toolbarVertical.toggle() }
        }

        stack.distribution = .gravityAreas
        stack.spacing = 4
        stack.translatesAutoresizingMaskIntoConstraints = false
    }

    private func configureContainer() {
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -8),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 8),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -8)
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
            let material = NSVisualEffectView()
            material.material = .popover
            material.blendingMode = .behindWindow
            material.state = .active
            material.wantsLayer = true
            material.layer?.cornerRadius = 19
            material.layer?.masksToBounds = true
            material.layer?.borderWidth = 0.75
            material.layer?.borderColor = NSColor.white.withAlphaComponent(0.22).cgColor
            content.translatesAutoresizingMaskIntoConstraints = false
            material.addSubview(content)
            NSLayoutConstraint.activate([
                content.leadingAnchor.constraint(equalTo: material.leadingAnchor),
                content.trailingAnchor.constraint(equalTo: material.trailingAnchor),
                content.topAnchor.constraint(equalTo: material.topAnchor),
                content.bottomAnchor.constraint(equalTo: material.bottomAnchor)
            ])
            panel.contentView = material
        }
    }

    // MARK: Layout (orientation and compact/expanded tools)

    private func applyLayout() {
        let vertical = settings.value.toolbarVertical
        let compact = settings.value.compactTools
        refreshColor()
        updateMode(overlayController.mode)
        guard vertical != laidOutVertical || compact != laidOutCompact else { return }
        let orientationChanged = vertical != laidOutVertical
        laidOutVertical = vertical
        laidOutCompact = compact

        // Keep the top-left corner (where the grip lives) fixed across relayouts.
        let topLeft = NSPoint(x: panel.frame.minX, y: panel.frame.maxY)

        stack.orientation = vertical ? .vertical : .horizontal
        stack.alignment = vertical ? .centerX : .centerY
        stack.arrangedSubviews.forEach {
            stack.removeArrangedSubview($0)
            $0.removeFromSuperview()
        }
        dividers = []

        func divider() -> DividerView {
            let view = DividerView(vertical: !vertical)
            dividers.append(view)
            return view
        }

        var views: [NSView] = [grip, modeButton, divider()]
        if compact {
            views.append(toolFlyoutButton)
        } else {
            views.append(contentsOf: Self.shapeTools.compactMap { toolButtons[$0] })
        }
        views.append(eraserButton)
        views.append(contentsOf: [divider(), colorButton, strokeButton, veilButton])
        views.append(contentsOf: [divider(), undoButton, redoButton, clearButton])
        views.append(contentsOf: [divider(), captureButton, captureMenuButton, libraryButton, moreButton])
        views.forEach { stack.addArrangedSubview($0) }

        grip.isVertical = !vertical
        grip.needsDisplay = true
        modeWidth?.constant = vertical ? 36 : 88
        captureWidth?.constant = vertical ? 36 : 92
        captureButton.imagePosition = vertical ? .imageOnly : .imageLeading
        modeButton.imagePosition = vertical ? .imageOnly : .imageLeading

        content.layoutSubtreeIfNeeded()
        let size = content.fittingSize
        panel.setContentSize(size)
        if orientationChanged || panel.isVisible {
            panel.setFrameOrigin(NSPoint(x: topLeft.x, y: topLeft.y - panel.frame.height))
            recoverIfOffscreen()
        }
        refreshToolSelection()
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

    /// Pulls the toolbar back onto a display after a display was removed or rearranged.
    private func recoverIfOffscreen() {
        let frame = panel.frame
        let recovered = WindowPlacement.recover(
            frame: PixelRect(x: frame.minX, y: frame.minY, width: frame.width, height: frame.height),
            visibleFrames: NSScreen.screens.map {
                PixelRect(
                    x: $0.visibleFrame.minX,
                    y: $0.visibleFrame.minY,
                    width: $0.visibleFrame.width,
                    height: $0.visibleFrame.height
                )
            }
        )
        guard recovered.x != frame.minX || recovered.y != frame.minY else { return }
        panel.setFrameOrigin(NSPoint(x: recovered.x, y: recovered.y))
    }

    // MARK: Styling

    private func stylePill(_ button: NSButton, width: CGFloat) {
        button.bezelStyle = .regularSquare
        button.isBordered = false
        button.imagePosition = .imageLeading
        button.font = .systemFont(ofSize: 12, weight: .semibold)
        button.wantsLayer = true
        button.layer?.cornerRadius = 11
        button.layer?.borderWidth = 1
        button.widthAnchor.constraint(equalToConstant: width).isActive = true
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

    private func styleCaptureButton() {
        captureButton.title = "Capture"
        captureButton.target = self
        captureButton.action = #selector(capture)
        captureButton.image = LucideIcon.camera.image(size: 16, label: "Capture")
        captureButton.imagePosition = .imageLeading
        captureButton.font = .systemFont(ofSize: 12, weight: .semibold)
        captureButton.contentTintColor = .white
        captureButton.bezelStyle = .regularSquare
        captureButton.isBordered = false
        captureButton.toolTip = "Capture"
        captureButton.wantsLayer = true
        captureButton.layer?.cornerRadius = 11
        captureButton.layer?.backgroundColor = NSColor.systemBlue.cgColor
        captureButton.layer?.borderWidth = 0.75
        captureButton.layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
        let width = captureButton.widthAnchor.constraint(equalToConstant: 92)
        width.isActive = true
        captureWidth = width
        captureButton.heightAnchor.constraint(equalToConstant: 36).isActive = true
    }

    private func toolTooltip(_ tool: SketchTool) -> String {
        let name = OverlayController.title(for: tool)
        guard let letter = DrawShortcutMap.letter(for: tool) else { return name }
        return "\(name) (\(letter))"
    }

    private func setSelected(_ button: NSButton, _ selected: Bool) {
        button.contentTintColor = selected ? .systemBlue : .secondaryLabelColor
        button.layer?.backgroundColor = selected
            ? NSColor.systemBlue.withAlphaComponent(0.13).cgColor
            : NSColor.clear.cgColor
    }

    private func refreshToolSelection() {
        let tool = overlayController.session.tool
        if tool != .eraser { lastShapeTool = tool }

        toolFlyoutButton.image = OverlayController.icon(for: lastShapeTool)
            .image(label: toolTooltip(lastShapeTool))
        toolFlyoutButton.toolTip = toolTooltip(lastShapeTool) + " · click for more tools"
        setSelected(toolFlyoutButton, tool != .eraser)
        setSelected(eraserButton, tool == .eraser)
        for (candidate, button) in toolButtons {
            setSelected(button, candidate == tool)
        }
    }

    private func refreshColor() {
        colorButton.color = NSColor(hex: overlayController.session.colorHex)
    }

    // MARK: Actions

    @objc private func toggleDrawing() {
        overlayController.toggleMode()
    }

    @objc private func selectToolButton(_ sender: NSButton) {
        guard let raw = sender.identifier?.rawValue, let tool = SketchTool(rawValue: raw) else { return }
        overlayController.session.tool = tool
    }

    @objc private func selectEraser() {
        overlayController.session.tool = .eraser
    }

    @objc private func showToolMenu() {
        let menu = NSMenu()
        for tool in Self.shapeTools + [.eraser] {
            if tool == .eraser { menu.addItem(.separator()) }
            let item = NSMenuItem(title: toolTooltip(tool), action: #selector(selectTool(_:)), keyEquivalent: "")
            item.target = self
            item.image = OverlayController.icon(for: tool).image(size: 16, label: toolTooltip(tool))
            item.representedObject = tool.rawValue
            item.state = overlayController.session.tool == tool ? .on : .off
            menu.addItem(item)
        }
        popUp(menu, from: toolFlyoutButton)
    }

    @objc private func selectTool(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let tool = SketchTool(rawValue: raw) else {
            return
        }
        overlayController.session.tool = tool
    }

    @objc private func showColorMenu() {
        let session = overlayController.session
        let palette = CustomColorPalette(normalizing: settings.value.customColors)
        let menu = NSMenu()

        let grid = SwatchGridView(
            presets: PresetPalette.hexColors,
            custom: palette.colors,
            selected: session.colorHex
        )
        grid.onSelect = { [weak self, weak menu] hex in
            self?.overlayController.session.colorHex = hex
            menu?.cancelTracking()
        }
        let gridItem = NSMenuItem()
        gridItem.view = grid
        menu.addItem(gridItem)
        menu.addItem(.separator())

        let custom = NSMenuItem(title: "Custom color…", action: #selector(openColorPanel), keyEquivalent: "")
        custom.target = self
        menu.addItem(custom)

        let save = NSMenuItem(title: "Save current color", action: #selector(saveCurrentColor), keyEquivalent: "")
        save.target = self
        save.isEnabled = !palette.hasSwatch(session.colorHex) && palette.colors.count < CustomColorPalette.maxColors
        menu.addItem(save)

        if !palette.colors.isEmpty {
            let removeItem = NSMenuItem(title: "Remove saved color", action: nil, keyEquivalent: "")
            let submenu = NSMenu()
            for hex in palette.colors {
                let item = NSMenuItem(title: hex, action: #selector(removeSavedColor(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = hex
                item.image = ColorSwatchButton.swatchImage(color: NSColor(hex: hex), size: 14)
                submenu.addItem(item)
            }
            removeItem.submenu = submenu
            menu.addItem(removeItem)
        }
        popUp(menu, from: colorButton)
    }

    @objc private func saveCurrentColor() {
        saveColor(overlayController.session.colorHex)
    }

    private func saveColor(_ hex: String) {
        settings.update {
            var palette = CustomColorPalette(normalizing: $0.customColors)
            palette.add(hex)
            $0.customColors = palette.colors
        }
    }

    @objc private func removeSavedColor(_ sender: NSMenuItem) {
        guard let hex = sender.representedObject as? String else { return }
        settings.update {
            var palette = CustomColorPalette(normalizing: $0.customColors)
            palette.remove(hex)
            $0.customColors = palette.colors
        }
    }

    @objc private func openColorPanel() {
        let colorPanel = NSColorPanel.shared
        colorPanel.mode = .wheel
        colorPanel.showsAlpha = false
        colorPanel.color = NSColor(hex: overlayController.session.colorHex)
        colorPanel.setTarget(self)
        colorPanel.setAction(#selector(colorPanelChanged(_:)))
        // The overlays sit at screen-saver level; the picker must float above them.
        colorPanel.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 2)
        colorPanel.orderFrontRegardless()

        // A color picked on the wheel is saved as a swatch when the picker closes.
        if let colorPanelObserver { NotificationCenter.default.removeObserver(colorPanelObserver) }
        colorPanelObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification,
            object: colorPanel,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let hex = self.overlayController.session.colorHex
                if !CustomColorPalette(normalizing: self.settings.value.customColors).hasSwatch(hex) {
                    self.saveColor(hex)
                }
                if let observer = self.colorPanelObserver {
                    NotificationCenter.default.removeObserver(observer)
                    self.colorPanelObserver = nil
                }
                NSColorPanel.shared.setTarget(nil)
                NSColorPanel.shared.setAction(nil)
            }
        }
    }

    @objc private func colorPanelChanged(_ sender: NSColorPanel) {
        overlayController.session.colorHex = sender.color.hexRGB
    }

    @objc private func showStrokeMenu() {
        let session = overlayController.session
        let menu = NSMenu()

        let sliderItem = NSMenuItem()
        let slider = MenuSliderView(
            title: "Width",
            range: 1 ... 24,
            value: session.penWidth,
            format: { "\(Int($0.rounded())) pt" }
        )
        slider.onChange = { [weak self] value in
            self?.overlayController.session.penWidth = value.rounded()
        }
        sliderItem.view = slider
        menu.addItem(sliderItem)
        menu.addItem(.separator())

        let pressure = NSMenuItem(
            title: "Pressure sensitivity",
            action: #selector(togglePressure),
            keyEquivalent: ""
        )
        pressure.target = self
        pressure.state = session.pressureSensitive ? .on : .off
        menu.addItem(pressure)
        popUp(menu, from: strokeButton)
    }

    @objc private func togglePressure() {
        overlayController.session.pressureSensitive.toggle()
    }

    @objc private func showVeilMenu() {
        let session = overlayController.session
        let menu = NSMenu()
        let sliderItem = NSMenuItem()
        let slider = MenuSliderView(
            title: "Screen veil",
            range: 0 ... 0.6,
            value: session.veilOpacity,
            format: { $0 < 0.005 ? "Off" : "\(Int(($0 * 100).rounded()))%" }
        )
        slider.onChange = { [weak self] value in
            self?.overlayController.session.veilOpacity = value
        }
        sliderItem.view = slider
        menu.addItem(sliderItem)
        popUp(menu, from: veilButton)
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

    @objc private func showCaptureMenu(_ sender: NSButton) {
        let menu = NSMenu()
        let full = NSMenuItem(title: "Capture full display", action: #selector(capture), keyEquivalent: "")
        full.target = self
        full.image = LucideIcon.camera.image(size: 16, label: "Full display")
        menu.addItem(full)

        let region = NSMenuItem(title: "Select region…", action: #selector(captureRegion), keyEquivalent: "")
        region.target = self
        region.image = LucideIcon.rectangle.image(size: 16, label: "Select region")
        menu.addItem(region)
        popUp(menu, from: sender)
    }

    @objc private func captureRegion() {
        onCaptureRegion()
    }

    @objc private func openLibrary() {
        onOpenLibrary()
    }

    @objc private func showMoreMenu() {
        let menu = NSMenu()
        let value = settings.value

        func toggle(_ title: String, isOn: Bool, action: Selector) {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
            item.target = self
            item.state = isOn ? .on : .off
            menu.addItem(item)
        }

        toggle("Compact tool buttons", isOn: value.compactTools, action: #selector(toggleCompactTools))
        toggle("Vertical toolbar", isOn: value.toolbarVertical, action: #selector(toggleVertical))
        toggle(
            "Hide toolbar when not drawing",
            isOn: value.hideToolbarWhenNotDrawing,
            action: #selector(toggleAutoHide)
        )
        menu.addItem(.separator())
        toggle("Copy after capture", isOn: value.copyAfterCapture, action: #selector(toggleCopyAfterCapture))
        toggle(
            "Exit draw mode after capture",
            isOn: value.exitDrawModeAfterCapture,
            action: #selector(toggleExitAfterCapture)
        )
        toggle(
            "Clear drawing after capture",
            isOn: value.clearAfterCapture,
            action: #selector(toggleClearAfterCapture)
        )
        menu.addItem(.separator())

        let permission = NSMenuItem(
            title: "Screen Recording Settings…",
            action: #selector(openScreenRecordingSettings),
            keyEquivalent: ""
        )
        permission.target = self
        menu.addItem(permission)
        menu.addItem(.separator())
        let hideItem = NSMenuItem(title: "Hide Toolbar", action: #selector(hideFromMenu), keyEquivalent: "")
        hideItem.target = self
        hideItem.image = LucideIcon.minus.image(size: 16, label: "Hide")
        menu.addItem(hideItem)
        popUp(menu, from: moreButton)
    }

    @objc private func toggleCompactTools() {
        settings.update { $0.compactTools.toggle() }
    }

    @objc private func toggleVertical() {
        settings.update { $0.toolbarVertical.toggle() }
    }

    @objc private func toggleAutoHide() {
        settings.update { $0.hideToolbarWhenNotDrawing.toggle() }
    }

    @objc private func toggleCopyAfterCapture() {
        settings.update { $0.copyAfterCapture.toggle() }
    }

    @objc private func toggleExitAfterCapture() {
        settings.update { $0.exitDrawModeAfterCapture.toggle() }
    }

    @objc private func toggleClearAfterCapture() {
        settings.update { $0.clearAfterCapture.toggle() }
    }

    @objc private func openScreenRecordingSettings() {
        guard let url = URL(
            string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
        ) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func hideFromMenu() {
        hide()
    }

    private func popUp(_ menu: NSMenu, from view: NSView) {
        let vertical = settings.value.toolbarVertical
        menu.popUp(
            positioning: nil,
            at: vertical
                ? NSPoint(x: view.bounds.width + 6, y: view.bounds.height)
                : NSPoint(x: 0, y: view.bounds.height + 6),
            in: view
        )
    }

    // MARK: NSWindowDelegate

    func windowDidMove(_ notification: Notification) {
        let origin = panel.frame.origin
        settings.update {
            $0.toolbarX = origin.x
            $0.toolbarY = origin.y
        }
    }
}

// MARK: - Views

/// The drag handle at the toolbar's leading edge. Double-click flips the toolbar's orientation.
@MainActor
private final class GripView: NSView {
    var isVertical = false
    var onDoubleClick: (() -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        toolTip = "Drag to move · double-click to flip"
        widthAnchor.constraint(equalToConstant: 20).isActive = true
        heightAnchor.constraint(equalToConstant: 20).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .openHand)
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount >= 2 {
            onDoubleClick?()
        } else {
            window?.performDrag(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.secondaryLabelColor.withAlphaComponent(0.7).setFill()
        let columns = isVertical ? 3 : 2
        let rows = isVertical ? 2 : 3
        let dot: CGFloat = 3
        let spacing: CGFloat = 3
        let width = CGFloat(columns) * dot + CGFloat(columns - 1) * spacing
        let height = CGFloat(rows) * dot + CGFloat(rows - 1) * spacing
        let originX = (bounds.width - width) / 2
        let originY = (bounds.height - height) / 2
        for column in 0 ..< columns {
            for row in 0 ..< rows {
                let rect = NSRect(
                    x: originX + CGFloat(column) * (dot + spacing),
                    y: originY + CGFloat(row) * (dot + spacing),
                    width: dot,
                    height: dot
                )
                NSBezierPath(ovalIn: rect).fill()
            }
        }
    }
}

@MainActor
private final class DividerView: NSView {
    private let vertical: Bool

    init(vertical: Bool) {
        self.vertical = vertical
        super.init(frame: .zero)
        if vertical {
            widthAnchor.constraint(equalToConstant: 1).isActive = true
            heightAnchor.constraint(equalToConstant: 24).isActive = true
        } else {
            widthAnchor.constraint(equalToConstant: 24).isActive = true
            heightAnchor.constraint(equalToConstant: 1).isActive = true
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.separatorColor.withAlphaComponent(0.6).setFill()
        bounds.fill()
    }
}

/// A labelled slider hosted inside a menu item. Dragging updates live while the menu stays open.
@MainActor
private final class MenuSliderView: NSView {
    var onChange: ((Double) -> Void)?
    private let titleLabel: NSTextField
    private let valueLabel = NSTextField(labelWithString: "")
    private let slider: NSSlider
    private let format: (Double) -> String

    init(title: String, range: ClosedRange<Double>, value: Double, format: @escaping (Double) -> String) {
        self.format = format
        titleLabel = NSTextField(labelWithString: title)
        slider = NSSlider(value: value, minValue: range.lowerBound, maxValue: range.upperBound, target: nil, action: nil)
        super.init(frame: NSRect(x: 0, y: 0, width: 230, height: 54))

        titleLabel.font = .systemFont(ofSize: 12, weight: .medium)
        valueLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        valueLabel.textColor = .secondaryLabelColor
        valueLabel.alignment = .right
        valueLabel.stringValue = format(value)

        slider.isContinuous = true
        slider.target = self
        slider.action = #selector(sliderMoved)

        for view in [titleLabel, valueLabel, slider] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            titleLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            titleLabel.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            valueLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            valueLabel.firstBaselineAnchor.constraint(equalTo: titleLabel.firstBaselineAnchor),
            slider.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            slider.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            slider.topAnchor.constraint(equalTo: titleLabel.bottomAnchor, constant: 6),
            slider.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -8)
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    @objc private func sliderMoved() {
        valueLabel.stringValue = format(slider.doubleValue)
        onChange?(slider.doubleValue)
    }
}

/// Preset and saved color swatches for the color menu.
@MainActor
private final class SwatchGridView: NSView {
    var onSelect: ((String) -> Void)?

    private let presets: [String]
    private let custom: [String]
    private let selected: String
    private let columns = 7
    private let cell: CGFloat = 26
    private let margin: CGFloat = 12
    private let sectionGap: CGFloat = 18

    init(presets: [String], custom: [String], selected: String) {
        self.presets = presets
        self.custom = custom
        self.selected = ColorHex.normalize(selected) ?? selected
        super.init(frame: .zero)
        frame = NSRect(origin: .zero, size: contentSize)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override var isFlipped: Bool { true }

    private func rows(_ count: Int) -> Int { (count + columns - 1) / columns }

    private var contentSize: NSSize {
        var height = margin * 2 + CGFloat(rows(presets.count)) * cell
        if !custom.isEmpty {
            height += sectionGap + CGFloat(rows(custom.count)) * cell
        }
        return NSSize(width: margin * 2 + CGFloat(columns) * cell, height: height)
    }

    private var customOriginY: CGFloat {
        margin + CGFloat(rows(presets.count)) * cell + sectionGap
    }

    private func rect(index: Int, originY: CGFloat) -> NSRect {
        NSRect(
            x: margin + CGFloat(index % columns) * cell,
            y: originY + CGFloat(index / columns) * cell,
            width: cell,
            height: cell
        ).insetBy(dx: 3, dy: 3)
    }

    override func draw(_ dirtyRect: NSRect) {
        func drawSwatch(_ hex: String, in frame: NSRect) {
            let color = NSColor(hex: hex)
            color.setFill()
            let circle = NSBezierPath(ovalIn: frame)
            circle.fill()
            NSColor.separatorColor.setStroke()
            circle.lineWidth = 0.75
            circle.stroke()
            if ColorHex.normalize(hex) == selected {
                NSColor.controlAccentColor.setStroke()
                let ring = NSBezierPath(ovalIn: frame.insetBy(dx: -2.5, dy: -2.5))
                ring.lineWidth = 2
                ring.stroke()
            }
        }
        for (index, hex) in presets.enumerated() {
            drawSwatch(hex, in: rect(index: index, originY: margin))
        }
        if !custom.isEmpty {
            let label = NSAttributedString(
                string: "Saved",
                attributes: [
                    .font: NSFont.systemFont(ofSize: 10, weight: .medium),
                    .foregroundColor: NSColor.secondaryLabelColor
                ]
            )
            label.draw(at: NSPoint(x: margin + 3, y: customOriginY - sectionGap + 2))
            for (index, hex) in custom.enumerated() {
                drawSwatch(hex, in: rect(index: index, originY: customOriginY))
            }
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        for (index, hex) in presets.enumerated() where rect(index: index, originY: margin).contains(point) {
            onSelect?(hex)
            return
        }
        for (index, hex) in custom.enumerated()
            where rect(index: index, originY: customOriginY).contains(point) {
            onSelect?(hex)
            return
        }
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

    /// `#RRGGBB` in sRGB.
    var hexRGB: String {
        let converted = usingColorSpace(.sRGB) ?? self
        return String(
            format: "#%02X%02X%02X",
            Int((converted.redComponent * 255).rounded()),
            Int((converted.greenComponent * 255).rounded()),
            Int((converted.blueComponent * 255).rounded())
        )
    }
}
