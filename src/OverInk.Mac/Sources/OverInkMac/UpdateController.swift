import AppKit
import OverInkCore
import Sparkle

/// In-app updates with Sparkle 2. The feed URL and the EdDSA public key come from the app's Info.plist
/// (the packaging script stamps them in), so a development build or `swift run` has no updater and says so.
///
/// Behaviour matches the Windows app: automatic checks at launch and daily, a beta switch, a "Check now"
/// action, and a "Not now" that stops reminding after three times for the same release.
@MainActor
final class UpdateController: NSObject, SPUUpdaterDelegate, @preconcurrency SPUStandardUserDriverDelegate {
    private let settings: SettingsModel
    private var controller: SPUStandardUpdaterController?
    private var observers: [() -> Void] = []

    let availability: UpdateAvailability

    init(settings: SettingsModel, bundle: Bundle = .main) {
        self.settings = settings
        availability = UpdateAvailability.determine(
            isAppBundle: bundle.bundleURL.pathExtension == "app",
            bundleIdentifier: bundle.bundleIdentifier,
            feedURL: bundle.object(forInfoDictionaryKey: "SUFeedURL") as? String,
            publicKey: bundle.object(forInfoDictionaryKey: "SUPublicEDKey") as? String
        )
        super.init()
    }

    /// The app's marketing version, or nil when running outside a bundle (`swift run`).
    var currentVersion: String? {
        guard Bundle.main.bundleURL.pathExtension == "app" else { return nil }
        return Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String
    }

    /// Starts Sparkle's scheduler and does a quiet check shortly after launch (like Windows).
    func start() {
        guard case .available = availability, controller == nil else { return }
        let controller = SPUStandardUpdaterController(
            startingUpdater: false,
            updaterDelegate: self,
            userDriverDelegate: self
        )
        self.controller = controller
        controller.startUpdater()
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(3))
            guard let self, let updater = self.controller?.updater, updater.canCheckForUpdates else { return }
            updater.checkForUpdatesInBackground()
        }
    }

    /// "Check now": always shows a result, even when there is nothing new. Without an updater it explains why.
    func checkNow() {
        guard let controller else {
            if case .unavailable(let reason) = availability {
                let alert = NSAlert()
                alert.messageText = "Updates aren't available"
                alert.informativeText = reason
                alert.runModal()
            }
            return
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
        controller.checkForUpdates(nil)
    }

    var canCheckNow: Bool {
        guard case .available = availability else { return true } // The button explains why it can't.
        return controller?.updater.canCheckForUpdates ?? false
    }

    var lastChecked: Date? { controller?.updater.lastUpdateCheckDate }

    /// A sentence for Settings under the Check now button.
    var statusText: String {
        switch availability {
        case .unavailable(let reason):
            return reason
        case .available:
            guard let date = lastChecked else { return "Not checked yet." }
            return "Last checked \(date.formatted(date: .abbreviated, time: .shortened))."
        }
    }

    /// Calls `observer` after a check finishes, so Settings can refresh its status line.
    func observeChecks(_ observer: @escaping () -> Void) {
        observers.append(observer)
    }

    // MARK: SPUUpdaterDelegate

    func allowedChannels(for updater: SPUUpdater) -> Set<String> {
        settings.value.updates.allowedChannels
    }

    /// Background checks stay quiet once the user has said "Not now" three times for this release.
    /// A user-started check always shows the update.
    func updater(
        _ updater: SPUUpdater,
        shouldProceedWithUpdate updateItem: SUAppcastItem,
        updateCheck: SPUUpdateCheck
    ) throws {
        guard updateCheck == .updatesInBackground,
              !settings.value.updates.shouldPromptAutomatically(for: updateItem.displayVersionString) else { return }
        throw NSError(
            domain: "app.overink.updates",
            code: 1,
            userInfo: [NSLocalizedDescriptionKey: "Update \(updateItem.displayVersionString) was dismissed three times."]
        )
    }

    func updater(
        _ updater: SPUUpdater,
        userDidMake choice: SPUUserUpdateChoice,
        forUpdate updateItem: SUAppcastItem,
        state: SPUUserUpdateState
    ) {
        guard choice == .dismiss else { return }
        // A downloaded update that is already installing doesn't count as "Not now".
        guard state.stage != .installing else { return }
        let version = updateItem.displayVersionString
        settings.update { $0.updates.recordDismissal(of: version) }
    }

    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: (any Error)?) {
        observers.forEach { $0() }
    }

    // MARK: SPUStandardUserDriverDelegate

    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// OverInk is a menu-bar app, so it has no frontmost window for Sparkle to put its dialog behind.
    /// Bring the app forward when Sparkle shows an update.
    func standardUserDriverWillHandleShowingUpdate(
        _ handleShowingUpdate: Bool,
        forUpdate update: SUAppcastItem,
        state: SPUUserUpdateState
    ) {
        if handleShowingUpdate {
            NSApplication.shared.activate(ignoringOtherApps: true)
        }
    }
}
