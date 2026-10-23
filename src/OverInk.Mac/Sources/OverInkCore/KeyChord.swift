import Foundation

/// A keyboard shortcut: a macOS virtual key code plus modifier keys.
/// Kept free of Carbon/AppKit so it can be stored in settings and tested.
public struct KeyChord: Codable, Equatable, Hashable, Sendable {
    public struct Modifiers: OptionSet, Codable, Hashable, Sendable {
        public let rawValue: Int
        public init(rawValue: Int) { self.rawValue = rawValue }

        public static let command = Modifiers(rawValue: 1 << 0)
        public static let option = Modifiers(rawValue: 1 << 1)
        public static let control = Modifiers(rawValue: 1 << 2)
        public static let shift = Modifiers(rawValue: 1 << 3)
    }

    public var keyCode: UInt32
    public var modifiers: Modifiers

    public init(keyCode: UInt32, modifiers: Modifiers = []) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    /// Windows parity default for the draw toggle: Ctrl+Shift+D.
    public static let defaultDrawToggle = KeyChord(keyCode: KeyCodes.d, modifiers: [.control, .shift])

    /// Human-readable form in macOS order, for example `⌃⇧D`.
    public var displayString: String {
        var text = ""
        if modifiers.contains(.control) { text += "⌃" }
        if modifiers.contains(.option) { text += "⌥" }
        if modifiers.contains(.shift) { text += "⇧" }
        if modifiers.contains(.command) { text += "⌘" }
        return text + (KeyCodes.label(for: keyCode) ?? "Key \(keyCode)")
    }

    /// A chord may be registered globally only if it has a modifier, or is a function key.
    public var isValidGlobalHotKey: Bool {
        !modifiers.isEmpty || KeyCodes.isFunctionKey(keyCode)
    }
}

/// ANSI virtual key codes (stable across macOS versions) and labels.
public enum KeyCodes {
    public static let a: UInt32 = 0
    public static let s: UInt32 = 1
    public static let d: UInt32 = 2
    public static let f: UInt32 = 3
    public static let h: UInt32 = 4
    public static let g: UInt32 = 5
    public static let z: UInt32 = 6
    public static let x: UInt32 = 7
    public static let c: UInt32 = 8
    public static let v: UInt32 = 9
    public static let b: UInt32 = 11
    public static let q: UInt32 = 12
    public static let w: UInt32 = 13
    public static let e: UInt32 = 14
    public static let r: UInt32 = 15
    public static let y: UInt32 = 16
    public static let t: UInt32 = 17
    public static let o: UInt32 = 31
    public static let u: UInt32 = 32
    public static let i: UInt32 = 34
    public static let p: UInt32 = 35
    public static let l: UInt32 = 37
    public static let j: UInt32 = 38
    public static let k: UInt32 = 40
    public static let n: UInt32 = 45
    public static let m: UInt32 = 46
    public static let escape: UInt32 = 53
    public static let delete: UInt32 = 51
    public static let forwardDelete: UInt32 = 117

    private static let labels: [UInt32: String] = [
        0: "A", 1: "S", 2: "D", 3: "F", 4: "H", 5: "G", 6: "Z", 7: "X", 8: "C", 9: "V",
        11: "B", 12: "Q", 13: "W", 14: "E", 15: "R", 16: "Y", 17: "T",
        18: "1", 19: "2", 20: "3", 21: "4", 22: "6", 23: "5", 24: "=", 25: "9", 26: "7",
        27: "-", 28: "8", 29: "0", 30: "]", 31: "O", 32: "U", 33: "[", 34: "I", 35: "P",
        36: "↩", 37: "L", 38: "J", 39: "'", 40: "K", 41: ";", 42: "\\", 43: ",", 44: "/",
        45: "N", 46: "M", 47: ".", 48: "⇥", 49: "Space", 50: "`", 51: "⌫", 53: "⎋",
        96: "F5", 97: "F6", 98: "F7", 99: "F3", 100: "F8", 101: "F9", 103: "F11",
        105: "F13", 106: "F16", 107: "F14", 109: "F10", 111: "F12", 113: "F15",
        115: "↖", 116: "⇞", 117: "⌦", 118: "F4", 119: "↘", 120: "F2", 121: "⇟", 122: "F1",
        123: "←", 124: "→", 125: "↓", 126: "↑"
    ]

    private static let functionKeys: Set<UInt32> = [
        122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106
    ]

    public static func label(for keyCode: UInt32) -> String? { labels[keyCode] }

    public static func isFunctionKey(_ keyCode: UInt32) -> Bool { functionKeys.contains(keyCode) }
}
