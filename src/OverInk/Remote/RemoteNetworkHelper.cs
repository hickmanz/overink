using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OverInk.Remote;

public static class RemoteNetworkHelper
{
    public static bool IsAllowedRemoteAddress(IPAddress? address)
    {
        if (address is null)
        {
            // Some hosts omit RemoteIpAddress until after accept; allow and rely on LAN binding.
            return true;
        }

        return IsPrivateOrLocalAddress(address);
    }

    public static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            return false;
        }

        var v6 = address.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        if (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80)
        {
            return true;
        }

        return false;
    }

    public static string? GetPreferredLanIPv4()
    {
        var chosen = LanAdapters.Choose(LanAdapters.GetAdapters(includeAll: false), null);
        return chosen is { CanReachLan: true } ? chosen.Address.ToString() : null;
    }
}
