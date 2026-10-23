import AppKit
import ImageIO
import OverInkCore
import UniformTypeIdentifiers

enum CaptureEditorError: LocalizedError {
    case imageUnavailable
    case noDocument

    var errorDescription: String? {
        switch self {
        case .imageUnavailable: "The capture image could not be opened."
        case .noDocument: "No capture is open."
        }
    }
}

/// Window that routes Command shortcuts to the editor even though the app has no main menu.
@MainActor
final class EditorWindow: NSWindow {
    var shortcutHandler: ((NSEvent) -> Bool)?
    var onCancel: (() -> Void)?

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        // Let text fields (the search box) keep their own Command-C/V/Z.
        if firstResponder is NSText { return super.performKeyEquivalent(with: event) }
        if shortcutHandler?(event) == true { return true }
        return super.performKeyEquivalent(with: event)
    }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}

/// The editor and the capture library in one window: a canvas with the recent-captures strip below
/// it, or the searchable grid of every capture.
@MainActor
final class EditorWindowController: NSObject, NSWindowDelegate, NSSearchFieldDelegate {
    private enum Mode {
        case editor
        case library
    }

    private let settings: SettingsModel
    private let model: LibraryModel
    private let actions: LibraryActions
    private let stripController: LibraryCollectionController
    private let gridController: LibraryCollectionController

    private var window: EditorWindow?
    private let canvas = EditorCanvasView()
    private let scrollView = NSScrollView()
    private var mode = Mode.editor

    private let editorBar = NSView()
    private let libraryBar = NSView()
    private let editorArea = NSView()
    private let libraryArea = NSView()
    private let emptyLabel = NSTextField(labelWithString: "")
    private let searchField = NSSearchField()
    private var backButton: NSButton?

    private var toolButtons: [EditorTool: NSButton] = [:]
    private let undoButton = NSButton()
    private let redoButton = NSButton()
    private let colorButton = ColorSwatchButton()
    private let widthSlider = NSSlider(value: 4, minValue: 1, maxValue: 24, target: nil, action: nil)
    private let veilSlider = NSSlider(value: 0, minValue: 0, maxValue: 0.6, target: nil, action: nil)
    private let zoomButton = NSButton()
    private lazy var colorMenuController = ColorMenuController(
        settings: settings,
        current: { [weak self] in self?.canvas.colorHex ?? "#E53935" },
        apply: { [weak self] in self?.setColor($0) }
    )

    private var currentURL: URL?
    private var originalImage: CGImage?
    private var promoteOriginal = false
    private var fitMode = true
    private var lastMagnification: CGFloat = 0
    private let saveQueue = DispatchQueue(label: "overink.editor.save")
    private var isDirty = false {
        didSet { window?.isDocumentEdited = isDirty }
    }

    init(settings: SettingsModel) {
        self.settings = settings
        model = LibraryModel(folder: CaptureLibrary.folder(forSetting: settings.value.libraryFolder))
        actions = LibraryActions(model: model)
        stripController = LibraryCollectionController(style: .strip, model: model, actions: actions)
        gridController = LibraryCollectionController(style: .grid, model: model, actions: actions)
        super.init()

        actions.beforeChange = { [weak self] url in
            if self?.currentURL == url { self?.saveIfNeeded(wait: true) }
        }
        actions.didRename = { [weak self] old, new in
            guard let self, self.currentURL == old else { return }
            self.currentURL = new
            self.stripController.currentURL = new
            self.window?.title = new.deletingPathExtension().lastPathComponent
        }
        actions.didDelete = { [weak self] url in
            guard let self, self.currentURL == url else { return }
            self.closeCurrentCapture()
        }
        stripController.onOpen = { [weak self] in self?.open($0) }
        gridController.onOpen = { [weak self] in self?.open($0) }
        model.observe { [weak self] in self?.libraryChanged() }
        settings.observe { [weak self] in
            guard let self else { return }
            self.model.retarget(folder: CaptureLibrary.folder(forSetting: self.settings.value.libraryFolder))
        }
    }

