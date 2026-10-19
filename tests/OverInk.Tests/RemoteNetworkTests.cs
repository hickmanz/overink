using System.Net;
using OverInk.Remote;
using Xunit;

namespace OverInk.Tests;

public class RemoteNetworkTests
{
    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivateOrLocalAddress_classifies_ipv4(string ip, bool expected)
    {
        Assert.Equal(expected, RemoteNetworkHelper.IsPrivateOrLocalAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void NormalizedToDips_maps_center()
    {
        var dips = new System.Windows.Rect(0, 0, 1000, 800);
        var p = CoordinateMapper.NormalizedToDips(0.5, 0.5, dips);
        Assert.Equal(500, p.X, 3);
        Assert.Equal(400, p.Y, 3);
    }
}
