using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class WireGuardSplitRouteOptionsTests
{
    [Theory]
    [InlineData("Error: unrecognized option: Table", true)]
    [InlineData("unknown option 'Table'", true)]
    [InlineData("Invalid option: Table", true)]
    [InlineData("unsupported key Table", true)]
    [InlineData("unknown error", false)]
    [InlineData("ERROR: Unknown interface", false)]
    [InlineData("failed to install tunnel service", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TableOffFallback_OnlyWhenTableOptionIsRejected(string? text, bool expected)
        => Assert.Equal(expected, WireGuardSplitRouteOptions.LooksLikeUnsupportedTableOption(text, ""));
}

public class VpnDefaultRouteInspectorTests
{
    [Fact]
    public void ZeroDestAndMask_IsDefaultRoute()
    {
        Assert.True(VpnDefaultRouteInspector.IsIpv4DefaultRoute(0, 0));
        Assert.False(VpnDefaultRouteInspector.IsIpv4DefaultRoute(0x08080808, 0xFFFFFFFF));
    }

    [Fact]
    public void FormatSummary_MarksVpnInterface()
    {
        var rows = new[]
        {
            new VpnDefaultRouteInspector.Ipv4RouteRow(0, 0, BitConverter.ToUInt32(System.Net.IPAddress.Parse("10.8.0.1").GetAddressBytes(), 0), 12, 1, 3),
            new VpnDefaultRouteInspector.Ipv4RouteRow(0, 0, BitConverter.ToUInt32(System.Net.IPAddress.Parse("192.168.1.1").GetAddressBytes(), 0), 8, 25, 3)
        };

        var summary = VpnDefaultRouteInspector.FormatSummary(rows, vpnIfIndex: 12);

        Assert.Contains("defaultGateways=2", summary);
        Assert.Contains("vpnIf=12", summary);
        Assert.Contains("vpnIfDefault=1", summary);
        Assert.Contains("if=12", summary);
        Assert.Contains("vpn", summary);
        Assert.Contains("phys", summary);
        Assert.Contains("10.8.0.1", summary);
        Assert.Contains("192.168.1.1", summary);
    }

    [Fact]
    public void OnInterface_FiltersByIndex()
    {
        var rows = new[]
        {
            new VpnDefaultRouteInspector.Ipv4RouteRow(0, 0, 0, 12, 1, 3),
            new VpnDefaultRouteInspector.Ipv4RouteRow(0, 0, 0, 8, 25, 3)
        };

        var vpn = VpnDefaultRouteInspector.OnInterface(rows, 12);
        Assert.Single(vpn);
        Assert.Equal(12u, vpn[0].IfIndex);
    }
}
