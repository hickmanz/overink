import Darwin
import Foundation
import OverInkCore
import SystemConfiguration

/// The Mac's IPv4 interfaces, for choosing which address the pairing QR code advertises.
enum SystemLanAdapters {
    static func current() -> [LanAdapter] {
        var hardware: [String: (kind: LanAdapterKind.Hardware, name: String)] = [:]
        for interface in (SCNetworkInterfaceCopyAll() as? [SCNetworkInterface]) ?? [] {
            guard let bsd = SCNetworkInterfaceGetBSDName(interface) as String? else { continue }
            let type = SCNetworkInterfaceGetInterfaceType(interface) as String?
            let kind: LanAdapterKind.Hardware
            if type == kSCNetworkInterfaceTypeIEEE80211 as String {
                kind = .wifi
            } else if type == kSCNetworkInterfaceTypeEthernet as String {
                kind = .ethernet
            } else {
                kind = .other
            }
            let name = (SCNetworkInterfaceGetLocalizedDisplayName(interface) as String?) ?? bsd
            hardware[bsd] = (kind, name)
        }

        let primary = primaryInterface()
        var result: [LanAdapter] = []
        var cursor: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&cursor) == 0, let first = cursor else { return [] }
        defer { freeifaddrs(cursor) }

        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let entry = pointer.pointee
            guard let address = entry.ifa_addr, address.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = Int32(entry.ifa_flags)
            guard flags & IFF_LOOPBACK == 0 else { continue }

            var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            var sin = UnsafeRawPointer(address).load(as: sockaddr_in.self).sin_addr
            guard inet_ntop(AF_INET, &sin, &buffer, socklen_t(INET_ADDRSTRLEN)) != nil else { continue }

            let bsd = String(cString: entry.ifa_name)
            let known = hardware[bsd]
            result.append(LanAdapter(
                id: bsd,
                name: known?.name ?? bsd,
                address: String(cString: buffer),
                kind: LanAdapterKind.classify(bsdName: bsd, hardware: known?.kind ?? .other),
                isUp: flags & IFF_UP != 0 && flags & IFF_RUNNING != 0,
                hasGateway: bsd == primary
            ))
        }
        return result
    }

    /// The interface that carries the default route.
    private static func primaryInterface() -> String? {
        guard let store = SCDynamicStoreCreate(nil, "OverInk" as CFString, nil, nil),
              let global = SCDynamicStoreCopyValue(store, "State:/Network/Global/IPv4" as CFString) as? [String: Any] else {
            return nil
        }
        return global["PrimaryInterface"] as? String
    }
}
