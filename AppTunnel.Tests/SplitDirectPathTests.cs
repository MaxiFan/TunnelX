using System.Net;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class SplitDirectPathPolicyTests
{
    [Fact]
    public void RelayMetric_DoesNotCountAsACapturingDefaultRoute()
    {
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(1));
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(25));
        Assert.False(SplitDirectPathPolicy.IsCapturingDefaultMetric(SplitDirectPathPolicy.RelayInterfaceRouteMetric));
        // Vista+ GetIpForwardTable reports route metric + interface metric.
        // 9001 is the relay route, not a capturing default the #60 guard may strip.
        Assert.False(SplitDirectPathPolicy.IsCapturingDefaultMetric(SplitDirectPathPolicy.RelayInterfaceRouteMetric + 1));
        Assert.True(SplitDirectPathPolicy.IsRelayInterfaceRoute(SplitDirectPathPolicy.RelayInterfaceRouteMetric));
        Assert.True(SplitDirectPathPolicy.IsRelayInterfaceRoute(SplitDirectPathPolicy.ObservedRelayMetric(25)));
        Assert.Equal(
            SplitDirectPathPolicy.RelayInterfaceRouteMetric - SplitDirectPathPolicy.MaxAutomaticInterfaceMetric,
            SplitDirectPathPolicy.RelayMetricFloor);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void HostRoutes_AreOnlyForFullRouteOrInclude(bool fullRoute, bool included, bool expected)
        => Assert.Equal(expected, SplitDirectPathPolicy.ShouldInstallCapturingHostRoute(fullRoute, included));

    [Fact]
    public void UnselectedApp_StaysDirectEvenWhenASelectedFlowExists()
    {
        var action = SplitDirectPathPolicy.Decide(
            fullRoute: false,
            included: false,
            excluded: false,
            probeIp: false,
            blockedProc: false,
            processIsTarget: false,
            forwarderReady: true,
            targetFlowRefCount: 4);

        Assert.False(action.RelaySelectedApp);
        Assert.False(action.RewriteOntoExistingTunnel);
        Assert.True(action.RemoveHostRoute);
    }
}

public class Ipv4PacketRewriteTests
{
    [Fact]
    public void DestinationRewrite_ChangesAddressAndPortOnly()
    {
        var packet = UdpPacket(src: "10.0.0.5", srcPort: 53000, dst: "8.8.8.8", dstPort: 53);
        Assert.True(Ipv4PacketRewrite.TryRewriteDestination(packet, packet.Length, [127, 0, 0, 1], 40000));

        Assert.Equal("10.0.0.5", new IPAddress(packet[12..16]).ToString());
        Assert.Equal("127.0.0.1", new IPAddress(packet[16..20]).ToString());
        Assert.Equal(53000, (packet[20] << 8) | packet[21]);
        Assert.Equal(40000, (packet[22] << 8) | packet[23]);
    }

    [Fact]
    public void SourceRewrite_RestoresTheServerTheAppExpects()
    {
        var packet = UdpPacket(src: "127.0.0.1", srcPort: 40000, dst: "10.0.0.5", dstPort: 53000);
        Assert.True(Ipv4PacketRewrite.TryRewriteSource(packet, packet.Length, [8, 8, 8, 8], 53));

        Assert.Equal("8.8.8.8", new IPAddress(packet[12..16]).ToString());
        Assert.Equal(53, (packet[20] << 8) | packet[21]);
        Assert.Equal("10.0.0.5", new IPAddress(packet[16..20]).ToString());
        Assert.Equal(53000, (packet[22] << 8) | packet[23]);
    }

    [Fact]
    public void FragmentedPacket_IsNotRewritten()
    {
        var packet = UdpPacket(src: "10.0.0.5", srcPort: 1, dst: "1.1.1.1", dstPort: 53);
        packet[6] = 0x00;
        packet[7] = 0x01;
        Assert.False(Ipv4PacketRewrite.TryRewriteDestination(packet, packet.Length, [127, 0, 0, 1], 9));
    }

    private static byte[] UdpPacket(string src, int srcPort, string dst, int dstPort)
    {
        var packet = new byte[32];
        packet[0] = 0x45;
        packet[3] = 32;
        packet[9] = 17;
        var srcBytes = IPAddress.Parse(src).GetAddressBytes();
        var dstBytes = IPAddress.Parse(dst).GetAddressBytes();
        Buffer.BlockCopy(srcBytes, 0, packet, 12, 4);
        Buffer.BlockCopy(dstBytes, 0, packet, 16, 4);
        packet[20] = (byte)(srcPort >> 8);
        packet[21] = (byte)srcPort;
        packet[22] = (byte)(dstPort >> 8);
        packet[23] = (byte)dstPort;
        return packet;
    }
}