    // MARK: Public

    func showLibrary(focusSearch: Bool = false) {
        buildWindowIfNeeded()
        setMode(.library)
        present()
        if focusSearch { window?.makeFirstResponder(searchField) }
    }

    func open(_ url: URL) {
        buildWindowIfNeeded()
        if url != currentURL { saveIfNeeded(wait: true) }
        // Make sure a save still in flight has finished before reading the file back.
        saveQueue.sync {}
        do {
            let loaded = try CaptureStorage.load(url)
            currentURL = url
            originalImage = loaded.image
            promoteOriginal = loaded.promote
            canvas.pressureSensitive = settings.value.pressureSensitive
            canvas.load(image: loaded.image, document: loaded.document)
            canvas.colorHex = settings.value.penColor
            canvas.penWidth = settings.value.penWidth
            colorButton.color = NSColor(hex: canvas.colorHex)
            widthSlider.doubleValue = canvas.penWidth
            veilSlider.doubleValue = loaded.document.veilOpacity
            isDirty = false
            stripController.currentURL = url
            window?.title = url.deletingPathExtension().lastPathComponent
            setMode(.editor)
            // The viewport has no size until the window has been laid out.
            window?.contentView?.layoutSubtreeIfNeeded()
            fit()
            present()
            window?.makeFirstResponder(canvas)
            syncToolbar()
        } catch {
            showError(error)
        }
    }

    func reload() {
        model.reload()
    }

    /// Saves pending edits synchronously (used when the app quits).
    func flush() {
        saveIfNeeded(wait: true)
    }

    // MARK: Window

