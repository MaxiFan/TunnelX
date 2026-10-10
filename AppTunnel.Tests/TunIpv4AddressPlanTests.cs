using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class TunIpv4AddressPlanTests
{
    [Fact]
    public void Empty_machine_keeps_historical_tun_prefix()
    {
        var chosen = TunIpv4AddressPlan.Select(Array.Empty<ObservedIpv4>());
        Assert.Equal("172.18.0.1/30", chosen.Cidr);
        Assert.Equal("TunnelX-V2Ray", TunIpv4AddressPlan.SelectInterfaceName(null));
    }

    [Fact]
    public void Docker_or_hyperv_slash16_on_172_18_selects_the_next_free_prefix()
    {
        var observed = new[]
        {
            new ObservedIpv4("172.18.0.1", 16, 12, "vEthernet (WSL)", true),
            new ObservedIpv4("127.0.0.1", 8, 1, "Loopback Pseudo-Interface 1", true),
        };

        var chosen = TunIpv4AddressPlan.Select(observed);
        Assert.Equal("172.19.0.1/30", chosen.Cidr);
        Assert.False(TunIpv4AddressPlan.IsStaleManagedAddress(observed[0]));
    }

    [Fact]
    public void Foreign_host_inside_the_slash30_is_a_subnet_collision()
    {
        var peer = new ObservedIpv4("172.18.0.2", 32, 4, "Ethernet", true);
        Assert.Equal("172.19.0.1", TunIpv4AddressPlan.Select(new[] { peer }).Host);
        Assert.False(TunIpv4AddressPlan.IsStaleManagedAddress(peer));
    }

    [Fact]
    public void Ordinary_lan_does_not_displace_the_default_prefix()
    {
        var lan = new ObservedIpv4("10.0.0.1", 8, 5, "Ethernet", true);
        Assert.Equal("172.18.0.1", TunIpv4AddressPlan.Select(new[] { lan }).Host);
    }

    [Fact]
    public void Burned_and_overlapping_prefixes_walk_forward()
    {
        var burned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "172.18.0.1", "172.19.0.1" };
        var observed = new[] { new ObservedIpv4("172.20.0.5", 24, 3, "Ethernet", true) };
        Assert.Equal("172.21.0.1", TunIpv4AddressPlan.Select(observed, burned).Host);
    }

    [Fact]
    public void Stale_cleanup_is_limited_to_orphaned_or_tunnelx_interfaces()
    {
        Assert.True(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("172.18.0.1", 30, 44, null, false)));
        Assert.True(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("172.19.0.1", 30, 20, "TunnelX-V2Ray", true)));
        Assert.True(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("172.20.0.1", 30, 21, "TunnelX-V2Ray-2", true)));

        Assert.False(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("172.18.0.1", 30, 20, null, true)));
        Assert.False(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("172.18.0.1", 32, 8, "WireGuard Tunnel", true)));
        Assert.False(TunIpv4AddressPlan.IsStaleManagedAddress(
            new ObservedIpv4("8.8.8.8", 32, 5, null, false)));
        Assert.False(TunIpv4AddressPlan.IsManagedTunInterfaceName("TunnelX-WireGuard"));
    }

    [Fact]
    public void Stale_routes_match_only_managed_prefixes_on_dead_or_tunnelx_interfaces()
    {
        var prefix = TunIpv4AddressPlan.DefaultPrefix;
        Assert.True(TunIpv4AddressPlan.IsStaleManagedRoute(prefix.Network, prefix.Mask, false, null));
        Assert.True(TunIpv4AddressPlan.IsStaleManagedRoute(prefix.HostAddress, uint.MaxValue, true, "TunnelX-V2Ray"));
        Assert.False(TunIpv4AddressPlan.IsStaleManagedRoute(prefix.Network, prefix.Mask, true, "vEthernet (Default Switch)"));

        Assert.True(TunIpv4AddressPlan.TryParse("172.18.0.0", out var dockerNetwork));
        Assert.False(TunIpv4AddressPlan.IsStaleManagedRoute(
            dockerNetwork,
            TunIpv4AddressPlan.MaskOf(16),
            false,
            null));

        var rawNetwork = BitConverter.ToUInt32(new byte[] { 172, 18, 0, 0 });
        Assert.Equal(prefix.Network, TunIpv4AddressPlan.FromForwardTableOrder(rawNetwork));
        Assert.True(TunIpv4AddressPlan.IsStaleManagedRoute(
            TunIpv4AddressPlan.FromForwardTableOrder(rawNetwork),
            TunIpv4AddressPlan.FromForwardTableOrder(BitConverter.ToUInt32(new byte[] { 255, 255, 255, 252 })),
            false,
            null));

        Assert.True(TunIpv4AddressPlan.IsManagedPersistentRoute("172.18.0.0,255.255.255.252,0.0.0.0,1"));
        Assert.False(TunIpv4AddressPlan.IsManagedPersistentRoute("172.18.0.0,255.255.0.0,0.0.0.0,1"));
        Assert.False(TunIpv4AddressPlan.IsManagedPersistentRoute("192.168.1.0,255.255.255.0,192.168.1.1,1"));
    }

    [Fact]
    public void Interface_name_advances_only_after_the_primary_name_is_blocked()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TunIpv4AddressPlan.PrimaryInterfaceName
        };
        Assert.Equal("TunnelX-V2Ray-2", TunIpv4AddressPlan.SelectInterfaceName(blocked));

        blocked.Add("TunnelX-V2Ray-2");
        Assert.Equal("TunnelX-V2Ray-3", TunIpv4AddressPlan.SelectInterfaceName(blocked));
    }

    [Fact]
    public void BuildSingBoxDocument_uses_the_requested_tun_address_and_name()
    {
        const string link = "hy2://secret@hy.example:443/?insecure=1#Office";
        var json = V2RayTunnelProvider.BuildSingBoxDocument(
            link,
            2080,
            includeTun: true,
            tunMtu: 1400,
            enableDnsOptimization: false,
            tunAddress: "172.19.0.1/30",
            tunInterfaceName: "TunnelX-V2Ray-2");

        var tun = JsonNode.Parse(json)!["inbounds"]!.AsArray()
            .First(item => item!["type"]!.GetValue<string>() == "tun")!;
        Assert.Equal("172.19.0.1/30", tun["address"]![0]!.GetValue<string>());
        Assert.Equal("TunnelX-V2Ray-2", tun["interface_name"]!.GetValue<string>());

        var fallback = JsonNode.Parse(V2RayTunnelProvider.BuildSingBoxDocument(
            link, 2080, includeTun: true, tunMtu: 1400, enableDnsOptimization: false))!;
        var defaultTun = fallback["inbounds"]!.AsArray()
            .First(item => item!["type"]!.GetValue<string>() == "tun")!;
        Assert.Equal("172.18.0.1/30", defaultTun["address"]![0]!.GetValue<string>());
        Assert.Equal("TunnelX-V2Ray", defaultTun["interface_name"]!.GetValue<string>());
    }

    [Fact]
    public void Unicast_row_field_offsets_match_the_reader()
    {
        Assert.Equal(4, Marshal.OffsetOf<SOCKADDR_INET_TUN>(nameof(SOCKADDR_INET_TUN.Ipv4)).ToInt32());
        Assert.Equal(28, Marshal.SizeOf<SOCKADDR_INET_TUN>());
        Assert.Equal(40, Marshal.OffsetOf<MIB_UNICASTIPADDRESS_ROW>(nameof(MIB_UNICASTIPADDRESS_ROW.InterfaceIndex)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<MIB_UNICASTIPADDRESS_ROW>(nameof(MIB_UNICASTIPADDRESS_ROW.OnLinkPrefixLength)).ToInt32());
    }

    [Fact]
    public void IpHelper_unicast_table_layout_sees_loopback()
    {
        Assert.True(
            TunIpv4ConflictCleanup.UnicastTableContainsLoopback(),
            "GetUnicastIpAddressTable did not yield 127.0.0.1; the MIB_UNICASTIPADDRESS_ROW layout is wrong.");
    }
}
