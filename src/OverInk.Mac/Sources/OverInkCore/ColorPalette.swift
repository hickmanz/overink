import Foundation

public enum ColorHex {
    /// Normalizes `#RGB`, `RRGGBB`, or `#RRGGBB` input to uppercase `#RRGGBB`. Returns `nil` if invalid.
    public static func normalize(_ raw: String) -> String? {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("#") { text.removeFirst() }
        if text.count == 3 {
            text = text.map { "\($0)\($0)" }.joined()
        }
        guard text.count == 6, text.allSatisfy(\.isHexDigit) else { return nil }
        return "#" + text.uppercased()
    }
}

/// The default swatches shown on the toolbar and the iPad remote (same list as Windows).
public enum PresetPalette {
    public static let hexColors = [
        "#E53935", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#1E88E5", "#00ACC1",
        "#43A047", "#CDDC39", "#FDD835", "#FB8C00", "#795548", "#FFFFFF", "#212121"
    ]

    public static func isPreset(_ hex: String) -> Bool {
        guard let normalized = ColorHex.normalize(hex) else { return false }
        return hexColors.contains(normalized)
    }
}

/// The user's saved custom colors, shared by the toolbar, editor, and iPad remote.
public struct CustomColorPalette: Equatable, Sendable {
    public static let maxColors = 28

    public private(set) var colors: [String]

    /// Cleans an older or hand-edited list: valid `#RRGGBB` only, no presets, no duplicates, capped.
    public init(normalizing raw: [String] = []) {
        var cleaned: [String] = []
        for entry in raw {
            guard let hex = ColorHex.normalize(entry),
                  !PresetPalette.isPreset(hex),
                  !cleaned.contains(hex) else { continue }
            cleaned.append(hex)
            if cleaned.count >= Self.maxColors { break }
        }
        colors = cleaned
    }

    /// True for a color that already has a swatch (a preset or a saved color).
    public func hasSwatch(_ hex: String) -> Bool {
        guard let normalized = ColorHex.normalize(hex) else { return false }
        return PresetPalette.isPreset(normalized) || colors.contains(normalized)
    }

    /// Saves a color. Returns false for presets, duplicates, invalid input, and a full palette.
    @discardableResult
    public mutating func add(_ hex: String) -> Bool {
        guard let normalized = ColorHex.normalize(hex),
              !hasSwatch(normalized),
              colors.count < Self.maxColors else { return false }
        colors.append(normalized)
        return true
    }

    @discardableResult
    public mutating func remove(_ hex: String) -> Bool {
        guard let normalized = ColorHex.normalize(hex),
              let index = colors.firstIndex(of: normalized) else { return false }
        colors.remove(at: index)
        return true
    }
}
