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
        Assert.True(SplitDirectPathPolicy.IsCapturingDefaultMetric(SplitDirectPathPolicy.RelayInterfaceRouteMetric + 1));
        Assert.True(SplitDirectPathPolicy.IsRelayInterfaceRoute(SplitDirectPathPolicy.RelayInterfaceRouteMetric));
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
}
