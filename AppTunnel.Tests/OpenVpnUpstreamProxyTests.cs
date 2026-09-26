using AppTunnel.Models;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class OpenVpnUpstreamProxyTests
{
    [Fact]
    public void BuildConfigLines_HttpWithoutCredentials_UsesHttpProxy()
    {
        var lines = OpenVpnUpstreamProxy.BuildConfigLines(Http("proxy.example", 3128), null);

        Assert.Equal(new[]
        {
            "http-proxy proxy.example 3128",
            "http-proxy-retry"
        }, lines);
    }

    [Fact]
    public void BuildConfigLines_HttpWithCredentials_UsesBasicAuthFile()
    {
        var settings = Http("10.1.2.3", 8080, "alice", "secret");
        var lines = OpenVpnUpstreamProxy.BuildConfigLines(settings, "\"C:/TunnelX/tunnelx-proxy-auth.txt\"");

        Assert.Equal(
            "http-proxy 10.1.2.3 8080 \"C:/TunnelX/tunnelx-proxy-auth.txt\" basic",
            lines[0]);
        Assert.Equal("http-proxy-retry", lines[1]);
        Assert.DoesNotContain("secret", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfigLines_SocksWithCredentials_UsesSocksProxy()
    {
        var settings = Socks("socks.example", 1080, "bob", "pw");
        var lines = OpenVpnUpstreamProxy.BuildConfigLines(settings, "\"C:/auth.txt\"");

        Assert.Equal(new[]
        {
            "socks-proxy socks.example 1080 \"C:/auth.txt\"",
            "socks-proxy-retry"
        }, lines);
    }

    [Fact]
    public void BuildConfigLines_Disabled_EmitsNothing()
    {
        var lines = OpenVpnUpstreamProxy.BuildConfigLines(
            OpenVpnUpstreamProxySettings.From(OpenVpnUpstreamProxyKind.None, "proxy", 8080, "", ""),
            null);

        Assert.Empty(lines);
    }

    [Fact]
    public void BuildAuthFileBody_IsUsernameThenPassword()
    {
        Assert.Equal("alice\nsecret", OpenVpnUpstreamProxy.BuildAuthFileBody(" alice ", "secret"));
    }

    [Fact]
    public void TryParse_HttpProxyLine()
    {
        const string config = """
            client
            proto tcp
            remote vpn.example 443
            http-proxy proxy.example.net 3128
            """;

        Assert.True(OpenVpnUpstreamProxy.TryParse(config, out var parsed));
        Assert.Equal(OpenVpnUpstreamProxyKind.Http, parsed.Kind);
        Assert.Equal("proxy.example.net", parsed.Host);
        Assert.Equal(3128, parsed.Port);
        Assert.False(parsed.ReferencesExternalAuthFile);
        Assert.False(parsed.HasInlineCredentials);
    }

    [Fact]
    public void TryParse_SocksProxyDefaultsPortAndDetectsAuthFile()
    {
        const string config = "socks-proxy socks.example auth.txt";

        Assert.True(OpenVpnUpstreamProxy.TryParse(config, out var parsed));
        Assert.Equal(OpenVpnUpstreamProxyKind.Socks, parsed.Kind);
        Assert.Equal("socks.example", parsed.Host);
        Assert.Equal(1080, parsed.Port);
        Assert.True(parsed.ReferencesExternalAuthFile);
    }

    [Fact]
    public void TryParse_InlineHttpProxyUserPass_ImportsCredentials()
    {
        const string config = """
            proto tcp
            remote vpn.example 443
            http-proxy proxy.example 8080
            <http-proxy-user-pass>
            alice
            secret
            </http-proxy-user-pass>
            """;

        Assert.True(OpenVpnUpstreamProxy.TryParse(config, out var parsed));
        Assert.True(parsed.HasInlineCredentials);
        Assert.Equal("alice", parsed.Username);
        Assert.Equal("secret", parsed.Password);
        Assert.False(parsed.ReferencesExternalAuthFile);
    }

    [Fact]
    public void TryParse_IgnoresCommentsAndProxyOptions()
    {
        const string config = """
            # http-proxy evil.example 1
            http-proxy-option VERSION 1.1
            socks-proxy 127.0.0.1 9050
            """;

        Assert.True(OpenVpnUpstreamProxy.TryParse(config, out var parsed));
        Assert.Equal(OpenVpnUpstreamProxyKind.Socks, parsed.Kind);
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(9050, parsed.Port);
    }

    [Fact]
    public void TryParse_QuotedAuthPath_IsExternalFileNotInlineSecret()
    {
        const string config = "http-proxy proxy.example 8080 \"C:/my file.txt\" basic";

        Assert.True(OpenVpnUpstreamProxy.TryParse(config, out var parsed));
        Assert.True(parsed.ReferencesExternalAuthFile);
        Assert.False(parsed.HasInlineCredentials);
        Assert.Equal("", parsed.Password);
    }

    [Theory]
    [InlineData("proto tcp\nremote vpn.example 443", true)]
    [InlineData("proto udp\nremote vpn.example 1194", false)]
    [InlineData("remote vpn.example 443 tcp", true)]
    [InlineData("remote vpn.example 1194", false)]
    [InlineData("proto udp\nremote a 1194\nremote b 443 tcp", true)]
    [InlineData("proto udp\n<connection>\nproto tcp-client\nremote vpn.example 443\n</connection>", true)]
    [InlineData("proto tcp\n<connection>\nproto udp\nremote vpn.example 1194\n</connection>", false)]
    [InlineData("<ca>\nremote not-a-remote 1\n</ca>\nproto tcp\nremote real.example 443", true)]
    public void HasTcpTransport_MatchesOpenVpnTcpPaths(string config, bool expected)
    {
        Assert.Equal(expected, OpenVpnUpstreamProxy.HasTcpTransport(config));
    }

    [Fact]
    public void TryKeepTcpConnectionBlock_DropsUdpRemotesAndProxyLines()
    {
        var block = new[]
        {
            "<connection>",
            "proto udp",
            "remote udp.example 1194",
            "remote tcp.example 443 tcp",
            "http-proxy old.proxy 8080",
            "</connection>"
        };

        Assert.True(OpenVpnUpstreamProxy.TryKeepTcpConnectionBlock(block, "udp", out var filtered));
        var text = string.Join("\n", filtered);
        Assert.Contains("remote tcp.example 443 tcp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("udp.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http-proxy", text, StringComparison.Ordinal);
        Assert.Contains("<connection>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetConnectError_RequiresHostPortAndTcp()
    {
        var missingHost = Http("  ", 8080);
        Assert.True(OpenVpnUpstreamProxy.TryGetConnectError(missingHost, "proto tcp\nremote a 443", out var hostKey));
        Assert.Equal(OpenVpnUpstreamProxy.HostRequiredKey, hostKey);

        var badPort = Http("proxy.example", 0);
        Assert.True(OpenVpnUpstreamProxy.TryGetConnectError(badPort, "proto tcp\nremote a 443", out var portKey));
        Assert.Equal(OpenVpnUpstreamProxy.PortInvalidKey, portKey);

        var udp = Http("proxy.example", 8080);
        Assert.True(OpenVpnUpstreamProxy.TryGetConnectError(udp, "proto udp\nremote a 1194", out var tcpKey));
        Assert.Equal(OpenVpnUpstreamProxy.TcpRequiredKey, tcpKey);

        var passwordOnly = Http("proxy.example", 8080, "", "secret");
        Assert.True(OpenVpnUpstreamProxy.TryGetConnectError(passwordOnly, "proto tcp\nremote a 443", out var userKey));
        Assert.Equal(OpenVpnUpstreamProxy.UsernameRequiredKey, userKey);

        Assert.False(OpenVpnUpstreamProxy.TryGetConnectError(
            Http("proxy.example", 8080, "alice", "secret"),
            "proto tcp\nremote a 443",
            out var ok));
        Assert.Equal("", ok);
    }

    [Fact]
    public void TryGetConnectError_DisabledProxy_DoesNotRequireTcp()
    {
        var disabled = OpenVpnUpstreamProxySettings.From(OpenVpnUpstreamProxyKind.None, "", 0, "", "");
        Assert.False(OpenVpnUpstreamProxy.TryGetConnectError(disabled, "proto udp\nremote a 1194", out _));
    }

    private static OpenVpnUpstreamProxySettings Http(string host, int port, string user = "", string password = "") =>
        OpenVpnUpstreamProxySettings.From(OpenVpnUpstreamProxyKind.Http, host, port, user, password);

    private static OpenVpnUpstreamProxySettings Socks(string host, int port, string user, string password) =>
        OpenVpnUpstreamProxySettings.From(OpenVpnUpstreamProxyKind.Socks, host, port, user, password);
}
