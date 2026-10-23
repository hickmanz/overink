import Foundation
import Testing
@testable import OverInkCore

@Suite struct UpdatePolicyTests {
    @Test func promptsAutomaticallyUntilThreeDismissals() {
        var updates = UpdateSettings()
        #expect(updates.shouldPromptAutomatically(for: "0.3.0"))
        updates.recordDismissal(of: "0.3.0")
        updates.recordDismissal(of: "0.3.0")
        #expect(updates.shouldPromptAutomatically(for: "0.3.0"))
        updates.recordDismissal(of: "0.3.0")
        #expect(updates.skippedCount == 3)
        #expect(!updates.shouldPromptAutomatically(for: "0.3.0"))
    }

    @Test func newerVersionStartsTheCountOver() {
        var updates = UpdateSettings()
        for _ in 0..<3 { updates.recordDismissal(of: "0.3.0") }
        #expect(updates.shouldPromptAutomatically(for: "0.3.1"))
        updates.recordDismissal(of: "0.3.1")
        #expect(updates.skippedVersion == "0.3.1")
        #expect(updates.skippedCount == 1)
        #expect(updates.shouldPromptAutomatically(for: "0.3.1"))
    }

    @Test func versionComparisonIgnoresCaseAndWhitespace() {
        var updates = UpdateSettings()
        for _ in 0..<3 { updates.recordDismissal(of: "0.3.0-Beta.1") }
        #expect(!updates.shouldPromptAutomatically(for: " 0.3.0-beta.1 "))
    }

    @Test func channelsFollowTheBetaSwitch() {
        var updates = UpdateSettings()
        #expect(updates.allowedChannels.isEmpty)
        updates.includeBeta = true
        #expect(updates.allowedChannels == ["beta"])
    }

    @Test func settingsRoundTripAndTolerateMissingKeys() throws {
        var settings = MacAppSettings()
        settings.updates.includeBeta = true
        settings.updates.recordDismissal(of: "1.2.3")
        let data = try JSONEncoder().encode(settings)
        let decoded = try JSONDecoder().decode(MacAppSettings.self, from: data)
        #expect(decoded.updates == settings.updates)

        let old = try JSONDecoder().decode(MacAppSettings.self, from: Data("{}".utf8))
        #expect(old.updates == UpdateSettings())

        let partial = try JSONDecoder().decode(UpdateSettings.self, from: Data(#"{"skippedCount":-4}"#.utf8))
        #expect(partial.skippedCount == 0)
    }

    @Test func availabilityNeedsAnInstalledSignedBuild() {
        let feed = "https://example.com/appcast.xml"
        #expect(UpdateAvailability.determine(isAppBundle: true, bundleIdentifier: "app.overink.mac", feedURL: feed, publicKey: "abc") == .available)
        #expect(UpdateAvailability.determine(isAppBundle: false, bundleIdentifier: nil, feedURL: feed, publicKey: "abc") != .available)
        #expect(UpdateAvailability.determine(isAppBundle: true, bundleIdentifier: "app.overink.mac.dev", feedURL: feed, publicKey: "abc") != .available)
        #expect(UpdateAvailability.determine(isAppBundle: true, bundleIdentifier: "app.overink.mac", feedURL: nil, publicKey: "abc") != .available)
        #expect(UpdateAvailability.determine(isAppBundle: true, bundleIdentifier: "app.overink.mac", feedURL: "http://example.com/a.xml", publicKey: "abc") != .available)
        #expect(UpdateAvailability.determine(isAppBundle: true, bundleIdentifier: "app.overink.mac", feedURL: feed, publicKey: " ") != .available)
    }
}
