import Foundation
import OverInkCore

/// Shortcuts that are live only while drawing: P/E/L/A/R/O pick tools, Command-Z / Shift-Command-Z
/// undo and redo, and Escape returns to pass-through. They are registered as system hotkeys while
/// drawing (as the Windows build does with its keyboard hook) so they work without focusing OverInk.
@MainActor
final class DrawShortcuts {
    private var bindings: [HotKeyBinding] = []

    init(
        selectTool: @escaping (SketchTool) -> Void,
        undo: @escaping () -> Void,
        redo: @escaping () -> Void,
        exitDrawing: @escaping () -> Void
    ) {
        for entry in DrawShortcutMap.entries {
            let tool = entry.tool
            bindings.append(
                HotKeyBinding(chord: KeyChord(keyCode: entry.keyCode), enabled: false, allowsBareKey: true) {
                    selectTool(tool)
                }
            )
        }
        bindings.append(
            HotKeyBinding(
                chord: KeyChord(keyCode: KeyCodes.z, modifiers: [.command]),
                enabled: false,
                allowsBareKey: true,
                action: undo
            )
        )
        bindings.append(
            HotKeyBinding(
                chord: KeyChord(keyCode: KeyCodes.z, modifiers: [.command, .shift]),
                enabled: false,
                allowsBareKey: true,
                action: redo
            )
        )
        bindings.append(
            HotKeyBinding(
                chord: KeyChord(keyCode: KeyCodes.escape),
                enabled: false,
                allowsBareKey: true,
                action: exitDrawing
            )
        )
    }

    func setEnabled(_ enabled: Bool) {
        bindings.forEach { $0.setEnabled(enabled) }
    }

    func shutdown() {
        bindings.forEach { $0.shutdown() }
    }
}
