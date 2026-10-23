import Foundation
import Testing
@testable import OverInkCore

@Suite struct SettingsTests {
    @Test func emptyObjectDecodesToDefaults() throws {
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data("{}".utf8))
        #expect(decoded == MacAppSettings())
    }

    @Test func olderFileMissingNewKeysKeepsStoredValuesAndDefaultsTheRest() throws {
        let json = ##"{"penColor":"#112233","penWidth":9,"copyAfterCapture":false}"##
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data(json.utf8))

        #expect(decoded.penColor == "#112233")
        #expect(decoded.penWidth == 9)
        #expect(!decoded.copyAfterCapture)
        #expect(decoded.drawHotkey == KeyChord.defaultDrawToggle)
        #expect(decoded.exitDrawModeAfterCapture)
    }

    @Test func unknownToolFallsBackToDefault() throws {
        let json = #"{"tool":"laser"}"#
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data(json.utf8))
        #expect(decoded.tool == .pen)
    }

    @Test func storeRoundTripsAndToleratesMissingFile() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("overink-tests-\(UUID().uuidString)")
            .appendingPathComponent("settings.json")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        let store = MacSettingsStore(fileURL: url)

        #expect(store.load() == MacAppSettings())

        var settings = MacAppSettings()
        settings.penColor = "#ABCDEF"
        settings.toolbarX = 42
        settings.drawHotkey = KeyChord(keyCode: KeyCodes.a, modifiers: [.command, .option])
        store.save(settings)

        #expect(store.load() == settings)
    }

    @Test func corruptFileFallsBackToDefaults() throws {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("overink-tests-\(UUID().uuidString)")
            .appendingPathComponent("settings.json")
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        try Data("not json".utf8).write(to: url)

        #expect(MacSettingsStore(fileURL: url).load() == MacAppSettings())
    }

    @Test func keyChordDisplayAndValidity() {
        #expect(KeyChord.defaultDrawToggle.displayString == "⌃⇧D")
        #expect(KeyChord(keyCode: KeyCodes.a, modifiers: [.command, .shift]).displayString == "⇧⌘A")
        #expect(!KeyChord(keyCode: KeyCodes.a).isValidGlobalHotKey)
        #expect(KeyChord(keyCode: 122).isValidGlobalHotKey) // F1 needs no modifier
    }
}

@Suite struct ColorPaletteTests {
    @Test func hexNormalization() {
        #expect(ColorHex.normalize("#abc") == "#AABBCC")
        #expect(ColorHex.normalize("e53935") == "#E53935")
        #expect(ColorHex.normalize(" #00ff00 ") == "#00FF00")
        #expect(ColorHex.normalize("#12345") == nil)
        #expect(ColorHex.normalize("#GGGGGG") == nil)
    }

    @Test func presetsHaveFourteenSwatches() {
        #expect(PresetPalette.hexColors.count == 14)
        #expect(PresetPalette.isPreset("#e53935"))
        #expect(!PresetPalette.isPreset("#123456"))
    }

    @Test func addRejectsPresetsDuplicatesAndInvalid() {
        var palette = CustomColorPalette()
        let preset = palette.add("#E53935")
        let fresh = palette.add("#123456")
        let duplicate = palette.add("#123456")
        let invalid = palette.add("nope")
        #expect(!preset)
        #expect(fresh)
        #expect(!duplicate)
        #expect(!invalid)
        #expect(palette.colors == ["#123456"])
    }

    @Test func paletteIsCapped() {
        var palette = CustomColorPalette()
        for value in 0 ..< 40 {
            palette.add(String(format: "#%06X", 0x100000 + value))
        }
        #expect(palette.colors.count == CustomColorPalette.maxColors)
    }

    @Test func removeIsCaseInsensitive() {
        var palette = CustomColorPalette(normalizing: ["#abcdef"])
        let removed = palette.remove("#ABCDEF")
        #expect(removed)
        #expect(palette.colors.isEmpty)
    }

    @Test func normalizingDropsJunkWhenLoadingSettings() throws {
        let json = ##"{"customColors":["#123456","#123456","nothex","#E53935","#654321"]}"##
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data(json.utf8))
        #expect(decoded.customColors == ["#123456", "#654321"])
    }

    @Test func newToolbarSettingsDefaultForOldFiles() throws {
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data("{}".utf8))
        #expect(decoded.compactTools)
        #expect(!decoded.toolbarVertical)
        #expect(!decoded.hideToolbarWhenNotDrawing)
    }
}

@Suite struct OnboardingSettingTests {
    @Test func oldSettingsFilesDefaultToNotOnboarded() throws {
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: Data("{}".utf8))
        #expect(!decoded.hasCompletedOnboarding)
    }

    @Test func onboardingFlagRoundTrips() throws {
        var settings = MacAppSettings()
        settings.hasCompletedOnboarding = true
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: JSONEncoder().encode(settings))
        #expect(decoded.hasCompletedOnboarding)
    }
}
