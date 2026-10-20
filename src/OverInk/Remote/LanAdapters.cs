using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OverInk.Remote;

public enum LanAdapterKind
{
    Wired,
    WiFi,
    Vpn,
    Virtual,
    Other
}

/// <summary>One IPv4 address on one network adapter, with enough context to tell whether an iPad could use it.</summary>
public sealed record LanAdapter(
    string Id,
    string Name,
    string Description,
    IPAddress Address,
    LanAdapterKind Kind,
    bool IsUp,
    bool HasGateway)
{
    public bool IsLinkLocal => Address.AddressFamily == AddressFamily.InterNetwork &&
                               Address.GetAddressBytes() is [169, 254, ..];

    public bool IsPrivate => RemoteNetworkHelper.IsPrivateOrLocalAddress(Address);

    /// <summary>True when a device on the same home or office network could plausibly connect to this address.</summary>
    public bool CanReachLan =>
        IsUp && !IsLinkLocal && IsPrivate &&
        Kind is LanAdapterKind.Wired or LanAdapterKind.WiFi or LanAdapterKind.Other;

    /// <summary>Why this address is a poor choice for an iPad, or null when it looks fine.</summary>
    public string? Warning =>
        !IsUp ? "Not connected." :
        IsLinkLocal ? "No network address yet (not connected to a network)." :
        Kind == LanAdapterKind.Vpn ? "VPN adapter. An iPad on your Wi-Fi usually can't reach this address." :
        Kind == LanAdapterKind.Virtual ? "Virtual adapter (WSL, Hyper-V, VM). An iPad can't reach this address." :
        !IsPrivate ? "Not a private network address. OverInk only accepts devices on a private (home or office) network." :
        null;
}

public static class LanAdapters
{
    private static readonly string[] VpnMarkers =
    [
        "vpn", "pangp", "globalprotect", "openvpn", "tap-windows", "wireguard", "wintun", "tailscale",
        "zerotier", "nordlynx", "anyconnect", "fortinet", "forticlient", "sonicwall", "pulse secure",
        "mullvad", "proton", "wan miniport"
    ];

    private static readonly string[] VirtualMarkers =
    [
        "virtual", "hyper-v", "vethernet", "wsl", "vmware", "vmnet", "virtualbox", "vbox", "docker",
        "loopback", "bluetooth", "miniport"
    ];

    public static LanAdapterKind Classify(NetworkInterfaceType type, string name, string description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (VpnMarkers.Any(text.Contains))
        {
            return LanAdapterKind.Vpn;
        }

        if (VirtualMarkers.Any(text.Contains))
        {
            return LanAdapterKind.Virtual;
        }

        return type switch
        {
            NetworkInterfaceType.Wireless80211 => LanAdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or
                NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetT or
                NetworkInterfaceType.FastEthernetFx => LanAdapterKind.Wired,
            NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => LanAdapterKind.Vpn,
            _ => LanAdapterKind.Other
        };
    }

    /// <summary>
    /// Lists IPv4 addresses on this PC, best first. By default only connected, non-virtual adapters with a real
    /// address are returned; <paramref name="includeAll"/> also returns disconnected, virtual and link-local ones.
    /// </summary>
    public static IReadOnlyList<LanAdapter> GetAdapters(bool includeAll)
    {
        var list = new List<LanAdapter>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return list;
        }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g =>
                !IPAddress.Any.Equals(g.Address) && !IPAddress.IPv6Any.Equals(g.Address));
            var kind = Classify(nic.NetworkInterfaceType, nic.Name, nic.Description);
            var up = nic.OperationalStatus == OperationalStatus.Up;

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.Any.Equals(uni.Address))
                {
                    continue;
                }

                var adapter = new LanAdapter(nic.Id, nic.Name, nic.Description, uni.Address, kind, up, hasGateway);
                if (!includeAll && (!up || adapter.IsLinkLocal || kind == LanAdapterKind.Virtual))
                {
                    continue;
                }

                list.Add(adapter);
            }
        }

        return list
            .OrderBy(Rank)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Picks the remembered adapter if it is still connected, otherwise the best LAN-capable one.</summary>
    public static LanAdapter? Choose(IReadOnlyList<LanAdapter> adapters, string? preferredId)
    {
        if (!string.IsNullOrEmpty(preferredId))
        {
            var preferred = adapters.FirstOrDefault(a => a.Id == preferredId && a.IsUp && !a.IsLinkLocal);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return adapters.FirstOrDefault(a => a.CanReachLan) ??
               adapters.FirstOrDefault(a => a.IsUp && !a.IsLinkLocal && a.Kind != LanAdapterKind.Virtual);
    }

    private static int Rank(LanAdapter a)
    {
        if (a.CanReachLan)
        {
            return a.HasGateway ? 0 : 1;
        }

        if (a.IsUp && !a.IsLinkLocal)
        {
            return a.Kind == LanAdapterKind.Vpn ? 2 : 3;
        }

        return 4;
    }
}
