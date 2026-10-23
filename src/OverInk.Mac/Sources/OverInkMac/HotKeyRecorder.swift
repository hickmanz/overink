import AppKit
import OverInkCore

extension KeyChord.Modifiers {
    init(_ flags: NSEvent.ModifierFlags) {
        var result: KeyChord.Modifiers = []
        if flags.contains(.command) { result.insert(.command) }
        if flags.contains(.option) { result.insert(.option) }
        if flags.contains(.control) { result.insert(.control) }
        if flags.contains(.shift) { result.insert(.shift) }
        self = result
    }
}

/// A button that records a keyboard shortcut. Click it, then press the combination.
/// Escape cancels, Delete clears. A combination needs a modifier unless it is a function key.
@MainActor
final class HotKeyRecorderButton: NSButton {
    var onChange: ((KeyChord?) -> Void)?
    private(set) var chord: KeyChord?
    private var monitor: Any?
    private var isRecording = false
    private let emptyTitle: String

    init(chord: KeyChord?, emptyTitle: String = "Click to record") {
        self.chord = chord
        self.emptyTitle = emptyTitle
        super.init(frame: NSRect(x: 0, y: 0, width: 190, height: 28))
        bezelStyle = .rounded
        target = self
        action = #selector(toggleRecording)
        widthAnchor.constraint(equalToConstant: 190).isActive = true
        refreshTitle()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    func setChord(_ newChord: KeyChord?) {
        chord = newChord
        refreshTitle()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window == nil { stopRecording() }
    }

    @objc private func toggleRecording() {
        isRecording ? stopRecording() : startRecording()
    }

    private func startRecording() {
        isRecording = true
        title = "Press shortcut…"
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            let keyCode = UInt32(event.keyCode)
            let flags = event.modifierFlags
            let consumed = MainActor.assumeIsolated { self?.handle(keyCode: keyCode, flags: flags) ?? false }
            return consumed ? nil : event
        }
    }

    private func stopRecording() {
        isRecording = false
        if let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
        refreshTitle()
    }

    /// Returns true when the event was consumed.
    private func handle(keyCode: UInt32, flags: NSEvent.ModifierFlags) -> Bool {
        switch keyCode {
        case KeyCodes.escape:
            stopRecording()
        case KeyCodes.delete, KeyCodes.forwardDelete:
            chord = nil
            stopRecording()
            onChange?(nil)
        default:
            let candidate = KeyChord(keyCode: keyCode, modifiers: KeyChord.Modifiers(flags))
            if candidate.isValidGlobalHotKey {
                chord = candidate
                stopRecording()
                onChange?(candidate)
            } else {
                NSSound.beep()
                title = "Add ⌃ ⌥ ⇧ or ⌘"
            }
        }
        return true
    }

    private func refreshTitle() {
        title = chord?.displayString ?? emptyTitle
    }
}

enum HotKeyPrompt {
    /// Shows a small dialog with a recorder. `onChange` fires as soon as the shortcut changes.
    @MainActor
    static func present(
        title: String,
        message: String,
        current: KeyChord?,
        onChange: @escaping (KeyChord?) -> Void
    ) {
        let recorder = HotKeyRecorderButton(chord: current)
        recorder.onChange = onChange

        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.accessoryView = recorder
        alert.addButton(withTitle: "Done")
        NSApplication.shared.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
