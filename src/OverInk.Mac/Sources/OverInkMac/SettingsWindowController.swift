import AppKit
import OverInkCore

// MARK: - Shared window building blocks

@MainActor
enum WindowChrome {
    static func background() -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .underWindowBackground
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }

    static func window(title: String, width: CGFloat) -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: width, height: 400),
            styleMask: [.titled, .closable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = title
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        return window
    }

    static func label(_ text: String, size: CGFloat = 13, weight: NSFont.Weight = .regular, secondary: Bool = false) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: text)
        field.font = .systemFont(ofSize: size, weight: weight)
        field.textColor = secondary ? .secondaryLabelColor : .labelColor
        return field
    }

    /// Pins `stack` inside `root` with padding and sizes `window` to fit it.
    static func install(_ stack: NSStackView, in window: NSWindow, root: NSView, padding: CGFloat = 24, width: CGFloat) {
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: padding),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -padding),
            stack.topAnchor.constraint(equalTo: root.topAnchor, constant: padding + 18)
        ])
        window.contentView = root
        root.layoutSubtreeIfNeeded()
        window.setContentSize(NSSize(width: width, height: stack.fittingSize.height + padding * 2 + 18))
    }
}

/// A checkbox that runs a closure when toggled.
@MainActor
final class ClosureCheckbox: NSButton {
    var onToggle: ((Bool) -> Void)?

    init(title: String) {
        super.init(frame: .zero)
        self.title = title
        setButtonType(.switch)
        target = self
        action = #selector(toggled)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    @objc private func toggled() {
        onToggle?(state == .on)
    }
}

/// A button that runs a closure.
@MainActor
final class ClosureButton: NSButton {
    var onPress: (() -> Void)?

    init(title: String) {
        super.init(frame: .zero)
        self.title = title
        bezelStyle = .rounded
        target = self
        action = #selector(pressed)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    @objc private func pressed() {
        onPress?()
    }
}

