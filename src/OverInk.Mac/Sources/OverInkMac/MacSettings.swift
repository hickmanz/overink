import Foundation
import OverInkCore

struct MacCaptureBehavior {
    var copyAfterCapture: Bool
    var exitDrawModeAfterCapture: Bool
    var clearAfterCapture: Bool
}

struct MacAppSettings: Codable, Equatable {
    var penColor = "#E53935"
    var penWidth = 4.0
    var pressureSensitive = true
    var veilOpacity = 0.0
    var tool = SketchTool.pen

    var copyAfterCapture = true
    var exitDrawModeAfterCapture = true
    var clearAfterCapture = false

    var toolbarX: Double?
    var toolbarY: Double?

    var captureBehavior: MacCaptureBehavior {
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
}

struct MacSettingsStore {
    private let fileManager = FileManager.default

    func load() -> MacAppSettings {
        guard let data = try? Data(contentsOf: settingsURL),
              let settings = try? JSONDecoder().decode(MacAppSettings.self, from: data) else {
            return MacAppSettings()
        }
        return settings
    }

    func save(_ settings: MacAppSettings) {
        do {
            let folder = settingsURL.deletingLastPathComponent()
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(settings).write(to: settingsURL, options: .atomic)
        } catch {
            NSLog("OverInk settings save failed: %@", error.localizedDescription)
        }
    }

    private var settingsURL: URL {
        let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first!
        return applicationSupport
            .appendingPathComponent("OverInk", isDirectory: true)
            .appendingPathComponent("settings.json")
    }
}
