import AppKit
import ServiceManagement

/// Launch at login through `SMAppService`. This only works from an installed app bundle.
@MainActor
enum LoginItem {
    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    /// The user must approve the item in System Settings → General → Login Items.
    static var needsApproval: Bool {
        SMAppService.mainApp.status == .requiresApproval
    }

    static func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }

    static func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

/// Keeps one copy of OverInk running. A second launch asks the first to show itself, then quits.
@MainActor
enum SingleInstance {
    static let showRequested = Notification.Name("app.overink.mac.showRequested")

    /// Other running copies of this app (by bundle id, or by executable name when run unbundled).
    /// Unbundled processes have no bundle id, so they are matched by executable name.
    static func otherInstances() -> [NSRunningApplication] {
        let me = ProcessInfo.processInfo.processIdentifier
        if let identifier = Bundle.main.bundleIdentifier {
            return NSRunningApplication.runningApplications(withBundleIdentifier: identifier)
                .filter { $0.processIdentifier != me }
        }
        // Unbundled (swift run): only other unbundled copies count, never an installed app.
        let name = Bundle.main.executableURL?.lastPathComponent ?? ProcessInfo.processInfo.processName
        return NSWorkspace.shared.runningApplications.filter {
            $0.processIdentifier != me && $0.bundleIdentifier == nil && $0.executableURL?.lastPathComponent == name
        }
    }

    /// Returns true when another copy is already running (and has been asked to show itself).
    static func handOffToExistingInstance() -> Bool {
        guard !otherInstances().isEmpty else { return false }
        DistributedNotificationCenter.default().postNotificationName(
            showRequested, object: nil, userInfo: nil, deliverImmediately: true
        )
        return true
    }

    static func observeShowRequests(_ handler: @escaping @MainActor () -> Void) {
        DistributedNotificationCenter.default().addObserver(
            forName: showRequested, object: nil, queue: .main
        ) { _ in
            MainActor.assumeIsolated { handler() }
        }
    }
}

enum ScreenPermission {
    static var isGranted: Bool {
        CGPreflightScreenCaptureAccess()
    }

    /// Shows the system prompt the first time; afterwards macOS only reports the current state.
    @discardableResult
    static func request() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    @MainActor
    static func openSystemSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }
}
