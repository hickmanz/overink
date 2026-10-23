import AppKit
import ApplicationServices

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let overlayController = OverlayController()
    private let captureService = ScreenCaptureService()
    private let libraryController = CaptureLibraryController()
    private lazy var captureCoordinator = CaptureCoordinator(
        screenCapture: captureService,
        annotationSession: overlayController.session,
        excludedWindowIDs: { [weak self] in
            guard let self else { return [] }
            var ids = self.overlayController.windowIDs
            if let toolbarID = self.toolbarController?.windowID {
                ids.insert(toolbarID)
            }
            return ids
        }
    )
    private lazy var escapeHotKey = EscapeHotKey { [weak self] in
        self?.overlayController.setMode(.passThrough)
    }
    private var toolbarController: ToolbarController?
    private var statusItem: NSStatusItem?
    private var modeMenuItem: NSMenuItem?
    private var globalKeyMonitor: Any?
    private var localKeyMonitor: Any?
    private var showedScreenPermissionHelp = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureStatusItem()
        configureKeyboardShortcut()
        toolbarController = ToolbarController(
            overlayController: overlayController,
            onCapture: { [weak self] in self?.capture() },
            onOpenLibrary: { [weak self] in self?.openLibrary() }
        )
        overlayController.modeDidChange = { [weak self] mode in
            self?.refreshModeMenuItem(mode)
            self?.toolbarController?.updateMode(mode)
            self?.escapeHotKey.setEnabled(mode == .draw)
        }
        overlayController.show()
        toolbarController?.show()
        refreshModeMenuItem(overlayController.mode)
    }

    func applicationWillTerminate(_ notification: Notification) {
        escapeHotKey.shutdown()
        if let globalKeyMonitor {
            NSEvent.removeMonitor(globalKeyMonitor)
        }
        if let localKeyMonitor {
            NSEvent.removeMonitor(localKeyMonitor)
        }
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

        let inputPermissionItem = NSMenuItem(
            title: "Request Accessibility Permission…",
            action: #selector(requestAccessibilityPermission),
            keyEquivalent: ""
        )
        inputPermissionItem.target = self
        menu.addItem(inputPermissionItem)

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

    private func configureKeyboardShortcut() {
        globalKeyMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            let isEscape = event.keyCode == 53
            let isToggle = Self.isToggleShortcut(event)
            Task { @MainActor [weak self] in
                if isEscape {
                    self?.overlayController.setMode(.passThrough)
                } else if isToggle {
                    self?.overlayController.toggleMode()
                }
            }
        }

        localKeyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 {
                self?.overlayController.setMode(.passThrough)
                return nil
            }
            if Self.isToggleShortcut(event) {
                self?.overlayController.toggleMode()
                return nil
            }
            return event
        }
    }

    private static func isToggleShortcut(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        return modifiers == [.command, .shift] && event.charactersIgnoringModifiers?.lowercased() == "a"
    }

    private func refreshModeMenuItem(_ mode: OverlayController.InteractionMode) {
        switch mode {
        case .draw:
            modeMenuItem?.title = "Use Pass-Through Mode (⌘⇧A)"
            statusItem?.button?.contentTintColor = .systemRed
        case .passThrough:
            modeMenuItem?.title = "Start Drawing (⌘⇧A)"
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
        // Permission prompts and error dialogs must never appear behind a mouse-capturing overlay.
        overlayController.setMode(.passThrough)

        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                let destination = try await captureCoordinator.captureMainDisplay()
                libraryController.reload()
                statusItem?.button?.toolTip = "Saved \(destination.lastPathComponent) and copied it"
                NSSound(named: "Glass")?.play()
            } catch ScreenCaptureError.permissionDenied {
                if !showedScreenPermissionHelp {
                    showedScreenPermissionHelp = true
                    showScreenRecordingPermissionAlert()
                } else {
                    statusItem?.button?.toolTip =
                        "Screen Recording permission is still required. Enable OverInk Dev in System Settings."
                    NSSound.beep()
                }
            } catch {
                showAlert(
                    message: "Capture failed",
                    detail: error.localizedDescription
                )
            }
        }
    }

    private func openLibrary() {
        overlayController.setMode(.passThrough)
        libraryController.show()
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

    @objc private func requestAccessibilityPermission() {
        // The public constant is imported as mutable global state, which Swift 6 rejects from
        // actor-isolated code. Its documented string value is stable across supported macOS versions.
        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        let trusted = AXIsProcessTrustedWithOptions(options)
        if !trusted {
            showAlert(
                message: "Accessibility permission is not enabled.",
                detail: "Enable OverInk in System Settings → Privacy & Security → Accessibility for the global drawing shortcut."
            )
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