public class SplitForwarderSessionTests
{
    [Fact]
    public void DnsSession_RemembersTheResolverTheAppUsedAndTheTunnelTarget()
    {
        var sessions = new SplitForwarderSessions();
        uint client = BitConverter.ToUInt32(IPAddress.Parse("192.168.1.20").GetAddressBytes(), 0);
        uint router = BitConverter.ToUInt32(IPAddress.Parse("192.168.1.1").GetAddressBytes(), 0);
        uint tunnelDns = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);

        sessions.Register(17, client, 53000, router, 53, tunnelDns, 53);

        Assert.True(sessions.TryGet(17, client, 53000, out var session));
        Assert.Equal(router, session.OriginalDestIp);
        Assert.Equal((ushort)53, session.OriginalDestPort);
        Assert.Equal(tunnelDns, session.ForwardDestIp);
        Assert.False(sessions.TryGet(17, client, 53001, out _));
    }

    [Theory]
    [InlineData(6, 443)]
    [InlineData(17, 443)]
    [InlineData(17, 53)]
    [InlineData(6, 53)]
    public void TcpUdpAndDns_RegisterAVpnDialTarget(byte protocol, int port)
    {
        var sessions = new SplitForwarderSessions();
        uint client = BitConverter.ToUInt32(IPAddress.Parse("192.168.1.20").GetAddressBytes(), 0);
        uint original = BitConverter.ToUInt32(IPAddress.Parse("192.168.1.1").GetAddressBytes(), 0);
        uint tunnelTarget = BitConverter.ToUInt32(IPAddress.Parse("142.251.127.188").GetAddressBytes(), 0);
        if (port == 53)
            tunnelTarget = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);

        sessions.Register(protocol, client, 53000, original, (ushort)port, tunnelTarget, (ushort)port);

        Assert.True(SplitDirectPathPolicy.TransportUsesRelaySocket(protocol));
        Assert.True(sessions.TryGet(protocol, client, 53000, out var session));
        Assert.Equal(original, session.OriginalDestIp);
        Assert.Equal(tunnelTarget, session.ForwardDestIp);
        Assert.Equal((ushort)port, session.ForwardDestPort);
    }
}

