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
