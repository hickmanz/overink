import Foundation
import Testing
@testable import OverInkCore

@Suite struct RemoteNetworkTests {
    @Test(arguments: [
        "127.0.0.1", "10.0.0.5", "172.16.0.1", "172.31.255.255", "192.168.1.20", "169.254.3.4",
        "::1", "fc00::1", "fd12:3456::9", "fe80::1", "::ffff:192.168.1.5", "fe80::1%en0"
    ])
    func allowsPrivateAndLocal(address: String) {
        #expect(RemoteNetwork.isPrivateOrLocal(address))
        #expect(RemoteNetwork.isAllowedRemote(address))
    }

    @Test(arguments: [
        "8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.0.1", "100.64.0.1",
        "2001:4860:4860::8888", "::ffff:8.8.8.8", "not an address", ""
    ])
    func rejectsPublicAndGarbage(address: String) {
        #expect(!RemoteNetwork.isPrivateOrLocal(address))
        #expect(!RemoteNetwork.isAllowedRemote(address))
    }

    @Test func unknownPeerIsRejected() {
        #expect(!RemoteNetwork.isAllowedRemote(nil))
    }

    @Test func loopbackDetection() {
        #expect(RemoteNetwork.isLoopback("127.0.0.1"))
        #expect(RemoteNetwork.isLoopback("127.8.8.8"))
        #expect(RemoteNetwork.isLoopback("::1"))
        #expect(RemoteNetwork.isLoopback("::ffff:127.0.0.1"))
        #expect(!RemoteNetwork.isLoopback("192.168.1.1"))
        #expect(!RemoteNetwork.isLoopback("garbage"))
    }

    // MARK: Adapters

    private func adapter(
        _ id: String,
        _ address: String,
        kind: LanAdapterKind = .wifi,
        up: Bool = true,
        gateway: Bool = false
    ) -> LanAdapter {
        LanAdapter(id: id, name: id, address: address, kind: kind, isUp: up, hasGateway: gateway)
    }

    @Test func classifiesInterfacesByName() {
        #expect(LanAdapterKind.classify(bsdName: "utun3", hardware: .other) == .vpn)
        #expect(LanAdapterKind.classify(bsdName: "bridge100", hardware: .ethernet) == .virtual)
        #expect(LanAdapterKind.classify(bsdName: "awdl0", hardware: .wifi) == .virtual)
        #expect(LanAdapterKind.classify(bsdName: "en0", hardware: .wifi) == .wifi)
        #expect(LanAdapterKind.classify(bsdName: "en5", hardware: .ethernet) == .wired)
        #expect(LanAdapterKind.classify(bsdName: "en9", hardware: .other) == .other)
    }

    @Test func reachabilityAndWarnings() {
        #expect(adapter("en0", "192.168.1.5").canReachLan)
        #expect(adapter("en0", "192.168.1.5").warning == nil)
        #expect(!adapter("en0", "169.254.1.1").canReachLan)
        #expect(adapter("en0", "169.254.1.1").warning?.contains("No network address") == true)
        #expect(adapter("utun2", "10.8.0.2", kind: .vpn).warning?.contains("VPN") == true)
        #expect(adapter("bridge0", "192.168.64.1", kind: .virtual).warning?.contains("Virtual") == true)
        #expect(adapter("en0", "192.168.1.5", up: false).warning == "Not connected.")
        #expect(adapter("en0", "8.8.8.8").warning?.contains("private") == true)
    }

    @Test func defaultFilteringDropsDownVirtualAndLinkLocal() {
        let all = [
            adapter("en0", "192.168.1.5", gateway: true),
            adapter("en1", "10.0.0.2", up: false),
            adapter("bridge0", "192.168.64.1", kind: .virtual),
            adapter("en2", "169.254.9.9")
        ]
        #expect(LanAdapters.sorted(all, includeAll: false).map(\.id) == ["en0"])
        #expect(LanAdapters.sorted(all, includeAll: true).count == 4)
    }

    @Test func sortingPrefersGatewayThenLanThenVpn() {
        let all = [
            adapter("utun1", "10.8.0.2", kind: .vpn, gateway: true),
            adapter("en5", "192.168.2.2", kind: .wired),
            adapter("en0", "192.168.1.5", gateway: true)
        ]
        #expect(LanAdapters.sorted(all, includeAll: false).map(\.id) == ["en0", "en5", "utun1"])
    }

    @Test func chooseHonorsPreferenceOnlyWhileConnected() {
        let all = [
            adapter("en0", "192.168.1.5", gateway: true),
            adapter("en5", "192.168.2.2", kind: .wired)
        ]
        #expect(LanAdapters.choose(from: all, preferredId: "en5")?.id == "en5")
        #expect(LanAdapters.choose(from: all, preferredId: "gone")?.id == "en0")
        #expect(LanAdapters.choose(from: all, preferredId: nil)?.id == "en0")

        let down = [adapter("en5", "192.168.2.2", kind: .wired, up: false), adapter("en0", "192.168.1.5")]
        #expect(LanAdapters.choose(from: down, preferredId: "en5")?.id == "en0")
        #expect(LanAdapters.choose(from: [], preferredId: nil) == nil)
    }

    @Test func pairingURLShape() {
        #expect(RemotePairingURL.make(host: "192.168.1.5", port: 8787, joinToken: "J", pairToken: "P")
            == "http://192.168.1.5:8787/?join=J&pair=P")
        #expect(RemotePairingURL.make(host: "192.168.1.5", port: 8787, joinToken: "J", pairToken: nil)
            == "http://192.168.1.5:8787/?join=J")
        #expect(RemotePairingURL.make(host: "fd00::1", port: 80, joinToken: "J", pairToken: nil)
            == "http://[fd00::1]:80/?join=J")
    }
}