/// <summary>
/// v2.2.3 loop: route.exe installs the relay at metric 9000, GetIpForwardTable
/// reports 9000 + interface metric, and the #60 guard deleted that row
/// (helperDeleted=1) then the forwarder installed it again.
/// </summary>
public class SplitRelayRouteGuardTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(6u)]
    [InlineData(25u)]
    [InlineData(26u)]
    [InlineData(256u)]
    public void Issue60_CapturingVpnDefaults_AreStillStripped(uint metric)
    {
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(metric));
        var plan = SplitDirectPathPolicy.ClassifyVpnDefaults(new[] { metric });
        Assert.True(plan.StripMetrics.SequenceEqual(new[] { metric }));
        Assert.False(plan.RelaySurvives);
        Assert.False(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new[] { metric }));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(5u)]
    [InlineData(10u)]
    [InlineData(25u)]
    [InlineData(50u)]
    [InlineData(256u)]
    public void WindowsCombinedRelayMetric_SurvivesTheGuard(uint interfaceMetric)
    {
        var observed = SplitDirectPathPolicy.ObservedRelayMetric(interfaceMetric);
        var legacyWouldStrip = observed != SplitDirectPathPolicy.RelayInterfaceRouteMetric;

        if (interfaceMetric != 0)
            Assert.True(legacyWouldStrip);

        var plan = SplitDirectPathPolicy.ClassifyVpnDefaults(new[] { observed });
        Assert.Empty(plan.StripMetrics);
        Assert.True(plan.KeptRelayMetrics.SequenceEqual(new[] { observed }));
        Assert.False(SplitDirectPathPolicy.ShouldReinstallRelayRoute(new[] { observed }));
        Assert.True(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new[] { observed }));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(25u)]
    [InlineData(50u)]
    public void RouteExeSubtractedAutomaticInterfaceMetric_IsStillKept(uint interfaceMetric)
    {
        var observed = SplitDirectPathPolicy.RelayInterfaceRouteMetric - interfaceMetric;
        Assert.True(observed >= SplitDirectPathPolicy.RelayMetricFloor);
        Assert.False(SplitDirectPathPolicy.ShouldReinstallRelayRoute(new[] { observed }));
        Assert.True(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new[] { observed }));
    }

    [Fact]
    public void MetricJustBelowTheRelayFloor_IsStillStripped()
    {
        var metric = SplitDirectPathPolicy.RelayMetricFloor - 1;
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(metric));
        Assert.True(SplitDirectPathPolicy.ShouldReinstallRelayRoute(new[] { metric }));
    }

    [Fact]
    public void Guard_StripsCapturingDefault_AndDoesNotReaddTheRelay()
    {
        // Owner's VPN IF had the relay (9000 + interface metric 25 = 9025).
        // A real IPCP/WireGuard default (metric 1) must still be removed.
        var relay = SplitDirectPathPolicy.ObservedRelayMetric(25);
        var plan = SplitDirectPathPolicy.ClassifyVpnDefaults(new uint[] { 1, relay, 25 });

        Assert.True(plan.StripMetrics.SequenceEqual(new uint[] { 1, 25 }));
        Assert.True(plan.KeptRelayMetrics.SequenceEqual(new uint[] { relay }));
        Assert.False(SplitDirectPathPolicy.ShouldReinstallRelayRoute(plan.KeptRelayMetrics));
        Assert.True(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new uint[] { 1, relay, 25 }));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(17)]
    public void SelectedTcpUdpAndDns_HaveARouteWhenTheRelaySurvives(int protocol)
    {
        Assert.True(SplitDirectPathPolicy.TransportUsesRelaySocket((byte)protocol));

        var selected = SplitDirectPathPolicy.Decide(
            fullRoute: false,
            included: false,
            excluded: false,
            probeIp: false,
            blockedProc: false,
            processIsTarget: true,
            forwarderReady: true,
            targetFlowRefCount: 1);

        Assert.True(selected.RelaySelectedApp);
        Assert.True(selected.RemoveHostRoute);
        Assert.False(selected.RewriteOntoExistingTunnel);
        Assert.False(SplitDirectPathPolicy.ShouldInstallCapturingHostRoute(fullRoute: false, includedDestination: false));

        var relay = SplitDirectPathPolicy.ObservedRelayMetric(25);
        Assert.True(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new[] { relay }));
    }

    [Fact]
    public void UnselectedApp_StaysDirectWhileTheRelayRouteIsInstalled()
    {
        var action = SplitDirectPathPolicy.Decide(
            fullRoute: false,
            included: false,
            excluded: false,
            probeIp: false,
            blockedProc: false,
            processIsTarget: false,
            forwarderReady: true,
            targetFlowRefCount: 4);

        Assert.False(action.RelaySelectedApp);
        Assert.False(action.RewriteOntoExistingTunnel);
        Assert.True(action.RemoveHostRoute);
        Assert.False(SplitDirectPathPolicy.ShouldInstallCapturingHostRoute(false, false));

        var relay = SplitDirectPathPolicy.ObservedRelayMetric(25);
        Assert.True(SplitDirectPathPolicy.IsRelayInterfaceRoute(relay));
        Assert.False(SplitDirectPathPolicy.IsCapturingDefaultMetric(relay));
    }

    [Fact]
    public void IncludedDestination_StillUsesAHostRoute()
    {
        Assert.True(SplitDirectPathPolicy.ShouldInstallCapturingHostRoute(fullRoute: false, includedDestination: true));

        var action = SplitDirectPathPolicy.Decide(
            fullRoute: false,
            included: true,
            excluded: false,
            probeIp: false,
            blockedProc: false,
            processIsTarget: false,
            forwarderReady: true,
            targetFlowRefCount: 0);

        Assert.False(action.RelaySelectedApp);
        Assert.False(action.RemoveHostRoute);
        Assert.True(action.RewriteOntoExistingTunnel);
    }

    [Fact]
    public void FullRoute_CapturingDefault_IsARouteAndTheGuardIsNotWhatKeepsIt()
    {
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(1));
        Assert.True(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: true, new uint[] { 1 }));
        Assert.False(SplitDirectPathPolicy.ForwardedFlowHasVpnRoute(fullRoute: false, new uint[] { 1 }));
    }
}
