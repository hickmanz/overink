import Foundation
import OverInkCore
import Security

/// Paired devices (name, key hash, last seen), kept in the login Keychain.
///
/// An unsigned development build can be refused Keychain access (or be asked about it after every
/// rebuild). In that case the list falls back to a private file in Application Support, so pairing still
/// works. Only hashes of device keys are ever stored, so the file holds nothing that can be replayed.
final class KeychainPairedDeviceStore: PairedDeviceStore, @unchecked Sendable {
    private let service: String
    private let account = "paired-devices"
    private let fallbackURL: URL
    private let lock = NSLock()

    init(
        service: String = "app.overink.mac.remote",
        fallbackURL: URL = MacSettingsStore.defaultFileURL
            .deletingLastPathComponent().appendingPathComponent("paired-devices.json")
    ) {
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
