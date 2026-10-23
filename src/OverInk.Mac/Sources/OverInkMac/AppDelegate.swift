import AppKit
import OverInkCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
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
    private lazy var settingsController = SettingsWindowController(settings: settings)
    private lazy var onboardingController = OnboardingWindowController(settings: settings) { [weak self] in
        self?.openSettings()
    }
    private var loginItemMenuItem: NSMenuItem?
    private var remoteHost: RemoteHost?

    /// Launch arguments for development: `--library`, `--settings`, `--welcome`, or `--open-editor <capture PNG>`.
    private func handleLaunchArguments() {
        let arguments = CommandLine.arguments
        if arguments.contains("--library") {
            editorController.showLibrary()
        }
        if arguments.contains("--settings") {
            openSettings()
        }
        if arguments.contains("--welcome") {
            onboardingController.show()
        }
        if let index = arguments.firstIndex(of: "--open-editor"), arguments.indices.contains(index + 1) {
            editorController.open(URL(fileURLWithPath: arguments[index + 1]))
        }
    }

    private var isDevelopmentLaunch: Bool {
        CommandLine.arguments.contains { ["--library", "--open-editor", "--settings", "--welcome", "--remote"].contains($0) }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // One copy at a time: a second launch asks the first to show itself and quits.
        if !isDevelopmentLaunch, SingleInstance.handOffToExistingInstance() {
            NSApplication.shared.terminate(nil)
            return
        }
        SingleInstance.observeShowRequests { [weak self] in
            self?.toolbarController?.show()
        }
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
            onConfigureCaptureHotkey: { [weak self] in self?.configureCaptureHotkey() },
            onOpenSettings: { [weak self] in self?.openSettings() }
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
        startRemoteHost()
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
        var lastDrawHotkey = settings.value.drawHotkey
        settings.observe { [weak self] in
            guard let self, self.settings.value.drawHotkey != lastDrawHotkey else { return }
            let previous = lastDrawHotkey
            lastDrawHotkey = self.settings.value.drawHotkey
            self.drawToggleHotKey?.update(chord: lastDrawHotkey)
            if self.drawToggleHotKey?.isRegistered == false {
                // Keep the old shortcut when the new one is taken by macOS or another app.
                let rejected = lastDrawHotkey
                lastDrawHotkey = previous
                self.drawToggleHotKey?.update(chord: previous)
                self.settings.update { $0.drawHotkey = previous }
                self.showAlert(
                    message: "That shortcut is unavailable.",
                    detail: "\(rejected.displayString) is reserved by macOS or another app. Choose a different shortcut."
                )
            }
            self.refreshModeMenuItem(self.overlayController.mode)
        }
        refreshModeMenuItem(overlayController.mode)
        handleLaunchArguments()
        if !settings.value.hasCompletedOnboarding, !isDevelopmentLaunch {
            onboardingController.show()
        }
    }

    // MARK: URLs

    func application(_ application: NSApplication, open urls: [URL]) {
        urls.compactMap(AppURL.parse).forEach(perform)
    }

    private func perform(_ action: AppURLAction) {
        switch action {
        case .library:
            openLibrary()
        case .settings:
            openSettings()
        case .draw:
            toolbarController?.show()
            overlayController.toggleMode()
        case let .editor(capture):
            let folder = CaptureLibrary.folder(forSetting: settings.value.libraryFolder)
            overlayController.setMode(.passThrough)
            if let capture, let url = AppURL.resolveCapture(capture, in: folder) {
                editorController.open(url)
            } else {
                editorController.showLibrary()
            }
        }
    }

    private func openSettings() {
        overlayController.setMode(.passThrough)
        settingsController.show()
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

    /// The LAN remote: starts when enabled in settings (or with `--remote`) and follows later changes.
    private func startRemoteHost() {
        let host = RemoteHost(
            settings: settings,
            session: overlayController.session,
            overlay: overlayController,
            pairing: PairingService(store: KeychainPairedDeviceStore()),
            toolbarFrame: { [weak self] in self?.toolbarController?.frame }
        )
        host.forceEnabled = CommandLine.arguments.contains("--remote")
        remoteHost = host
        var lastRemote = settings.value.remote
        settings.observe { [weak self] in
            guard let self, self.settings.value.remote != lastRemote else { return }
            lastRemote = self.settings.value.remote
            self.remoteHost?.apply()
        }
        host.apply()
    }

    func applicationWillTerminate(_ notification: Notification) {
        remoteHost?.stop()
        editorController.flush()
        overlayController.shutdown()
        drawToggleHotKey?.shutdown()
        captureHotKey?.shutdown()
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

        let undoItem = NSMenuItem(title: "Undo", action: #selector(undo), keyEquivalent: "z")
        undoItem.keyEquivalentModifierMask = [.command]
        undoItem.target = self
        menu.addItem(undoItem)

        let redoItem = NSMenuItem(title: "Redo", action: #selector(redo), keyEquivalent: "Z")
        redoItem.keyEquivalentModifierMask = [.command, .shift]
        redoItem.target = self
        menu.addItem(redoItem)

        menu.addItem(.separator())

        let settingsItem = NSMenuItem(title: "Settings…", action: #selector(openSettingsFromMenu), keyEquivalent: ",")
        settingsItem.target = self
        menu.addItem(settingsItem)

        let loginItem = NSMenuItem(title: "Open at Login", action: #selector(toggleLoginItem), keyEquivalent: "")
        loginItem.target = self
        menu.addItem(loginItem)
        loginItemMenuItem = loginItem

        let aboutItem = NSMenuItem(title: "About OverInk", action: #selector(showAbout), keyEquivalent: "")
        aboutItem.target = self
        menu.addItem(aboutItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(
            title: "Quit OverInk",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        menu.addItem(quitItem)

        menu.delegate = self
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

    @objc private func openSettingsFromMenu() {
        openSettings()
    }

    @objc private func toggleLoginItem() {
        do {
            try LoginItem.setEnabled(!LoginItem.isEnabled)
            if LoginItem.needsApproval { LoginItem.openSystemSettings() }
        } catch {
            showAlert(
                message: "Couldn't change the login item",
                detail: "\(error.localizedDescription)\n\nOpen at login works from the installed OverInk app."
            )
        }
    }

    @objc private func showAbout() {
        NSApplication.shared.activate(ignoringOtherApps: true)
        NSApplication.shared.orderFrontStandardAboutPanel(nil)
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        loginItemMenuItem?.state = LoginItem.isEnabled ? .on : .off
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
