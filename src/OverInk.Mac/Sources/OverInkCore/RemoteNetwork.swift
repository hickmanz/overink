import Foundation

/// Which peers may talk to the remote server, and which of this Mac's addresses an iPad can reach.
public enum RemoteNetwork {
    /// Loopback, link-local and private ranges only: 127/8, ::1, 10/8, 172.16/12, 192.168/16, 169.254/16,
    /// fc00::/7 and fe80::/10. IPv4-mapped IPv6 addresses are judged by their IPv4 part.
    public static func isPrivateOrLocal(bytes: [UInt8]) -> Bool {
        switch bytes.count {
        case 4:
            return isPrivateOrLocalIPv4(bytes)
        case 16:
            if bytes.dropLast().allSatisfy({ $0 == 0 }), bytes[15] == 1 {
                return true
            }
            if bytes[0 ..< 10].allSatisfy({ $0 == 0 }), bytes[10] == 0xFF, bytes[11] == 0xFF {
                return isPrivateOrLocalIPv4(Array(bytes[12 ..< 16]))
            }
            if bytes[0] & 0xFE == 0xFC { return true }
            if bytes[0] == 0xFE, bytes[1] & 0xC0 == 0x80 { return true }
            return false
        default:
            return false
        }
    }

    public static func isPrivateOrLocal(_ address: String) -> Bool {
        guard let bytes = parse(address) else { return false }
        return isPrivateOrLocal(bytes: bytes)
    }

    /// Whether a connection from `address` is accepted. An unknown peer address is rejected: unlike the
    /// Windows host, the listener here can always see the remote endpoint, so there is no reason to guess.
    public static func isAllowedRemote(_ address: String?) -> Bool {
        guard let address else { return false }
        return isPrivateOrLocal(address)
    }

    public static func isLoopback(_ address: String) -> Bool {
        guard let bytes = parse(address) else { return false }
        if bytes.count == 4 { return bytes[0] == 127 }
        if bytes.count == 16 {
            if bytes.dropLast().allSatisfy({ $0 == 0 }), bytes[15] == 1 { return true }
            if bytes[0 ..< 10].allSatisfy({ $0 == 0 }), bytes[10] == 0xFF, bytes[11] == 0xFF { return bytes[12] == 127 }
        }
        return false
    }

    /// Parses an IPv4/IPv6 literal (an interface scope such as `%en0` is ignored).
    public static func parse(_ address: String) -> [UInt8]? {
        let literal = address.split(separator: "%", maxSplits: 1).first.map(String.init) ?? address
        var v4 = in_addr()
        if inet_pton(AF_INET, literal, &v4) == 1 {
            return withUnsafeBytes(of: &v4) { Array($0) }
        }
        var v6 = in6_addr()
        if inet_pton(AF_INET6, literal, &v6) == 1 {
            return withUnsafeBytes(of: &v6) { Array($0) }
        }
        return nil
    }

    private static func isPrivateOrLocalIPv4(_ b: [UInt8]) -> Bool {
        if b[0] == 127 || b[0] == 10 { return true }
        if b[0] == 172, (16 ... 31).contains(b[1]) { return true }
        if b[0] == 192, b[1] == 168 { return true }
        if b[0] == 169, b[1] == 254 { return true }
        return false
    }
}

public enum LanAdapterKind: Int, Codable, Comparable, Sendable {
    case wired, wifi, vpn, virtual, other

    public static func < (lhs: LanAdapterKind, rhs: LanAdapterKind) -> Bool { lhs.rawValue < rhs.rawValue }

    /// What the system reports for the interface's hardware, when it knows.
    public enum Hardware: Sendable {
        case wifi, ethernet, other
    }

    /// Classifies a macOS interface (`en0`, `utun3`, `bridge100`, ...).
    public static func classify(bsdName: String, hardware: Hardware) -> LanAdapterKind {
        let name = bsdName.lowercased()
        let vpn = ["utun", "ipsec", "ppp", "gpd", "tun", "tap", "wg"]
        if vpn.contains(where: { name.hasPrefix($0) }) { return .vpn }
        let virtual = ["bridge", "vmnet", "vnic", "veth", "docker", "awdl", "llw", "anpi", "ap", "lo", "gif", "stf"]
        if virtual.contains(where: { name.hasPrefix($0) }) { return .virtual }
        switch hardware {
        case .wifi: return .wifi
        case .ethernet: return .wired
        case .other: return .other
        }
    }
}

