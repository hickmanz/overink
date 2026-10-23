import Foundation

/// Update preferences that live in `settings.json`. Sparkle keeps its own "last checked" date.
public struct UpdateSettings: Codable, Equatable, Sendable {
    /// Also offer beta builds (GitHub pre-releases such as `v0.3.0-beta.1`).
    public var includeBeta = false
    /// The version the user last chose "Not now" for. A different version starts the count over.
    public var skippedVersion = ""
    /// How many times "Not now" was chosen for `skippedVersion`.
    public var skippedCount = 0

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case includeBeta, skippedVersion, skippedCount
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        includeBeta = try c.decodeIfPresent(Bool.self, forKey: .includeBeta) ?? false
        skippedVersion = try c.decodeIfPresent(String.self, forKey: .skippedVersion) ?? ""
        skippedCount = max(0, try c.decodeIfPresent(Int.self, forKey: .skippedCount) ?? 0)
    }
}

extension UpdateSettings {
    /// "Not now" snoozes the automatic prompt for a release instead of silencing it forever: the prompt
    /// comes back on the next few checks, then stays quiet until a newer release (or a manual Check now).
    /// Matches the Windows app.
    public static let maxDismissals = 3

    /// Whether an automatic (background) check should show the update prompt for `version`.
    public func shouldPromptAutomatically(for version: String) -> Bool {
        !Self.sameVersion(skippedVersion, version) || skippedCount < Self.maxDismissals
    }

    /// Counts a "Not now" for `version`; a different version starts over at one.
    public mutating func recordDismissal(of version: String) {
        if Self.sameVersion(skippedVersion, version) {
            skippedCount += 1
        } else {
            skippedVersion = version
            skippedCount = 1
        }
    }

    /// Sparkle channels to accept besides the default (stable) one.
    public var allowedChannels: Set<String> {
        includeBeta ? [UpdateChannel.beta] : []
    }

    private static func sameVersion(_ a: String, _ b: String) -> Bool {
        let trimmed = { (s: String) in s.trimmingCharacters(in: .whitespaces).lowercased() }
        return !a.isEmpty && trimmed(a) == trimmed(b)
    }
}

public enum UpdateChannel {
    /// The `<sparkle:channel>` name that marks beta items in the appcast.
    public static let beta = "beta"
}

/// What the app may do about updates in the way it is running.
public enum UpdateAvailability: Equatable, Sendable {
    case available
    /// A reason shown in Settings instead of a working Check now button.
    case unavailable(String)

    /// Sparkle can only replace an installed app that carries a feed URL and a signing key. The decision is
    /// pure so it can be tested; the app passes in what it reads from its bundle.
    public static func determine(
        isAppBundle: Bool,
        bundleIdentifier: String?,
        feedURL: String?,
        publicKey: String?
    ) -> UpdateAvailability {
        guard isAppBundle else {
            return .unavailable("Updates work from the installed OverInk app.")
        }
        if bundleIdentifier?.hasSuffix(".dev") == true {
            return .unavailable("This is a development build, which doesn't update itself.")
        }
        guard let feedURL, URL(string: feedURL)?.scheme == "https" else {
            return .unavailable("This build has no update feed.")
        }
        guard let publicKey, !publicKey.trimmingCharacters(in: .whitespaces).isEmpty else {
            return .unavailable("This build has no update signing key.")
        }
        return .available
    }
}