// MARK: - Settings

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    private let settings: SettingsModel
    private let updates: UpdateController
    private var window: NSWindow?
    private var refreshers: [() -> Void] = []

    private let width: CGFloat = 560
    private let labelWidth: CGFloat = 150

    init(settings: SettingsModel, updates: UpdateController) {
        self.settings = settings
        self.updates = updates
        super.init()
        settings.observe { [weak self] in self?.refresh() }
        updates.observeChecks { [weak self] in self?.refresh() }
    }

    func show() {
        if window == nil { build() }
        refresh()
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    private func refresh() {
        refreshers.forEach { $0() }
    }

    /// Development aid (`--snapshot-settings <png>`): renders the window to a file without needing a screen.
    func writeSnapshot(to url: URL) {
        show()
        guard let view = window?.contentView else { return }
        view.layoutSubtreeIfNeeded()
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    func windowDidBecomeKey(_ notification: Notification) {
        refresh()
    }

    // MARK: Build

    private func build() {
        let window = WindowChrome.window(title: "OverInk Settings", width: width)
        window.delegate = self

        let stack = NSStackView()
        stack.spacing = 8

        addHeader("General", to: stack)
        addLaunchAtLogin(to: stack)
        addHotkey(
            "Draw hotkey",
            note: "Starts and stops drawing from anywhere. Delete resets it to ⌃⇧D.",
            to: stack,
            get: { $0.drawHotkey },
            set: { $0.drawHotkey = $1 ?? KeyChord.defaultDrawToggle }
        )

        addHeader("Capture", to: stack)
        addCaptureMode(to: stack)
        addHotkey(
            "Capture hotkey",
            note: "Starts a region or window capture. Not set by default; function keys work without a modifier.",
            to: stack,
            get: { $0.captureHotkey },
            set: { $0.captureHotkey = $1 }
        )
        addCheckbox("Copy to the clipboard after capture", to: stack, get: \.copyAfterCapture) { $0.copyAfterCapture = $1 }
        addCheckbox("Leave drawing mode after capture", to: stack, get: \.exitDrawModeAfterCapture) { $0.exitDrawModeAfterCapture = $1 }
        addCheckbox("Clear annotations after capture", to: stack, get: \.clearAfterCapture) { $0.clearAfterCapture = $1 }
        addCheckbox("Include the toolbar in captures", to: stack, get: \.includeToolbarInCaptures) { $0.includeToolbarInCaptures = $1 }
        addCheckbox("Open the editor after capture", to: stack, get: \.openEditorAfterCapture) { $0.openEditorAfterCapture = $1 }
        addLibraryFolder(to: stack)

        addHeader("Toolbar", to: stack)
        addCheckbox("Compact tool buttons", to: stack, get: \.compactTools) { $0.compactTools = $1 }
        addCheckbox("Vertical toolbar", to: stack, get: \.toolbarVertical) { $0.toolbarVertical = $1 }
        addCheckbox("Hide the toolbar when not drawing", to: stack, get: \.hideToolbarWhenNotDrawing) {
            $0.hideToolbarWhenNotDrawing = $1
        }

        addHeader("Permissions", to: stack)
        addScreenPermission(to: stack)

        addHeader("Updates", to: stack)
        addUpdates(to: stack)

        WindowChrome.install(stack, in: window, root: WindowChrome.background(), width: width)
        window.center()
        self.window = window
    }

    private func addHeader(_ title: String, to stack: NSStackView) {
        let header = WindowChrome.label(title, size: 15, weight: .semibold)
        if !stack.arrangedSubviews.isEmpty {
            stack.setCustomSpacing(18, after: stack.arrangedSubviews.last!)
        }
        stack.addArrangedSubview(header)
        stack.setCustomSpacing(10, after: header)
    }

    private func row(_ title: String, control: NSView, note: String? = nil, in stack: NSStackView) {
        let label = WindowChrome.label(title)
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: labelWidth).isActive = true
        let line = NSStackView(views: [label, control])
        line.orientation = .horizontal
        line.alignment = .firstBaseline
        line.spacing = 12
        stack.addArrangedSubview(line)
        if let note {
            let text = WindowChrome.label(note, size: 11, secondary: true)
            text.preferredMaxLayoutWidth = width - 48 - labelWidth - 12
            let indent = NSStackView(views: [NSView(), text])
            indent.orientation = .horizontal
            indent.spacing = 12
            indent.arrangedSubviews[0].widthAnchor.constraint(equalToConstant: labelWidth).isActive = true
            stack.addArrangedSubview(indent)
        }
    }

    private func addCheckbox(
        _ title: String,
        to stack: NSStackView,
        get: @escaping (MacAppSettings) -> Bool,
        set: @escaping (inout MacAppSettings, Bool) -> Void
    ) {
        let box = ClosureCheckbox(title: title)
        box.onToggle = { [weak self] on in self?.settings.update { set(&$0, on) } }
        refreshers.append { [weak self, weak box] in
            guard let self, let box else { return }
            box.state = get(self.settings.value) ? .on : .off
        }
        let spacer = NSView()
        spacer.widthAnchor.constraint(equalToConstant: labelWidth + 12).isActive = true
        let line = NSStackView(views: [spacer, box])
        line.orientation = .horizontal
        line.spacing = 0
        stack.addArrangedSubview(line)
    }

    private func addHotkey(
        _ title: String,
        note: String,
        to stack: NSStackView,
        get: @escaping (MacAppSettings) -> KeyChord?,
        set: @escaping (inout MacAppSettings, KeyChord?) -> Void
    ) {
        let recorder = HotKeyRecorderButton(chord: get(settings.value), emptyTitle: "Not set")
        recorder.onChange = { [weak self] chord in self?.settings.update { set(&$0, chord) } }
        refreshers.append { [weak self, weak recorder] in
            guard let self, let recorder else { return }
            recorder.setChord(get(self.settings.value))
        }
        row(title, control: recorder, note: note, in: stack)
    }

    private func addCaptureMode(to stack: NSStackView) {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        popup.addItems(withTitles: ["Auto: display under the pointer", "Full screen: all displays"])
        popup.target = self
        popup.action = #selector(captureModeChanged(_:))
        refreshers.append { [weak self, weak popup] in
            guard let self, let popup else { return }
            popup.selectItem(at: self.settings.value.captureMode == .fullScreen ? 1 : 0)
        }
        row("Capture mode", control: popup, in: stack)
    }

    @objc private func captureModeChanged(_ sender: NSPopUpButton) {
        let mode: CaptureMode = sender.indexOfSelectedItem == 1 ? .fullScreen : .auto
        settings.update { $0.captureMode = mode }
    }

    private func addLaunchAtLogin(to stack: NSStackView) {
        let box = ClosureCheckbox(title: "Open OverInk when you log in")
        box.onToggle = { [weak self, weak box] on in
            do {
                try LoginItem.setEnabled(on)
                if on, LoginItem.needsApproval { LoginItem.openSystemSettings() }
            } catch {
                box?.state = on ? .off : .on
                self?.showError(
                    "Couldn't change the login item",
                    "\(error.localizedDescription)\n\nLaunch at login works from the installed OverInk app."
                )
            }
        }
        refreshers.append { [weak box] in
            box?.state = LoginItem.isEnabled ? .on : .off
        }
        row("Startup", control: box, in: stack)
    }

    private func addLibraryFolder(to stack: NSStackView) {
        let path = WindowChrome.label("", size: 12, secondary: true)
        path.lineBreakMode = .byTruncatingMiddle
        path.maximumNumberOfLines = 1
        path.widthAnchor.constraint(equalToConstant: 250).isActive = true
        refreshers.append { [weak self, weak path] in
            guard let self, let path else { return }
            let folder = CaptureLibrary.folder(forSetting: self.settings.value.libraryFolder)
            path.stringValue = (folder.path as NSString).abbreviatingWithTildeInPath
            path.toolTip = folder.path
        }

        let choose = ClosureButton(title: "Choose…")
        choose.onPress = { [weak self] in self?.chooseLibraryFolder() }
        let reset = ClosureButton(title: "Default")
        reset.onPress = { [weak self] in self?.settings.update { $0.libraryFolder = nil } }
        let show = ClosureButton(title: "Show")
        show.onPress = { [weak self] in
            guard let self else { return }
            let folder = CaptureLibrary.folder(forSetting: self.settings.value.libraryFolder)
            try? CaptureLibrary(folder: folder).ensureFolder()
            NSWorkspace.shared.open(folder)
        }
        let buttons = NSStackView(views: [path, choose, reset, show])
        buttons.orientation = .horizontal
        buttons.spacing = 8
        row("Capture folder", control: buttons, in: stack)
    }

    private func chooseLibraryFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Use Folder"
        panel.message = "Choose where OverInk saves and lists captures."
        panel.directoryURL = CaptureLibrary.folder(forSetting: settings.value.libraryFolder)
        guard let window else { return }
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            MainActor.assumeIsolated {
                let isDefault = url.standardizedFileURL == CaptureLibrary.defaultFolder().standardizedFileURL
                self?.settings.update { $0.libraryFolder = isDefault ? nil : url.path }
            }
        }
    }

    private func addScreenPermission(to stack: NSStackView) {
        let status = WindowChrome.label("", secondary: false)
        let button = ClosureButton(title: "Allow…")
        button.onPress = {
            if !ScreenPermission.request() { ScreenPermission.openSystemSettings() }
        }
        let line = NSStackView(views: [status, button])
        line.orientation = .horizontal
        line.spacing = 12
        refreshers.append { [weak status, weak button] in
            let granted = ScreenPermission.isGranted
            status?.stringValue = granted ? "Screen Recording: allowed" : "Screen Recording: not allowed"
            status?.textColor = granted ? .systemGreen : .systemOrange
            button?.title = granted ? "Open System Settings" : "Allow…"
            button?.onPress = granted
                ? { ScreenPermission.openSystemSettings() }
                : { if !ScreenPermission.request() { ScreenPermission.openSystemSettings() } }
        }
        row("Screen capture", control: line, note: "Needed for captures. macOS may ask you to relaunch OverInk after you allow it.", in: stack)
    }

    private func addUpdates(to stack: NSStackView) {
        let version = WindowChrome.label(updates.currentVersion.map { "OverInk \($0)" } ?? "Development build")
        row("Version", control: version, in: stack)

        addCheckbox("Include beta updates", to: stack, get: { $0.updates.includeBeta }) { $0.updates.includeBeta = $1 }
        let betaNote = WindowChrome.label("Offers pre-release builds, which can be less stable.", size: 11, secondary: true)
        betaNote.preferredMaxLayoutWidth = width - 48 - labelWidth - 12
        let indent = NSStackView(views: [NSView(), betaNote])
        indent.orientation = .horizontal
        indent.spacing = 12
        indent.arrangedSubviews[0].widthAnchor.constraint(equalToConstant: labelWidth).isActive = true
        stack.addArrangedSubview(indent)

        let status = WindowChrome.label("", size: 12, secondary: true)
        status.preferredMaxLayoutWidth = 260
        let check = ClosureButton(title: "Check now")
        check.onPress = { [weak self] in self?.updates.checkNow() }
        let line = NSStackView(views: [check, status])
        line.orientation = .horizontal
        line.alignment = .firstBaseline
        line.spacing = 12
        refreshers.append { [weak self, weak status, weak check] in
            guard let self else { return }
            status?.stringValue = self.updates.statusText
            check?.isEnabled = self.updates.canCheckNow
        }
        row("Check for updates", control: line, in: stack)
    }

    private func showError(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.alertStyle = .warning
        alert.runModal()
    }
}
