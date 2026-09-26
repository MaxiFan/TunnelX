using System.Text;
using System.Text.Json.Nodes;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class HysteriaShareLinkTests
{
    [Fact]
    public void Parse_Hysteria2ShareLink_BuildsSingBoxOutbound()
    {
        var link = "hysteria2://p%40ss@hy.example:8443/?insecure=1&sni=www.example.com&obfs=salamander&obfs-password=obfs-secret&mport=35000-35050,36000&hopInterval=30&upmbps=20&downmbps=50&pinSHA256=abc&alpn=h3#Office";

        var (outbound, tag) = HysteriaShareLink.Parse(link);

        Assert.Equal("Office", tag);
        Assert.Equal("hysteria2", outbound["type"]!.GetValue<string>());
        Assert.Equal("hy.example", outbound["server"]!.GetValue<string>());
        Assert.Equal(8443, outbound["server_port"]!.GetValue<int>());
        Assert.Equal("p@ss", outbound["password"]!.GetValue<string>());
        Assert.Equal(20, outbound["up_mbps"]!.GetValue<int>());
        Assert.Equal(50, outbound["down_mbps"]!.GetValue<int>());
        Assert.Equal("salamander", outbound["obfs"]!["type"]!.GetValue<string>());
        Assert.Equal("obfs-secret", outbound["obfs"]!["password"]!.GetValue<string>());
        Assert.Equal("30s", outbound["hop_interval"]!.GetValue<string>());
        Assert.Equal("35000:35050", outbound["server_ports"]![0]!.GetValue<string>());
        Assert.Equal("36000", outbound["server_ports"]![1]!.GetValue<string>());
        Assert.True(outbound["tls"]!["enabled"]!.GetValue<bool>());
        Assert.True(outbound["tls"]!["insecure"]!.GetValue<bool>());
        Assert.Equal("www.example.com", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("h3", outbound["tls"]!["alpn"]![0]!.GetValue<string>());
        Assert.False(outbound["tls"]!.AsObject().ContainsKey("certificate_public_key_sha256"));
        Assert.False(TunnelProviderFactory.RequiresXray(link));
    }

    [Fact]
    public void Parse_Hy2QueryAuth_OmitsBrutalBandwidthAndDefaultsPort()
    {
        var (outbound, tag) = HysteriaShareLink.Parse("HY2://example.com/?auth=secret#Node");

        Assert.Equal("Node", tag);
        Assert.Equal("hysteria2", outbound["type"]!.GetValue<string>());
        Assert.Equal(443, outbound["server_port"]!.GetValue<int>());
        Assert.Equal("secret", outbound["password"]!.GetValue<string>());
        Assert.False(outbound.ContainsKey("up_mbps"));
        Assert.False(outbound.ContainsKey("down_mbps"));
        Assert.False(outbound.ContainsKey("obfs"));
        Assert.Equal("example.com", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.False(outbound["tls"]!.AsObject().ContainsKey("insecure"));
        Assert.Equal("h3", outbound["tls"]!["alpn"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Parse_Hysteria1_UsesUdpAuthAndDefaultBandwidth()
    {
        var link = "hysteria://hy1.example:36712?protocol=udp&auth=letmein&peer=sni.example&insecure=1&upmbps=10&downmbps=20&obfs=xplus&obfsParam=hide-me#HY1";

        var (outbound, tag) = HysteriaShareLink.Parse(link);

        Assert.Equal("HY1", tag);
        Assert.Equal("hysteria", outbound["type"]!.GetValue<string>());
        Assert.Equal("hy1.example", outbound["server"]!.GetValue<string>());
        Assert.Equal(36712, outbound["server_port"]!.GetValue<int>());
        Assert.Equal("letmein", outbound["auth_str"]!.GetValue<string>());
        Assert.Equal("hide-me", outbound["obfs"]!.GetValue<string>());
        Assert.Equal(10, outbound["up_mbps"]!.GetValue<int>());
        Assert.Equal(20, outbound["down_mbps"]!.GetValue<int>());
        Assert.Equal("sni.example", outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.True(outbound["tls"]!["insecure"]!.GetValue<bool>());
    }

    [Fact]
    public void Parse_Hysteria1WithoutBandwidth_DefaultsTo100Mbps()
    {
        var (outbound, _) = HysteriaShareLink.Parse("hysteria://example.com:443?auth=pw");

        Assert.Equal(100, outbound["up_mbps"]!.GetValue<int>());
        Assert.Equal(100, outbound["down_mbps"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_Ipv6WithoutSni_DoesNotUseAddressAsServerName()
    {
        var (outbound, _) = HysteriaShareLink.Parse("hysteria2://pw@[2001:db8::1]:443/?sni=www.example.com");

        Assert.Equal("2001:db8::1", outbound["server"]!.GetValue<string>());
        Assert.Equal("www.example.com", outbound["tls"]!["server_name"]!.GetValue<string>());

        var (noSni, _) = HysteriaShareLink.Parse("hysteria2://pw@[2001:db8::1]:443/");
        Assert.False(noSni["tls"]!.AsObject().ContainsKey("server_name"));
    }

    [Fact]
    public void Parse_RejectsNonUdpHysteria1AndUnknownObfs()
    {
        var fakeTcp = Assert.Throws<InvalidOperationException>(() =>
            HysteriaShareLink.Parse("hysteria://example.com:443?protocol=faketcp&auth=pw"));
        Assert.Equal(HysteriaShareLink.UdpOnlyMessage, fakeTcp.Message);

        var obfs = Assert.Throws<InvalidOperationException>(() =>
            HysteriaShareLink.Parse("hysteria2://pw@example.com:443/?obfs=custom"));
        Assert.Equal(HysteriaShareLink.Hy2ObfsTypeMessage, obfs.Message);
    }

    [Fact]
    public void TryCreateOutbound_WrapsBareAndOutboundOnlyJson_LeavesFullConfig()
    {
        const string bare = """
            {
              "type": "Hysteria2",
              "server": "bare.example",
              "server_port": 443,
              "password": "pw"
            }
            """;
        Assert.True(HysteriaShareLink.TryCreateOutbound(bare, out var bareOutbound, out var bareTag));
        Assert.Equal("hysteria2-out", bareTag);
        Assert.Equal("hysteria2", bareOutbound["type"]!.GetValue<string>());
        Assert.Equal("bare.example", bareOutbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("h3", bareOutbound["tls"]!["alpn"]![0]!.GetValue<string>());

        const string document = """
            {"outbounds":[{"type":"hysteria2","tag":"edge","server":"edge.example","server_port":443,"password":"pw","tls":{"enabled":true,"server_name":"edge.example"}}]}
            """;
        Assert.True(HysteriaShareLink.TryCreateOutbound(document, out var wrapped, out var wrappedTag));
        Assert.Equal("edge", wrappedTag);
        Assert.Equal("edge.example", wrapped["server"]!.GetValue<string>());

        const string full = """
            {"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":2080}],"outbounds":[{"type":"hysteria2","tag":"keep","server":"full.example","server_port":443,"password":"pw"}]}
            """;
        Assert.False(HysteriaShareLink.TryCreateOutbound(full, out _, out _));
        Assert.True(HysteriaShareLink.IsHysteria(full));
    }

    [Fact]
    public void BuildSingBoxDocument_PlacesHysteriaOutboundAndMixedInbound()
    {
        var link = "hy2://secret@hy.example:443/?insecure=1#Office";
        var json = V2RayTunnelProvider.BuildSingBoxDocument(link, 2091, includeTun: false, tunMtu: 1400, enableDnsOptimization: false);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("hysteria2", root["outbounds"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Office", root["outbounds"]![0]!["tag"]!.GetValue<string>());
        Assert.Equal("secret", root["outbounds"]![0]!["password"]!.GetValue<string>());
        Assert.Equal("hy.example", root["outbounds"]![0]!["server"]!.GetValue<string>());
        Assert.Equal("direct", root["outbounds"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("mixed", root["inbounds"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(2091, root["inbounds"]![0]!["listen_port"]!.GetValue<int>());
        Assert.Equal("Office", root["route"]!["rules"]![0]!["outbound"]!.GetValue<string>());
        Assert.DoesNotContain(root["inbounds"]!.AsArray(), item => item!["type"]!.GetValue<string>() == "tun");

        var withTun = JsonNode.Parse(V2RayTunnelProvider.BuildSingBoxDocument(
            link, 2080, includeTun: true, tunMtu: 1400, enableDnsOptimization: false))!.AsObject();
        Assert.Contains(withTun["inbounds"]!.AsArray(), item => item!["type"]!.GetValue<string>() == "tun");
    }

    [Fact]
    public void BuildSingBoxDocument_PassesThroughCompleteJson()
    {
        const string full = """{"inbounds":[{"type":"mixed"}],"outbounds":[{"type":"hysteria","tag":"keep","server":"full.example","server_port":443,"auth_str":"pw"}]}""";
        var json = V2RayTunnelProvider.BuildSingBoxDocument(full, 2080, includeTun: true, tunMtu: 1400, enableDnsOptimization: false);
        Assert.Equal(full, json);
    }

    [Fact]
    public void Import_AcceptsShareLinksBareJsonAndSubscription()
    {
        var link = ConfigImportService.ParseClipboard("hysteria2://secret@hy.example:443/?insecure=1#Office");
        var imported = Assert.Single(link);
        Assert.Equal(AppTunnel.Models.TunnelType.V2Ray, imported.TunnelType);
        Assert.Equal("Office", imported.SuggestedName);
        Assert.Null(imported.SkipReason);

        var bare = ConfigImportService.ParseClipboard("""
            {"type":"hysteria","tag":"hy1-node","server":"hy1.example","server_port":443,"auth_str":"pw"}
            """);
        var bareDraft = Assert.Single(bare);
        Assert.Equal("hy1-node", bareDraft.SuggestedName);
        Assert.Null(bareDraft.SkipReason);

        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes("hy2://secret@sub.example:443/#FromSub"));
        var fromSub = Assert.Single(ConfigImportService.ParseClipboard(blob));
        Assert.Equal("FromSub", fromSub.SuggestedName);
        Assert.StartsWith("hy2://", fromSub.ConfigText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectionPing_IncludesHysteriaLinksAndWrappableJsonOnly()
    {
        Assert.True(ConnectionPingSupport.SupportsSingBoxShareLink("hysteria2://pw@example.com:443/"));
        Assert.True(ConnectionPingSupport.SupportsSingBoxShareLink("hysteria://example.com:443?auth=pw"));
        Assert.True(ConnectionPingSupport.SupportsSingBoxShareLink("""
            {"type":"hysteria2","server":"example.com","server_port":443,"password":"pw"}
            """));
        Assert.False(ConnectionPingSupport.SupportsSingBoxShareLink("""
            {"inbounds":[{"type":"tun"}],"outbounds":[{"type":"hysteria2","server":"example.com","server_port":443,"password":"pw"}]}
            """));
        Assert.False(ConnectionPingSupport.SupportsSingBoxShareLink("vmess://abc"));
    }

    [Fact]
    public void EndpointHelper_ReadsShareLinkAndBareOutbound()
    {
        Assert.True(V2RayEndpointHelper.TryExtract("hysteria2://pw@hy.example:8443/", out var host, out var port));
        Assert.Equal("hy.example", host);
        Assert.Equal(8443, port);

        Assert.True(V2RayEndpointHelper.TryExtract(
            """{"type":"hysteria2","server":"bare.example","server_port":36712,"password":"pw"}""",
            out host,
            out port));
        Assert.Equal("bare.example", host);
        Assert.Equal(36712, port);
    }
}
