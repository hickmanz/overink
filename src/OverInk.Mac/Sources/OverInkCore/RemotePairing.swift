import CryptoKit
import Foundation

/// A device that has paired with this Mac. Only a hash of its key is kept, so a leaked store cannot be replayed.
public struct PairedDevice: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String
    /// Uppercase hex SHA-256 of the device key (same encoding as the Windows host).
    public var keyHash: String
    public var lastSeen: Date
    public var canUseLibrary: Bool

    public init(
        id: String = UUID().uuidString.lowercased(),
        name: String,
        keyHash: String,
        lastSeen: Date,
        canUseLibrary: Bool
    ) {
        self.id = id
        self.name = name
        self.keyHash = keyHash
        self.lastSeen = lastSeen
        self.canUseLibrary = canUseLibrary
    }
}

/// Where paired devices live. The Mac app backs this with the Keychain; tests use memory.
public protocol PairedDeviceStore: Sendable {
    func load() -> [PairedDevice]
    func save(_ devices: [PairedDevice])
}

public final class InMemoryPairedDeviceStore: PairedDeviceStore, @unchecked Sendable {
    private let lock = NSLock()
    private var devices: [PairedDevice]

    public init(devices: [PairedDevice] = []) {
        self.devices = devices
    }

    public func load() -> [PairedDevice] {
        lock.withLock { devices }
    }

    public func save(_ devices: [PairedDevice]) {
        lock.withLock { self.devices = devices }
    }
}

/// Pair tokens (shown once in the QR code), the session join link, and the paired-device list.
///
/// Auth order for a `hello` is the caller's: pair token, then device key, then join token. This type
/// only answers each question.
public final class PairingService: @unchecked Sendable {
    public static let pairTokenLifetime: TimeInterval = 10 * 60

    private let lock = NSLock()
    private let store: PairedDeviceStore
    private let now: @Sendable () -> Date
    private var devices: [PairedDevice]
    private var activePairToken: String?
    private var pairTokenExpires = Date.distantPast
    private var joinToken: String

    public init(store: PairedDeviceStore, now: @escaping @Sendable () -> Date = { Date() }) {
        self.store = store
        self.now = now
        devices = store.load()
        joinToken = Self.makeToken()
    }

    public var sessionJoinToken: String {
        lock.withLock { joinToken }
    }

    @discardableResult
    public func rotateSessionJoinToken() -> String {
        lock.withLock {
            joinToken = Self.makeToken()
            return joinToken
        }
    }

    /// Creates the single active pair token (replacing any earlier one).
    public func createPairToken() -> String {
        lock.withLock {
            let token = Self.makeToken()
            activePairToken = token
            pairTokenExpires = now().addingTimeInterval(Self.pairTokenLifetime)
            return token
        }
    }

    /// Consumes the token if it matches and has not expired. Tokens work once.
    public func tryConsumePairToken(_ token: String?) -> Bool {
        guard let token, !token.trimmingCharacters(in: .whitespaces).isEmpty else { return false }
        return lock.withLock {
            guard let active = activePairToken, now() <= pairTokenExpires,
                  Self.constantTimeEquals(active, token) else {
                return false
            }
            activePairToken = nil
            return true
        }
    }

    public func validateJoinToken(_ token: String?) -> Bool {
        guard let token, !token.trimmingCharacters(in: .whitespaces).isEmpty else { return false }
        return lock.withLock { Self.constantTimeEquals(joinToken, token) }
    }

    /// Registers a new device and returns its secret key (shown to the device once) with its record.
    public func issueDeviceKey(deviceName: String, canUseLibrary: Bool) -> (key: String, device: PairedDevice) {
        let key = Self.makeToken()
        let trimmed = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        let device = PairedDevice(
            name: trimmed.isEmpty ? "iPad" : String(trimmed.prefix(64)),
            keyHash: Self.hash(key),
            lastSeen: now(),
            canUseLibrary: canUseLibrary
        )
        lock.withLock {
            devices.append(device)
            store.save(devices)
        }
        return (key, device)
    }

    /// The device holding `key`, with its last-seen time refreshed.
    public func findByDeviceKey(_ key: String?) -> PairedDevice? {
        guard let key, !key.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        let hash = Self.hash(key)
        return lock.withLock {
            guard let index = devices.firstIndex(where: { Self.constantTimeEquals($0.keyHash, hash) }) else {
                return nil
            }
            devices[index].lastSeen = now()
            store.save(devices)
            return devices[index]
        }
    }

    public var pairedDevices: [PairedDevice] {
        lock.withLock { devices }
    }

    public func revoke(id: String) {
        lock.withLock {
            devices.removeAll { $0.id == id }
            store.save(devices)
        }
    }

    public func revokeAll() {
        lock.withLock {
            devices.removeAll()
            store.save(devices)
        }
    }

    public static func hash(_ key: String) -> String {
        SHA256.hash(data: Data(key.utf8)).map { String(format: "%02X", $0) }.joined()
    }

    /// 128 random bits as unpadded base64url.
    public static func makeToken() -> String {
        var generator = SystemRandomNumberGenerator()
        let bytes = (0 ..< 16).map { _ in UInt8.random(in: .min ... .max, using: &generator) }
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    private static func constantTimeEquals(_ a: String, _ b: String) -> Bool {
        let x = Array(a.utf8)
        let y = Array(b.utf8)
        guard x.count == y.count else { return false }
        var difference: UInt8 = 0
        for (l, r) in zip(x, y) { difference |= l ^ r }
        return difference == 0
    }
}

/// Picks the colour a joining participant gets: the first palette entry nobody holds.
public enum ParticipantColors {
    public static func pick(used: [String], random: () -> Int = { Int.random(in: 0 ..< Int.max) }) -> String {
        let taken = Set(used.map { $0.uppercased() })
        if let free = RemoteProtocol.participantColors.first(where: { !taken.contains($0.uppercased()) }) {
            return free
        }
        return RemoteProtocol.participantColors[random() % RemoteProtocol.participantColors.count]
    }
}
