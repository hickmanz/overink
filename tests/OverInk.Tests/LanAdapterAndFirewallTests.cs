using System.Net;
using System.Net.NetworkInformation;
using OverInk.Remote;
using Xunit;

namespace OverInk.Tests;

public class LanAdapterAndFirewallTests
{
    private const string Exe = @"C:\Apps\OverInk\OverInk.exe";

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek Gaming 2.5GbE Family Controller", LanAdapterKind.Wired)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Realtek 8852CE WiFi 6E PCI-E NIC", LanAdapterKind.WiFi)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 2", "PANGP Virtual Ethernet Adapter Secure", LanAdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "Tailscale", "Tailscale Tunnel", LanAdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "Local Area Connection 4", "TAP-Windows Adapter V9 for OpenVPN Connect", LanAdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL (Hyper-V firewall))", "Hyper-V Virtual Ethernet Adapter", LanAdapterKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", LanAdapterKind.Virtual)]
    public void Classify_recognises_adapter_kinds(NetworkInterfaceType type, string name, string description, LanAdapterKind expected)
    {
        Assert.Equal(expected, LanAdapters.Classify(type, name, description));
    }

    private static LanAdapter Adapter(string id, string ip, LanAdapterKind kind, bool up = true, bool gateway = true) =>
        new(id, id, id, IPAddress.Parse(ip), kind, up, gateway);

    [Fact]
    public void Choose_prefers_lan_adapter_over_vpn()
    {
        var vpn = Adapter("vpn", "10.140.100.142", LanAdapterKind.Vpn, gateway: false);
        var eth = Adapter("eth", "192.168.0.172", LanAdapterKind.Wired);

        Assert.Equal(eth, LanAdapters.Choose([vpn, eth], null));
    }

    [Fact]
    public void Choose_honours_remembered_adapter_while_it_is_connected()
    {
        var wifi = Adapter("wifi", "192.168.1.20", LanAdapterKind.WiFi);
        var eth = Adapter("eth", "192.168.0.172", LanAdapterKind.Wired);

        Assert.Equal(wifi, LanAdapters.Choose([eth, wifi], "wifi"));
        Assert.Equal(eth, LanAdapters.Choose([eth], "wifi"));
    }

    [Fact]
    public void Choose_returns_null_when_nothing_is_connected()
    {
        var down = Adapter("wifi", "169.254.1.1", LanAdapterKind.WiFi, up: false);

        Assert.Null(LanAdapters.Choose([down], null));
        Assert.Null(LanAdapters.Choose([], null));
    }

    [Fact]
    public void Adapter_warnings_explain_poor_choices()
    {
        Assert.Null(Adapter("eth", "192.168.0.172", LanAdapterKind.Wired).Warning);
        Assert.Contains("VPN", Adapter("vpn", "10.1.1.1", LanAdapterKind.Vpn).Warning);
        Assert.Contains("Virtual", Adapter("v", "172.26.32.1", LanAdapterKind.Virtual).Warning);
        Assert.Contains("Not connected", Adapter("d", "192.168.0.5", LanAdapterKind.Wired, up: false).Warning);
        Assert.Contains("No network address", Adapter("l", "169.254.9.9", LanAdapterKind.Wired).Warning);
    }

    [Theory]
    [InlineData("8787", 8787, true)]
    [InlineData("80,8787", 8787, true)]
    [InlineData("8000-9000", 8787, true)]
    [InlineData("80,443", 8787, false)]
    [InlineData("*", 8787, false)]
    [InlineData("RPC", 8787, false)]
    [InlineData(null, 8787, false)]
    public void PortListContains_parses_firewall_port_lists(string? ports, int port, bool expected)
    {
        Assert.Equal(expected, FirewallHelper.PortListContains(ports, port));
    }

    private static FirewallRuleInfo Rule(bool allow = true, string? app = null, string? ports = null, int protocol = 6,
        int profiles = 7, bool enabled = true, bool inbound = true, string? service = null) =>
        new("rule", enabled, inbound, allow, app, service, ports, protocol, profiles);

    [Fact]
    public void Evaluate_allows_when_program_rule_exists()
    {
        var result = FirewallHelper.Evaluate([Rule(app: Exe, ports: "*")], 8787, Exe, activeProfiles: 2);
        Assert.Equal(FirewallState.Open, result.State);
    }

    [Fact]
    public void Evaluate_allows_when_port_rule_exists_without_program()
    {
        var result = FirewallHelper.Evaluate([Rule(ports: "8787", protocol: 256)], 8787, Exe, activeProfiles: 2);
        Assert.Equal(FirewallState.Open, result.State);
    }

    [Fact]
    public void Evaluate_reports_block_rule_even_if_allow_rule_exists()
    {
        var rules = new[] { Rule(app: Exe, ports: "*"), Rule(allow: false, app: Exe, ports: "*") };
        var result = FirewallHelper.Evaluate(rules, 8787, Exe, activeProfiles: 2);
        Assert.Equal(FirewallState.Blocked, result.State);
    }

    [Fact]
    public void Evaluate_ignores_unrelated_disabled_outbound_and_other_profile_rules()
    {
        var rules = new[]
        {
            Rule(app: @"C:\Other\Other.exe", ports: "*"),
            Rule(app: Exe, ports: "*", enabled: false),
            Rule(app: Exe, ports: "*", inbound: false),
            Rule(app: Exe, ports: "*", profiles: 4),
            Rule(ports: "*"),
            Rule(ports: "8787", service: "SomeService"),
            Rule(ports: "8787", protocol: 17)
        };

        var result = FirewallHelper.Evaluate(rules, 8787, Exe, activeProfiles: 2);
        Assert.Equal(FirewallState.NoRule, result.State);
    }
}
