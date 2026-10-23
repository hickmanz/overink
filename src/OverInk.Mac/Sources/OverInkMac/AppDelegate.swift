import AppKit
import OverInkCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let overlayController = OverlayController()
    private let captureService = ScreenCaptureService()
    private lazy var editorController = EditorWindowController(settings: settings)
    private let regionPicker = RegionPickerController()
    private let settings = SettingsModel()
    private lazy var captureCoordinator = CaptureCoordinator(
        screenCapture: captureService,
        annotationSession: overlayController.session,
        libraryFolder: { [weak self] in
            CaptureLibrary.folder(forSetting: self?.settings.value.libraryFolder)
        },
        excludedWindowIDs: { [weak self] in
            guard let self else { return [] }
            var ids = self.overlayController.windowIDs
            if !self.settings.value.includeToolbarInCaptures,
               let toolbarID = self.toolbarController?.windowID {
                ids.insert(toolbarID)
            }
            return ids
        }
    )
    private var drawToggleHotKey: HotKeyBinding?
    private var captureHotKey: HotKeyBinding?
    private var regionMenuItem: NSMenuItem?
    private var toolbarController: ToolbarController?
    private var statusItem: NSStatusItem?
    private var modeMenuItem: NSMenuItem?
    private var showedScreenPermissionHelp = false

    /// Launch arguments for development: `--library` or `--open-editor <path to a capture PNG>`.
    private func handleLaunchArguments() {
        let arguments = CommandLine.arguments
        if arguments.contains("--library") {
            editorController.showLibrary()
        }
        if let index = arguments.firstIndex(of: "--open-editor"), arguments.indices.contains(index + 1) {
            editorController.open(URL(fileURLWithPath: arguments[index + 1]))
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        applySettings()
        overlayController.session.observeSettings { [weak self] in
            self?.persistDrawingSettings()
        }
        configureStatusItem()
        drawToggleHotKey = HotKeyBinding(chord: settings.value.drawHotkey) { [weak self] in
            self?.handleDrawHotKey()
        }
        toolbarController = ToolbarController(
            overlayController: overlayController,
            settings: settings,
            onCapture: { [weak self] in self?.capture() },
            onCaptureRegion: { [weak self] in self?.beginRegionCapture() },
            onOpenLibrary: { [weak self] in self?.openLibrary() },
            onConfigureCaptureHotkey: { [weak self] in self?.configureCaptureHotkey() }
        )
        captureHotKey = HotKeyBinding(chord: settings.value.captureHotkey) { [weak self] in
            self?.beginRegionCapture()
        }
        overlayController.modeDidChange = { [weak self] mode in
            guard let self else { return }
            self.refreshModeMenuItem(mode)
            self.toolbarController?.updateMode(mode)
            self.syncToolbarVisibility(for: mode)
        }
        overlayController.show()
        if !(settings.value.hideToolbarWhenNotDrawing) {
            toolbarController?.show()
        }
        var lastCaptureHotkey = settings.value.captureHotkey
        settings.observe { [weak self] in
            guard let self, self.settings.value.captureHotkey != lastCaptureHotkey else { return }
            lastCaptureHotkey = self.settings.value.captureHotkey
            self.captureHotKey?.update(chord: lastCaptureHotkey)
            self.refreshRegionMenuItemTitle()
            if let chord = lastCaptureHotkey, self.captureHotKey?.isRegistered == false {
                self.showAlert(
                    message: "That shortcut is unavailable.",
                    detail: "\(chord.displayString) is reserved by macOS or another app. Choose a different shortcut."
                )
            }
        }
        var lastAutoHide = settings.value.hideToolbarWhenNotDrawing
        settings.observe { [weak self] in
            // Only react to the auto-hide option itself, not to every toolbar drag.
            guard let self, self.settings.value.hideToolbarWhenNotDrawing != lastAutoHide else { return }
            lastAutoHide = self.settings.value.hideToolbarWhenNotDrawing
            if lastAutoHide {
                self.syncToolbarVisibility(for: self.overlayController.mode)
            } else {
                self.toolbarController?.show()
            }
        }
        refreshModeMenuItem(overlayController.mode)
        handleLaunchArguments()
    }

    /// The draw hotkey brings back a hidden toolbar and starts drawing; otherwise it toggles drawing.
    private func handleDrawHotKey() {
        if toolbarController?.isVisible == false, overlayController.mode == .passThrough {
            toolbarController?.show()
            overlayController.setMode(.draw)
        } else {
            overlayController.toggleMode()
        }
    }

    /// With "hide toolbar when not drawing" the toolbar follows the draw/pass-through mode.
    private func syncToolbarVisibility(for mode: OverlayController.InteractionMode) {
        guard settings.value.hideToolbarWhenNotDrawing else { return }
        switch mode {
        case .draw: toolbarController?.show()
        case .passThrough: toolbarController?.hide()
        }
    }

    private func applySettings() {
        let session = overlayController.session
        let value = settings.value
        session.colorHex = value.penColor
        session.penWidth = value.penWidth
        session.pressureSensitive = value.pressureSensitive
        session.veilOpacity = value.veilOpacity
        session.tool = value.tool
    }

    private func persistDrawingSettings() {
        let session = overlayController.session
        settings.update {
            $0.penColor = session.colorHex
            $0.penWidth = session.penWidth
            $0.pressureSensitive = session.pressureSensitive
            $0.veilOpacity = session.veilOpacity
            $0.tool = session.tool
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        editorController.flush()
        overlayController.shutdown()
        drawToggleHotKey?.shutdown()
        captureHotKey?.shutdown()
    }

    @objc private func chooseLibraryFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Use Folder"
        panel.message = "Choose where OverInk saves and lists captures."
        panel.directoryURL = CaptureLibrary.folder(forSetting: settings.value.libraryFolder)
        NSApplication.shared.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let isDefault = url.standardizedFileURL == CaptureLibrary.defaultFolder().standardizedFileURL
        settings.update { $0.libraryFolder = isDefault ? nil : url.path }
    }

    private func configureStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = NSImage(
            systemSymbolName: "pencil.and.outline",
            accessibilityDescription: "OverInk"
        )
        item.button?.toolTip = "OverInk"

        let menu = NSMenu()
        let showToolbarItem = NSMenuItem(
            title: "Show toolbar",
            action: #selector(showToolbar),
            keyEquivalent: ""
        )
        showToolbarItem.target = self
        menu.addItem(showToolbarItem)

        let libraryItem = NSMenuItem(
            title: "Open capture library…",
            action: #selector(openLibraryFromMenu),
            keyEquivalent: ""
        )
        libraryItem.target = self
        menu.addItem(libraryItem)

        let folderItem = NSMenuItem(
            title: "Library folder…",
            action: #selector(chooseLibraryFolder),
            keyEquivalent: ""
        )
        folderItem.target = self
        menu.addItem(folderItem)

        let modeItem = NSMenuItem(
            title: "",
            action: #selector(toggleDrawing),
            keyEquivalent: ""
        )
        modeItem.target = self
        menu.addItem(modeItem)
        modeMenuItem = modeItem

        let clearItem = NSMenuItem(
            title: "Clear annotations",
            action: #selector(clearAnnotations),
            keyEquivalent: ""
        )
        clearItem.target = self
        menu.addItem(clearItem)

        let captureItem = NSMenuItem(
            title: "Capture to Pictures and Clipboard",
            action: #selector(captureFromMenu),
            keyEquivalent: ""
        )
        captureItem.target = self
        menu.addItem(captureItem)

        let regionCaptureItem = NSMenuItem(
            title: "Capture region or window…",
            action: #selector(captureRegionFromMenu),
            keyEquivalent: ""
        )
        regionCaptureItem.target = self
        menu.addItem(regionCaptureItem)
        regionMenuItem = regionCaptureItem
        refreshRegionMenuItemTitle()

        let hotkeyItem = NSMenuItem(
            title: "Capture hotkey…",
            action: #selector(configureCaptureHotkeyFromMenu),
            keyEquivalent: ""
        )
        hotkeyItem.target = self
        menu.addItem(hotkeyItem)

        let undoItem = NSMenuItem(title: "Undo", action: #selector(undo), keyEquivalent: "z")
        undoItem.keyEquivalentModifierMask = [.command]
        undoItem.target = self
        menu.addItem(undoItem)

        let redoItem = NSMenuItem(title: "Redo", action: #selector(redo), keyEquivalent: "Z")
        redoItem.keyEquivalentModifierMask = [.command, .shift]
        redoItem.target = self
        menu.addItem(redoItem)

        menu.addItem(.separator())

        let screenPermissionItem = NSMenuItem(
            title: "Request Screen Recording Permission…",
            action: #selector(requestScreenCapturePermission),
            keyEquivalent: ""
        )
        screenPermissionItem.target = self
        menu.addItem(screenPermissionItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(
            title: "Quit OverInk",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        menu.addItem(quitItem)

        item.menu = menu
        statusItem = item
    }

    private func refreshRegionMenuItemTitle() {
        let suffix = settings.value.captureHotkey.map { " (\($0.displayString))" } ?? ""
        regionMenuItem?.title = "Capture region or window…" + suffix
    }

    @objc private func configureCaptureHotkeyFromMenu() {
        configureCaptureHotkey()
    }

    private func configureCaptureHotkey() {
        overlayController.setMode(.passThrough)
        HotKeyPrompt.present(
            title: "Capture hotkey",
            message: "Press the shortcut that starts a region or window capture. Press Delete to remove it.",
            current: settings.value.captureHotkey
        ) { [weak self] chord in
            self?.settings.update { $0.captureHotkey = chord }
        }
    }

    private var drawShortcutLabel: String {
        settings.value.drawHotkey.displayString
    }

    private func refreshModeMenuItem(_ mode: OverlayController.InteractionMode) {
        switch mode {
        case .draw:
            modeMenuItem?.title = "Use Pass-Through Mode (\(drawShortcutLabel))"
            statusItem?.button?.contentTintColor = .systemRed
        case .passThrough:
            modeMenuItem?.title = "Start Drawing (\(drawShortcutLabel))"
            statusItem?.button?.contentTintColor = nil
        }
    }

    @objc private func toggleDrawing() {
        overlayController.toggleMode()
    }

    @objc private func showToolbar() {
        toolbarController?.show()
    }

    @objc private func openLibraryFromMenu() {
        openLibrary()
    }

    @objc private func clearAnnotations() {
        overlayController.clear()
    }

    @objc private func captureFromMenu() {
        capture()
    }

    @objc private func captureRegionFromMenu() {
        beginRegionCapture()
    }

    @objc private func undo() {
        overlayController.undo()
    }

    @objc private func redo() {
        overlayController.redo()
    }

    @objc private func requestScreenCapturePermission() {
        if captureService.requestAuthorization() {
            showAlert(
                message: "Screen Recording is enabled.",
                detail: "OverInk can capture and stream the desktop."
            )
        } else {
            showAlert(
                message: "Screen Recording permission is not enabled.",
                detail: "Enable OverInk in System Settings → Privacy & Security → Screen Recording, then relaunch it."
            )
        }
    }

    private func capture() {
        let target: CaptureTarget = settings.value.captureMode == .fullScreen
            ? .allDisplays
            : .displayUnderCursor
        runCapture(target, wasDrawing: overlayController.mode == .draw)
    }

    private func beginRegionCapture() {
        let wasDrawing = overlayController.mode == .draw
        overlayController.setMode(.passThrough)
        toolbarController?.show()
        regionPicker.begin { [weak self] selection in
            guard let self else { return }
            guard let selection else {
                if wasDrawing {
                    self.overlayController.setMode(.draw)
                }
                return
            }
            self.runCapture(.region(selection.rect), wasDrawing: wasDrawing, alreadyPassThrough: true)
        }
    }

    private func runCapture(_ target: CaptureTarget, wasDrawing: Bool, alreadyPassThrough: Bool = false) {
        // Permission prompts and error dialogs must never appear behind a mouse-capturing overlay.
        if !alreadyPassThrough {
            overlayController.setMode(.passThrough)
        }
        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                let destination = try await captureCoordinator.capture(
                    target,
                    copyToClipboard: settings.value.copyAfterCapture
                )
                finishCapture(destination, wasDrawing: wasDrawing)
            } catch {
                // Show the error before re-entering draw mode so it is never under the overlay.
                handleCaptureError(error)
                if wasDrawing { overlayController.setMode(.draw) }
            }
        }
    }

    private func finishCapture(_ destination: URL, wasDrawing: Bool) {
        editorController.reload()
        if settings.value.openEditorAfterCapture {
            editorController.open(destination)
        }
        if settings.value.clearAfterCapture {
            overlayController.clear()
        }
        if wasDrawing && !settings.value.exitDrawModeAfterCapture {
            overlayController.setMode(.draw)
        }
        statusItem?.button?.toolTip = settings.value.copyAfterCapture
            ? "Saved \(destination.lastPathComponent) and copied it"
            : "Saved \(destination.lastPathComponent)"
        NSSound(named: "Glass")?.play()
    }

    private func handleCaptureError(_ error: Error) {
        if let captureError = error as? ScreenCaptureError,
           case .permissionDenied = captureError {
            if !showedScreenPermissionHelp {
                showedScreenPermissionHelp = true
                showScreenRecordingPermissionAlert()
            } else {
                statusItem?.button?.toolTip =
                    "Screen Recording permission is still required. Enable OverInk Dev in System Settings."
                NSSound.beep()
            }
            return
        }
        showAlert(
            message: "Capture failed",
            detail: error.localizedDescription
        )
    }

    private func openLibrary() {
        overlayController.setMode(.passThrough)
        editorController.showLibrary()
    }

    private func showScreenRecordingPermissionAlert() {
        let alert = NSAlert()
        alert.messageText = "Screen Recording permission is required"
        alert.informativeText =
            "Enable OverInk in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch it."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Open System Settings")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn,
           let settingsURL = URL(
               string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
           ) {
            NSWorkspace.shared.open(settingsURL)
        }
    }

    private func showAlert(message: String, detail: String) {
        let alert = NSAlert()
        alert.messageText = message
        alert.informativeText = detail
        alert.alertStyle = .informational
        alert.runModal()
    }
}
