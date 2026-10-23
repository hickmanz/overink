import Foundation

public struct MacCaptureBehavior: Equatable, Sendable {
    public var copyAfterCapture: Bool
    public var exitDrawModeAfterCapture: Bool
    public var clearAfterCapture: Bool

    public init(copyAfterCapture: Bool, exitDrawModeAfterCapture: Bool, clearAfterCapture: Bool) {
        self.copyAfterCapture = copyAfterCapture
        self.exitDrawModeAfterCapture = exitDrawModeAfterCapture
        self.clearAfterCapture = clearAfterCapture
    }
}

/// Persistent Mac settings. Every field decodes with a default so settings files written by older
/// versions (missing newer keys) still load.
public struct MacAppSettings: Codable, Equatable, Sendable {
    public var penColor = "#E53935"
    public var penWidth = 4.0
    public var pressureSensitive = true
    public var veilOpacity = 0.0
    public var tool = SketchTool.pen

    public var copyAfterCapture = true
    public var exitDrawModeAfterCapture = true
    public var clearAfterCapture = false

    public var toolbarX: Double?
    public var toolbarY: Double?

    public var drawHotkey = KeyChord.defaultDrawToggle

    public var toolbarVertical = false
    /// Compact: one tool button with a flyout plus the eraser. Expanded: a button per tool.
    public var compactTools = true
    public var customColors: [String] = []
    public var hideToolbarWhenNotDrawing = false

    public var captureMode = CaptureMode.auto
    /// Global shortcut that starts region/window capture. Unbound by default (as on Windows).
    public var captureHotkey: KeyChord?
    public var includeToolbarInCaptures = false
    public var openEditorAfterCapture = false

    /// Custom capture library folder. `nil` means `Pictures/OverInk`.
    public var libraryFolder: String?

    public init() {}

    public var captureBehavior: MacCaptureBehavior {
        get {
            MacCaptureBehavior(
                copyAfterCapture: copyAfterCapture,
                exitDrawModeAfterCapture: exitDrawModeAfterCapture,
                clearAfterCapture: clearAfterCapture
            )
        }
        set {
            copyAfterCapture = newValue.copyAfterCapture
            exitDrawModeAfterCapture = newValue.exitDrawModeAfterCapture
            clearAfterCapture = newValue.clearAfterCapture
        }
    }

    private enum CodingKeys: String, CodingKey {
        case penColor, penWidth, pressureSensitive, veilOpacity, tool
        case copyAfterCapture, exitDrawModeAfterCapture, clearAfterCapture
        case toolbarX, toolbarY
        case drawHotkey
        case toolbarVertical, compactTools, customColors, hideToolbarWhenNotDrawing
        case captureMode, captureHotkey, includeToolbarInCaptures, openEditorAfterCapture
        case libraryFolder
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = MacAppSettings()
        penColor = try c.decodeIfPresent(String.self, forKey: .penColor) ?? d.penColor
        penWidth = try c.decodeIfPresent(Double.self, forKey: .penWidth) ?? d.penWidth
        pressureSensitive = try c.decodeIfPresent(Bool.self, forKey: .pressureSensitive) ?? d.pressureSensitive
        veilOpacity = try c.decodeIfPresent(Double.self, forKey: .veilOpacity) ?? d.veilOpacity
        tool = (try? c.decodeIfPresent(SketchTool.self, forKey: .tool)) ?? d.tool
        copyAfterCapture = try c.decodeIfPresent(Bool.self, forKey: .copyAfterCapture) ?? d.copyAfterCapture
        exitDrawModeAfterCapture = try c.decodeIfPresent(Bool.self, forKey: .exitDrawModeAfterCapture)
            ?? d.exitDrawModeAfterCapture
        clearAfterCapture = try c.decodeIfPresent(Bool.self, forKey: .clearAfterCapture) ?? d.clearAfterCapture
        toolbarX = try c.decodeIfPresent(Double.self, forKey: .toolbarX)
        toolbarY = try c.decodeIfPresent(Double.self, forKey: .toolbarY)
        drawHotkey = (try? c.decodeIfPresent(KeyChord.self, forKey: .drawHotkey)) ?? d.drawHotkey
        toolbarVertical = try c.decodeIfPresent(Bool.self, forKey: .toolbarVertical) ?? d.toolbarVertical
        compactTools = try c.decodeIfPresent(Bool.self, forKey: .compactTools) ?? d.compactTools
        customColors = CustomColorPalette(
            normalizing: try c.decodeIfPresent([String].self, forKey: .customColors) ?? []
        ).colors
        hideToolbarWhenNotDrawing = try c.decodeIfPresent(Bool.self, forKey: .hideToolbarWhenNotDrawing)
            ?? d.hideToolbarWhenNotDrawing
        captureMode = (try? c.decodeIfPresent(CaptureMode.self, forKey: .captureMode)) ?? d.captureMode
        captureHotkey = try? c.decodeIfPresent(KeyChord.self, forKey: .captureHotkey)
        includeToolbarInCaptures = try c.decodeIfPresent(Bool.self, forKey: .includeToolbarInCaptures)
            ?? d.includeToolbarInCaptures
        openEditorAfterCapture = try c.decodeIfPresent(Bool.self, forKey: .openEditorAfterCapture)
            ?? d.openEditorAfterCapture
        libraryFolder = try c.decodeIfPresent(String.self, forKey: .libraryFolder)
    }
}

public struct MacSettingsStore: Sendable {
    public let fileURL: URL

    public init(fileURL: URL = MacSettingsStore.defaultFileURL) {
        self.fileURL = fileURL
    }

    public static var defaultFileURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base
            .appendingPathComponent("OverInk", isDirectory: true)
            .appendingPathComponent("settings.json")
    }

    public func load() -> MacAppSettings {
        guard let data = try? Data(contentsOf: fileURL),
              let settings = try? JSONDecoder().decode(MacAppSettings.self, from: data) else {
            return MacAppSettings()
        }
        return settings
    }

    public func save(_ settings: MacAppSettings) {
        do {
            try FileManager.default.createDirectory(
                at: fileURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(settings).write(to: fileURL, options: .atomic)
        } catch {
            NSLog("OverInk settings save failed: %@", error.localizedDescription)
        }
    }
}