/// One IPv4 address on one network interface, with enough context to tell whether an iPad could use it.
public struct LanAdapter: Equatable, Identifiable, Sendable {
    /// The BSD interface name (`en0`); stable enough to remember as the user's choice.
    public var id: String
    public var name: String
    public var address: String
    public var kind: LanAdapterKind
    public var isUp: Bool
    /// True for the interface that carries the default route.
    public var hasGateway: Bool

    public init(id: String, name: String, address: String, kind: LanAdapterKind, isUp: Bool, hasGateway: Bool) {
        self.id = id
        self.name = name
        self.address = address
        self.kind = kind
        self.isUp = isUp
        self.hasGateway = hasGateway
    }

    public var isLinkLocal: Bool {
        guard let bytes = RemoteNetwork.parse(address), bytes.count == 4 else { return false }
        return bytes[0] == 169 && bytes[1] == 254
    }

    public var isPrivate: Bool { RemoteNetwork.isPrivateOrLocal(address) }

    /// True when a device on the same home or office network could plausibly connect to this address.
    public var canReachLan: Bool {
        isUp && !isLinkLocal && isPrivate && (kind == .wired || kind == .wifi || kind == .other)
    }

    /// Why this address is a poor choice for an iPad, or `nil` when it looks fine.
    public var warning: String? {
        if !isUp { return "Not connected." }
        if isLinkLocal { return "No network address yet (not connected to a network)." }
        if kind == .vpn { return "VPN interface. An iPad on your Wi-Fi usually can't reach this address." }
        if kind == .virtual { return "Virtual interface (VM, bridge, AirDrop). An iPad can't reach this address." }
        if !isPrivate {
            return "Not a private network address. OverInk only accepts devices on a private (home or office) network."
        }
        return nil
    }
}

public enum LanAdapters {
    /// Best first. By default only connected, non-virtual interfaces with a real address are kept;
    /// `includeAll` also keeps disconnected, virtual and link-local ones.
    public static func sorted(_ adapters: [LanAdapter], includeAll: Bool) -> [LanAdapter] {
        adapters
            .filter { includeAll || ($0.isUp && !$0.isLinkLocal && $0.kind != .virtual) }
            .enumerated()
            .sorted { lhs, rhs in
                let (a, b) = (lhs.element, rhs.element)
                if rank(a) != rank(b) { return rank(a) < rank(b) }
                if a.kind != b.kind { return a.kind < b.kind }
                let order = a.name.caseInsensitiveCompare(b.name)
                if order != .orderedSame { return order == .orderedAscending }
                return lhs.offset < rhs.offset
            }
            .map(\.element)
    }

    /// The remembered interface if it is still connected, otherwise the best LAN-capable one.
    public static func choose(from adapters: [LanAdapter], preferredId: String?) -> LanAdapter? {
        if let preferredId, !preferredId.isEmpty,
           let preferred = adapters.first(where: { $0.id == preferredId && $0.isUp && !$0.isLinkLocal }) {
            return preferred
        }
        return adapters.first(where: \.canReachLan)
            ?? adapters.first(where: { $0.isUp && !$0.isLinkLocal && $0.kind != .virtual })
    }

    private static func rank(_ a: LanAdapter) -> Int {
        if a.canReachLan { return a.hasGateway ? 0 : 1 }
        if a.isUp && !a.isLinkLocal { return a.kind == .vpn ? 2 : 3 }
        return 4
    }
}

/// What the QR code and the pairing window show.
public enum RemotePairingURL {
    /// `http://<address>:<port>/?join=<session token>&pair=<one-time token>`
    public static func make(host: String, port: Int, joinToken: String, pairToken: String?) -> String {
        let hostPart = host.contains(":") && !host.hasPrefix("[") ? "[\(host)]" : host
        var query = "join=\(joinToken)"
        if let pairToken { query += "&pair=\(pairToken)" }
        return "http://\(hostPart):\(port)/?\(query)"
    }
}