    private func present() {
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    private func buildWindowIfNeeded() {
        guard window == nil else { return }

        let window = EditorWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1040, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 920, height: 520)
        window.delegate = self
        window.shortcutHandler = { [weak self] in self?.handleShortcut($0) ?? false }
        window.onCancel = { [weak self] in self?.cancel() }
        window.setFrameAutosaveName("OverInkEditor")
        if !window.setFrameUsingName("OverInkEditor") { window.center() }

        let root = NSVisualEffectView()
        root.material = .underWindowBackground
        root.blendingMode = .behindWindow
        root.state = .active

        configureEditorBar()
        configureLibraryBar()
        configureEditorArea()
        configureLibraryArea()

        for view in [editorBar, libraryBar, editorArea, libraryArea] {
            view.translatesAutoresizingMaskIntoConstraints = false
            root.addSubview(view)
        }
        for bar in [editorBar, libraryBar] {
            NSLayoutConstraint.activate([
                bar.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16),
                bar.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16),
                bar.topAnchor.constraint(equalTo: root.safeAreaLayoutGuide.topAnchor, constant: 10),
                bar.heightAnchor.constraint(equalToConstant: 44)
            ])
        }
        for area in [editorArea, libraryArea] {
            NSLayoutConstraint.activate([
                area.leadingAnchor.constraint(equalTo: root.leadingAnchor),
                area.trailingAnchor.constraint(equalTo: root.trailingAnchor),
                area.topAnchor.constraint(equalTo: editorBar.bottomAnchor, constant: 8),
                area.bottomAnchor.constraint(equalTo: root.bottomAnchor)
            ])
        }

        window.contentView = root
        self.window = window
        setMode(mode)
        updateZoomLabel()
    }

    private func setMode(_ newMode: Mode) {
        mode = newMode
        let editing = newMode == .editor
        editorBar.isHidden = !editing
        editorArea.isHidden = !editing
        libraryBar.isHidden = editing
        libraryArea.isHidden = editing
        backButton?.isEnabled = currentURL != nil
        if editing {
            window?.title = currentURL?.deletingPathExtension().lastPathComponent ?? "Editor"
            window?.isDocumentEdited = isDirty
        } else {
            window?.title = "Capture Library"
            window?.isDocumentEdited = false
            updateEmptyLabel()
        }
    }

    private func cancel() {
        if mode == .library, currentURL != nil {
            setMode(.editor)
            window?.makeFirstResponder(canvas)
        }
    }

    // MARK: Toolbars

    private func configureEditorBar() {
        var views: [NSView] = []

        let library = iconButton(.images, "Library (⌘F to search)", #selector(openLibrary))
        views.append(library)
        views.append(divider())

        for tool in EditorTool.allCases {
            let button = iconButton(tool.icon, tool.title, #selector(toolButtonPressed(_:)))
            button.setButtonType(.pushOnPushOff)
            button.identifier = NSUserInterfaceItemIdentifier(tool.rawValue)
            toolButtons[tool] = button
            views.append(button)
        }
        views.append(divider())

        configure(undoButton, .undo, "Undo (⌘Z)", #selector(undoPressed))
        configure(redoButton, .redo, "Redo (⇧⌘Z)", #selector(redoPressed))
        views.append(contentsOf: [undoButton, redoButton, divider()])

        colorButton.target = self
        colorButton.action = #selector(showColorMenu)
        widthSlider.controlSize = .small
        widthSlider.target = self
        widthSlider.action = #selector(widthChanged)
        widthSlider.toolTip = "Pen width"
        widthSlider.widthAnchor.constraint(equalToConstant: 80).isActive = true
        veilSlider.controlSize = .small
        veilSlider.target = self
        veilSlider.action = #selector(veilChanged)
        veilSlider.toolTip = "Veil"
        veilSlider.widthAnchor.constraint(equalToConstant: 80).isActive = true
        let veilLabel = NSTextField(labelWithString: "Veil")
        veilLabel.font = .systemFont(ofSize: 11, weight: .medium)
        veilLabel.textColor = .secondaryLabelColor
        views.append(contentsOf: [colorButton, widthSlider, veilLabel, veilSlider])

        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        views.append(spacer)

        zoomButton.bezelStyle = .texturedRounded
        zoomButton.controlSize = .small
        zoomButton.target = self
        zoomButton.action = #selector(showZoomMenu)
        zoomButton.toolTip = "Zoom (⌘0 fit, ⌘1 actual size)"
        views.append(zoomButton)

        let more = NSButton(title: "More", target: self, action: #selector(showMoreMenu))
        more.bezelStyle = .rounded
        more.image = LucideIcon.chevronDown.image(size: 12, label: "More")
        more.imagePosition = .imageTrailing
        more.controlSize = .small
        views.append(more)

        let copy = NSButton(title: "Copy", target: self, action: #selector(copyFlattened))
        copy.bezelStyle = .rounded
        copy.toolTip = "Copy image (⌘C)"
        views.append(copy)

        let save = NSButton(title: "Save", target: self, action: #selector(savePressed))
        save.bezelStyle = .rounded
        save.toolTip = "Save (⌘S). Edits also save automatically."
        views.append(save)

        install(NSStackView(views: views), in: editorBar)

        canvas.onChange = { [weak self] in self?.isDirty = true }
        canvas.onStateChange = { [weak self] in self?.syncToolbar() }
        canvas.onToolShortcut = { [weak self] in self?.canvas.tool = $0 }
    }

    private func configureLibraryBar() {
        let title = NSTextField(labelWithString: "Library")
        title.font = .systemFont(ofSize: 18, weight: .bold)

        searchField.placeholderString = "Search captures"
        searchField.sendsSearchStringImmediately = true
        searchField.delegate = self
        searchField.target = self
        searchField.action = #selector(searchChanged)
        searchField.widthAnchor.constraint(equalToConstant: 220).isActive = true

        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)

        let back = NSButton(title: "Back to editor", target: self, action: #selector(backToEditor))
        back.bezelStyle = .rounded
        backButton = back

        var views: [NSView] = [title, searchField, spacer, back]
        let buttons: [(String, LucideIcon, Selector)] = [
            ("Open", .images, #selector(libraryOpen)),
            ("Copy", .camera, #selector(libraryCopy)),
            ("Rename", .pencil, #selector(libraryRename)),
            ("Duplicate", .rectangle, #selector(libraryDuplicate)),
            ("Reveal", .pointer, #selector(libraryReveal)),
            ("Trash", .trash, #selector(libraryDelete))
        ]
        for (label, icon, action) in buttons {
            let button = NSButton(title: label, target: self, action: action)
            button.image = icon.image(size: 14, label: label)
            button.imagePosition = .imageLeading
            button.bezelStyle = .rounded
            button.controlSize = .small
            views.append(button)
        }
        install(NSStackView(views: views), in: libraryBar)
    }

    private func configureEditorArea() {
        let clip = CenteringClipView()
        clip.drawsBackground = false
        scrollView.contentView = clip
        scrollView.documentView = canvas
        scrollView.allowsMagnification = true
        scrollView.minMagnification = 0.1
        scrollView.maxMagnification = 8
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.drawsBackground = false
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        clip.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(
            self, selector: #selector(viewportChanged), name: NSView.boundsDidChangeNotification, object: clip
        )
        NotificationCenter.default.addObserver(
            self, selector: #selector(userMagnified), name: NSScrollView.didEndLiveMagnifyNotification,
            object: scrollView
        )

        let strip = NSVisualEffectView()
        strip.material = .sidebar
        strip.blendingMode = .withinWindow
        strip.state = .active
        strip.translatesAutoresizingMaskIntoConstraints = false
        let stripScroll = stripController.scrollView
        stripScroll.translatesAutoresizingMaskIntoConstraints = false
        strip.addSubview(stripScroll)

        editorArea.addSubview(scrollView)
        editorArea.addSubview(strip)
        NSLayoutConstraint.activate([
            strip.leadingAnchor.constraint(equalTo: editorArea.leadingAnchor),
            strip.trailingAnchor.constraint(equalTo: editorArea.trailingAnchor),
            strip.bottomAnchor.constraint(equalTo: editorArea.bottomAnchor),
            strip.heightAnchor.constraint(equalToConstant: 104),

            stripScroll.leadingAnchor.constraint(equalTo: strip.leadingAnchor),
            stripScroll.trailingAnchor.constraint(equalTo: strip.trailingAnchor),
            stripScroll.topAnchor.constraint(equalTo: strip.topAnchor),
            stripScroll.bottomAnchor.constraint(equalTo: strip.bottomAnchor),

            scrollView.leadingAnchor.constraint(equalTo: editorArea.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: editorArea.trailingAnchor),
            scrollView.topAnchor.constraint(equalTo: editorArea.topAnchor),
            scrollView.bottomAnchor.constraint(equalTo: strip.topAnchor)
        ])
    }

    private func configureLibraryArea() {
        let scroll = gridController.scrollView
        scroll.translatesAutoresizingMaskIntoConstraints = false
        emptyLabel.font = .systemFont(ofSize: 15, weight: .medium)
        emptyLabel.textColor = .secondaryLabelColor
        emptyLabel.alignment = .center
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        libraryArea.addSubview(scroll)
        libraryArea.addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: libraryArea.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: libraryArea.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: libraryArea.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: libraryArea.bottomAnchor),
            emptyLabel.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: scroll.centerYAnchor)
        ])
    }

    private func install(_ stack: NSStackView, in host: NSView) {
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8

        let container: NSView
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.contentView = stack
            glass.cornerRadius = 15
            glass.style = .regular
            if #available(macOS 27.0, *) {
                glass.effectIsInteractive = true
            }
            container = glass
        } else {
            let material = NSVisualEffectView()
            material.material = .popover
            material.blendingMode = .withinWindow
            material.state = .active
            stack.translatesAutoresizingMaskIntoConstraints = false
            material.addSubview(stack)
            NSLayoutConstraint.activate([
                stack.leadingAnchor.constraint(equalTo: material.leadingAnchor, constant: 10),
                stack.trailingAnchor.constraint(equalTo: material.trailingAnchor, constant: -10),
                stack.topAnchor.constraint(equalTo: material.topAnchor, constant: 4),
                stack.bottomAnchor.constraint(equalTo: material.bottomAnchor, constant: -4)
            ])
            container = material
        }
        container.translatesAutoresizingMaskIntoConstraints = false
        host.addSubview(container)
        NSLayoutConstraint.activate([
            container.leadingAnchor.constraint(equalTo: host.leadingAnchor),
            container.trailingAnchor.constraint(equalTo: host.trailingAnchor),
            container.topAnchor.constraint(equalTo: host.topAnchor),
            container.bottomAnchor.constraint(equalTo: host.bottomAnchor)
        ])
    }

    private func iconButton(_ icon: LucideIcon, _ tip: String, _ action: Selector) -> NSButton {
        let button = NSButton()
        configure(button, icon, tip, action)
        return button
    }

    private func configure(_ button: NSButton, _ icon: LucideIcon, _ tip: String, _ action: Selector) {
        button.image = icon.image(label: tip)
        button.toolTip = tip
        button.bezelStyle = .texturedRounded
        button.target = self
        button.action = action
    }

    private func divider() -> NSView {
        let box = NSBox()
        box.boxType = .separator
        box.widthAnchor.constraint(equalToConstant: 1).isActive = true
        box.heightAnchor.constraint(equalToConstant: 22).isActive = true
        return box
    }

    private func syncToolbar() {
        for (tool, button) in toolButtons {
            button.state = tool == canvas.tool ? .on : .off
        }
        undoButton.isEnabled = canvas.canUndo
        redoButton.isEnabled = canvas.canRedo
    }

    // MARK: Toolbar actions

    @objc private func openLibrary() {
        showLibrary()
    }

    @objc private func backToEditor() {
        cancel()
    }

    @objc private func toolButtonPressed(_ sender: NSButton) {
        guard let raw = sender.identifier?.rawValue, let tool = EditorTool(rawValue: raw) else { return }
        canvas.tool = tool
        syncToolbar()
        window?.makeFirstResponder(canvas)
    }

    @objc private func undoPressed() { canvas.undo() }
    @objc private func redoPressed() { canvas.redo() }

    @objc private func showColorMenu() {
        colorMenuController.makeMenu().popUp(
            positioning: nil,
            at: NSPoint(x: 0, y: -4),
            in: colorButton
        )
    }

    private func setColor(_ hex: String) {
        canvas.colorHex = hex
        colorButton.color = NSColor(hex: hex)
        if canvas.tool == .select {
            canvas.recolorSelection(hex)
        }
    }

    @objc private func widthChanged() {
        canvas.penWidth = widthSlider.doubleValue.rounded()
    }

    @objc private func veilChanged() {
        canvas.setVeil(veilSlider.doubleValue)
    }

    @objc private func savePressed() {
        saveIfNeeded()
    }

    @objc private func copyFlattened() {
        do {
            let image = try renderCurrent()
            let nsImage = NSImage(cgImage: image, size: canvas.canvasSize)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([nsImage])
        } catch {
            showError(error)
        }
    }

    @objc private func showMoreMenu(_ sender: NSButton) {
        let menu = NSMenu()
        menu.addItem(ClosureMenuItem(title: "Save Copy as PNG…") { [weak self] in self?.saveCopy() })
        menu.addItem(ClosureMenuItem(title: "Rename…") { [weak self] in
            if let entry = self?.currentEntry { self?.actions.rename(entry) }
        })
        menu.addItem(ClosureMenuItem(title: "Duplicate") { [weak self] in
            if let entry = self?.currentEntry { self?.actions.duplicate(entry) }
        })
        menu.addItem(ClosureMenuItem(title: "Reveal in Finder") { [weak self] in
            if let entry = self?.currentEntry { self?.actions.reveal(entry) }
        })
        menu.addItem(.separator())
        menu.addItem(ClosureMenuItem(title: "Move to Trash") { [weak self] in
            if let entry = self?.currentEntry { self?.actions.delete(entry) }
        })
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: -4), in: sender)
    }

    private var currentEntry: CaptureEntry? {
        guard let currentURL else { return nil }
        return CaptureEntry(url: currentURL, modified: .now)
    }

    // MARK: Library actions

    @objc private func searchChanged() {
        model.query = searchField.stringValue
    }

    func controlTextDidChange(_ obj: Notification) {
        model.query = searchField.stringValue
    }

    @objc private func libraryOpen() { gridController.openSelected() }

    @objc private func libraryCopy() {
        if let entry = gridController.selectedEntry { actions.copy(entry) }
    }

    @objc private func libraryRename() {
        if let entry = gridController.selectedEntry { actions.rename(entry) }
    }

    @objc private func libraryDuplicate() {
        if let entry = gridController.selectedEntry { actions.duplicate(entry) }
    }

    @objc private func libraryReveal() {
        if let entry = gridController.selectedEntry { actions.reveal(entry) }
    }

    @objc private func libraryDelete() {
        if let entry = gridController.selectedEntry { actions.delete(entry) }
    }

    private func libraryChanged() {
        updateEmptyLabel()
    }

    private func updateEmptyLabel() {
        let empty = model.entries.isEmpty
        emptyLabel.isHidden = !empty
        emptyLabel.stringValue = model.allEntries.isEmpty ? "No captures yet" : "No matching captures"
    }

    private func closeCurrentCapture() {
        currentURL = nil
        originalImage = nil
        isDirty = false
        stripController.currentURL = nil
        window?.title = "Capture Library"
        setMode(.library)
    }

    // MARK: Shortcuts

    private func handleShortcut(_ event: NSEvent) -> Bool {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        guard flags.contains(.command), flags.isDisjoint(with: [.control, .option]) else { return false }
        let shift = flags.contains(.shift)
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""

        if key == "w" {
            window?.performClose(nil)
            return true
        }
        if key == "f" {
            showLibrary(focusSearch: true)
            return true
        }
        if mode == .library {
            if key == "c", let entry = gridController.selectedEntry {
                actions.copy(entry)
                return true
            }
            return false
        }
        guard currentURL != nil else { return false }

        switch (key, shift) {
        case ("z", false): canvas.undo()
        case ("z", true): canvas.redo()
        case ("s", false): saveIfNeeded()
        case ("s", true): saveCopy()
        case ("c", _): copyFlattened()
        case ("a", _): canvas.selectAll()
        case ("0", _): fit()
        case ("1", _): setZoom(1)
        case ("=", _), ("+", _): zoomBy(1.25)
        case ("-", _): zoomBy(0.8)
        default: return false
        }
        return true
    }

    // MARK: Zoom

    private func fit() {
        fitMode = true
        guard canvas.canvasSize.width > 0 else { return }
        let available = scrollView.contentSize
        let margin: CGFloat = 40
        let scale = min(
            (available.width - margin) / canvas.canvasSize.width,
            (available.height - margin) / canvas.canvasSize.height,
            1
        )
        applyMagnification(max(scale, scrollView.minMagnification), center: canvasCenter)
    }

    private var canvasCenter: NSPoint {
        NSPoint(x: canvas.canvasSize.width / 2, y: canvas.canvasSize.height / 2)
    }

    private var visibleCenter: NSPoint {
        let rect = scrollView.documentVisibleRect
        return NSPoint(x: rect.midX, y: rect.midY)
    }

    private func setZoom(_ value: CGFloat) {
        fitMode = false
        applyMagnification(value, center: visibleCenter)
    }

    private func zoomBy(_ factor: CGFloat) {
        setZoom(scrollView.magnification * factor)
    }

    private func applyMagnification(_ value: CGFloat, center: NSPoint) {
        let clamped = min(max(value, scrollView.minMagnification), scrollView.maxMagnification)
        scrollView.setMagnification(clamped, centeredAt: center)
        zoomChanged()
    }

    private func zoomChanged() {
        updateZoomLabel()
        canvas.refreshCursor()
        canvas.needsDisplay = true
    }

    private func updateZoomLabel() {
        let percent = Int((scrollView.magnification * 100).rounded())
        zoomButton.title = "\(percent)%"
    }

    @objc private func viewportChanged() {
        guard scrollView.magnification != lastMagnification else { return }
        lastMagnification = scrollView.magnification
        updateZoomLabel()
        canvas.needsDisplay = true
    }

    @objc private func userMagnified() {
        fitMode = false
        zoomChanged()
    }

    @objc private func showZoomMenu(_ sender: NSButton) {
        let menu = NSMenu()
        menu.addItem(ClosureMenuItem(title: "Fit to Window", keyEquivalent: "0") { [weak self] in self?.fit() })
        menu.addItem(ClosureMenuItem(title: "Actual Size", keyEquivalent: "1") { [weak self] in self?.setZoom(1) })
        menu.addItem(ClosureMenuItem(title: "Zoom In", keyEquivalent: "+") { [weak self] in self?.zoomBy(1.25) })
        menu.addItem(ClosureMenuItem(title: "Zoom Out", keyEquivalent: "-") { [weak self] in self?.zoomBy(0.8) })
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: -4), in: sender)
    }

    // MARK: Window delegate

    func windowDidResize(_ notification: Notification) {
        if fitMode, mode == .editor, currentURL != nil { fit() }
    }

    func windowDidResignKey(_ notification: Notification) {
        saveIfNeeded()
    }

    func windowWillClose(_ notification: Notification) {
        saveIfNeeded(wait: true)
    }

    // MARK: Loading and saving

    private func renderCurrent() throws -> CGImage {
        guard let originalImage, currentURL != nil else { throw CaptureEditorError.noDocument }
        return try CaptureRenderer.flattened(background: originalImage, document: canvas.document)
    }

    /// Writes pending edits. Nothing is written for an unchanged capture, and the original image is
    /// never re-encoded.
    private func saveIfNeeded(wait: Bool = false) {
        guard isDirty, let url = currentURL, originalImage != nil else { return }
        do {
            let job = CaptureSaveJob(
                url: url,
                document: canvas.document,
                flattened: try renderCurrent(),
                promoteOriginal: promoteOriginal
            )
            isDirty = false
            promoteOriginal = false
            if wait {
                saveQueue.sync { Self.run(job) }
            } else {
                saveQueue.async { Self.run(job) }
            }
        } catch {
            showError(error)
        }
    }

    nonisolated private static func run(_ job: CaptureSaveJob) {
        do {
            try job.run()
        } catch {
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    let alert = NSAlert()
                    alert.messageText = "Could not save the capture"
                    alert.informativeText = error.localizedDescription
                    alert.alertStyle = .warning
                    alert.runModal()
                }
            }
        }
    }

    private func saveCopy() {
        guard let window, currentURL != nil else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = (currentURL?.deletingPathExtension().lastPathComponent ?? "capture") + " copy.png"
        panel.directoryURL = CaptureLibrary.folder(forSetting: settings.value.libraryFolder)
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let destination = panel.url else { return }
            MainActor.assumeIsolated {
                do {
                    let data = try CaptureRenderer.pngData(for: self?.renderCurrent() ?? { throw CaptureEditorError.noDocument }())
                    try data.write(to: destination, options: .atomic)
                } catch {
                    self?.showError(error)
                }
            }
        }
    }

    private func showError(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = "Editor error"
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.runModal()
    }
}
