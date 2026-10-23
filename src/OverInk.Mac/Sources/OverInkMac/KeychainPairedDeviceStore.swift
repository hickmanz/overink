import Foundation
import OverInkCore
import Security

/// Paired devices (name, key hash, last seen), kept in the login Keychain.
///
/// A build without a Team ID (a development build) must not touch the Keychain: every rebuild changes the
/// app's identity, so macOS stops the app with a password prompt on the main thread. Those builds, and any
/// Keychain failure, use a private file in Application Support instead, so pairing still works. Only hashes
/// of device keys are ever stored, so the file holds nothing that can be replayed.
final class KeychainPairedDeviceStore: PairedDeviceStore, @unchecked Sendable {
    private let service: String
    private let account = "paired-devices"
    private let fallbackURL: URL
    private let lock = NSLock()
    private let keychainAllowed: Bool

    /// Whether this process is signed with a Team ID (Developer ID or App Store builds are).
    static let isTeamSigned: Bool = {
        var code: SecCode?
        var staticCode: SecStaticCode?
        var info: CFDictionary?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
              SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
              SecCodeCopySigningInformation(
                  staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info
              ) == errSecSuccess,
              let dictionary = info as? [String: Any] else {
            return false
        }
        return dictionary[kSecCodeInfoTeamIdentifier as String] is String
    }()

    init(
        service: String = "app.overink.mac.remote",
        fallbackURL: URL = MacSettingsStore.defaultFileURL
            .deletingLastPathComponent().appendingPathComponent("paired-devices.json"),
        useKeychain: Bool = KeychainPairedDeviceStore.isTeamSigned
    ) {
        keychainAllowed = useKeychain
        self.service = service
        self.fallbackURL = fallbackURL
    }

    func load() -> [PairedDevice] {
        lock.withLock {
            if let data = readKeychain() ?? readFallback() {
                return Self.decode(data)
            }
            return []
        }
    }

    func save(_ devices: [PairedDevice]) {
        lock.withLock {
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            guard let data = try? encoder.encode(devices) else { return }
            if writeKeychain(data) {
                try? FileManager.default.removeItem(at: fallbackURL)
            } else {
                writeFallback(data)
            }
        }
    }

    // MARK: Keychain

    private var baseQuery: [CFString: Any] {
        [kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account]
    }

    private func readKeychain() -> Data? {
        guard keychainAllowed else { return nil }
        var query = baseQuery
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        // Never show a password prompt from a background read.
        query[kSecUseAuthenticationUI] = kSecUseAuthenticationUIFail
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    private func writeKeychain(_ data: Data) -> Bool {
        guard keychainAllowed else { return false }
        let update = SecItemUpdate(
            baseQuery as CFDictionary,
            [kSecValueData: data] as CFDictionary
        )
        if update == errSecSuccess { return true }
        guard update == errSecItemNotFound else { return false }
        var add = baseQuery
        add[kSecValueData] = data
        add[kSecAttrAccessible] = kSecAttrAccessibleWhenUnlocked
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    // MARK: Fallback file

    private func readFallback() -> Data? {
        try? Data(contentsOf: fallbackURL)
    }

    private func writeFallback(_ data: Data) {
        do {
            try FileManager.default.createDirectory(
                at: fallbackURL.deletingLastPathComponent(), withIntermediateDirectories: true
            )
            try data.write(to: fallbackURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fallbackURL.path)
        } catch {
            NSLog("OverInk could not save paired devices: %@", error.localizedDescription)
        }
    }

    private static func decode(_ data: Data) -> [PairedDevice] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([PairedDevice].self, from: data)) ?? []
    }
}
