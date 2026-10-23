import Foundation

/// Single-letter tool shortcuts used while drawing (and in the editor): P E L A R O.
public enum DrawShortcutMap {
    public static let entries: [(keyCode: UInt32, letter: String, tool: SketchTool)] = [
        (KeyCodes.p, "P", .pen),
        (KeyCodes.e, "E", .eraser),
        (KeyCodes.l, "L", .line),
        (KeyCodes.a, "A", .arrow),
        (KeyCodes.r, "R", .rectangle),
        (KeyCodes.o, "O", .ellipse)
    ]

    public static func tool(forKeyCode keyCode: UInt32) -> SketchTool? {
        entries.first { $0.keyCode == keyCode }?.tool
    }

    public static func letter(for tool: SketchTool) -> String? {
        entries.first { $0.tool == tool }?.letter
    }

    public static func tool(forLetter letter: String) -> SketchTool? {
        entries.first { $0.letter.caseInsensitiveCompare(letter) == .orderedSame }?.tool
    }
}
