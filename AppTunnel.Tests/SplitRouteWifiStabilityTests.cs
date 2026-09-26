using System.Net;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class WindowsConnectivityGuardTests
{
    [Theory]
    [InlineData("www.msftconnecttest.com", true)]
    [InlineData("msftconnecttest.com", true)]
    [InlineData("dns.msftncsi.com", true)]
    [InlineData("ipv6.msftconnecttest.com", true)]
    [InlineData("WWW.MSFTNCSI.COM.", true)]
    [InlineData("not-msftconnecttest.com", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProbeHosts_MatchWindowsNcsiNamesOnly(string? host, bool expected)
        => Assert.Equal(expected, WindowsConnectivityGuard.IsProbeHost(host));

    [Fact]
    public void ProbeIps_AreThePublishedNcsiAddresses()
    {
        Assert.True(WindowsConnectivityGuard.IsProbeIpv4(ToNbo("13.107.4.52")));
        Assert.True(WindowsConnectivityGuard.IsProbeIpv4(ToNbo("131.107.255.255")));
        Assert.False(WindowsConnectivityGuard.IsProbeIpv4(ToNbo("8.8.8.8")));
        Assert.False(WindowsConnectivityGuard.IsProbeIpv4(ToNbo("1.1.1.1")));
    }

    private static uint ToNbo(string ip)
        => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
}

public class SplitRouteFastPathTests
{
    [Fact]
    public void SplitMode_ProbeIp_StaysOnPhysicalNicAndDropsHostRoute()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: false,
            probeIp: true,
            excluded: false,
            included: true,
            blockedProc: false,
            processIsTarget: true,
            ipRefCount: 3);

        Assert.False(decision.SendViaVpn);
        Assert.True(decision.RemoveRouteNow);
        Assert.False(decision.ScheduleDelayedRemovalIfIdle);
    }

    [Fact]
    public void FullRoute_ProbeIp_StillUsesTheTunnel()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: true,
            probeIp: true,
            excluded: false,
            included: false,
            blockedProc: false,
            processIsTarget: false,
            ipRefCount: 0);

        Assert.True(decision.SendViaVpn);
        Assert.False(decision.RemoveRouteNow);
    }

    [Fact]
    public void SharedCdnIp_NonTargetPacket_DoesNotTouchTheRoute()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: false,
            probeIp: false,
            excluded: false,
            included: false,
            blockedProc: false,
            processIsTarget: false,
            ipRefCount: 2);

        Assert.False(decision.SendViaVpn);
        Assert.False(decision.RemoveRouteNow);
        Assert.False(decision.ScheduleDelayedRemovalIfIdle);
    }

    [Fact]
    public void StaleRoute_SchedulesOneDelayedRemoval()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: false,
            probeIp: false,
            excluded: false,
            included: false,
            blockedProc: false,
            processIsTarget: false,
            ipRefCount: 0);

        Assert.False(decision.SendViaVpn);
        Assert.False(decision.RemoveRouteNow);
        Assert.True(decision.ScheduleDelayedRemovalIfIdle);
    }

    [Fact]
    public void TargetProcess_StillGoesThroughTheVpn()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: false,
            probeIp: false,
            excluded: false,
            included: false,
            blockedProc: false,
            processIsTarget: true,
            ipRefCount: 1);

        Assert.True(decision.SendViaVpn);
        Assert.False(decision.RemoveRouteNow);
        Assert.False(decision.ScheduleDelayedRemovalIfIdle);
    }

    [Fact]
    public void ExcludedDestination_IsRemovedEvenInFullRoute()
    {
        var decision = SplitRouteFastPath.Decide(
            fullRoute: true,
            probeIp: false,
            excluded: true,
            included: false,
            blockedProc: false,
            processIsTarget: true,
            ipRefCount: 4);

        Assert.False(decision.SendViaVpn);
        Assert.True(decision.RemoveRouteNow);
    }
}
