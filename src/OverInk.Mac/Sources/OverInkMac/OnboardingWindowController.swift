import AppKit
import OverInkCore

/// First-run welcome: Screen Recording permission and the draw hotkey.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private let settings: SettingsModel
    private let onOpenSettings: () -> Void
    private var window: NSWindow?
    private var timer: Timer?
    private let statusLabel = WindowChrome.label("")
    private let allowButton = ClosureButton(title: "Allow Screen Recording…")

    init(settings: SettingsModel, onOpenSettings: @escaping () -> Void) {
        self.settings = settings
        self.onOpenSettings = onOpenSettings
        super.init()
    }

    func show() {
        if window == nil { build() }
        refreshPermission()
        // Permission is granted in System Settings, so poll while the window is up.
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshPermission() }
        }
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    private func build() {
        let width: CGFloat = 520
        let window = WindowChrome.window(title: "Welcome to OverInk", width: width)
        window.delegate = self

        let stack = NSStackView()
        stack.spacing = 14

        let title = WindowChrome.label("Welcome to OverInk", size: 24, weight: .bold)
        let subtitle = WindowChrome.label(
            "Draw on top of anything on your screen, then capture it. Two quick things to set up first.",
            secondary: true
        )
        subtitle.preferredMaxLayoutWidth = width - 48
        stack.addArrangedSubview(title)
        stack.addArrangedSubview(subtitle)

        stack.addArrangedSubview(step(
            "1. Allow Screen Recording",
            "OverInk needs it to take captures of what you annotate. macOS may ask you to relaunch OverInk once you allow it.",
            accessory: permissionAccessory()
        ))

        let drawRecorder = HotKeyRecorderButton(chord: settings.value.drawHotkey, emptyTitle: "Not set")
        drawRecorder.onChange = { [weak self] chord in
            self?.settings.update { $0.drawHotkey = chord ?? KeyChord.defaultDrawToggle }
        }
        stack.addArrangedSubview(step(
            "2. Choose a drawing hotkey",
            "Press it anywhere to start drawing. Press Escape to stop. Click the button to change it.",
            accessory: drawRecorder
        ))

        let captureHint = WindowChrome.label(
            "Capture with the camera button in the toolbar. You can also set a capture hotkey in Settings.",
            secondary: true
        )
        captureHint.preferredMaxLayoutWidth = width - 48
        stack.addArrangedSubview(captureHint)

        let settingsButton = ClosureButton(title: "Open Settings…")
        settingsButton.onPress = { [weak self] in self?.onOpenSettings() }
        let done = ClosureButton(title: "Get Started")
        done.keyEquivalent = "\r"
        done.onPress = { [weak self] in self?.finish() }
        let buttons = NSStackView(views: [NSView(), settingsButton, done])
        buttons.orientation = .horizontal
        buttons.spacing = 10
        buttons.widthAnchor.constraint(equalToConstant: width - 48).isActive = true
        stack.addArrangedSubview(buttons)

        WindowChrome.install(stack, in: window, root: WindowChrome.background(), width: width)
        window.center()
        self.window = window
    }

    private func permissionAccessory() -> NSView {
        allowButton.onPress = {
            if !ScreenPermission.request() { ScreenPermission.openSystemSettings() }
        }
        let line = NSStackView(views: [statusLabel, allowButton])
        line.orientation = .horizontal
        line.spacing = 12
        return line
    }

    private func step(_ title: String, _ detail: String, accessory: NSView) -> NSView {
        let heading = WindowChrome.label(title, size: 14, weight: .semibold)
        let text = WindowChrome.label(detail, size: 12, secondary: true)
        text.preferredMaxLayoutWidth = 520 - 48
        let stack = NSStackView(views: [heading, text, accessory])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        return stack
    }

    private func refreshPermission() {
        let granted = ScreenPermission.isGranted
        statusLabel.stringValue = granted ? "Allowed" : "Not allowed yet"
        statusLabel.textColor = granted ? .systemGreen : .systemOrange
        allowButton.isEnabled = !granted
    }

    private func finish() {
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        timer = nil
        settings.update { $0.hasCompletedOnboarding = true }
    }
}
