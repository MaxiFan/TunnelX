using System.Net;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class HealthCheckTargetsTests
{
    [Fact]
    public void Empty_input_keeps_public_defaults()
    {
        var plan = HealthCheckTargets.Resolve("  \n", includeDefaultPublicEndpoints: true);

        Assert.False(plan.HasCustomEndpoints);
        Assert.False(plan.FellBackToDefaults);
        Assert.True(plan.IncludesDefaultPublicEndpoints);
        Assert.Equal(["google.com:443", "cloudflare.com:443"], plan.Targets.Select(t => t.ToString()));
        Assert.Equal(HealthCheckTargets.DefaultPingTarget, plan.SuggestedPingTarget);
    }

    [Fact]
    public void Disabling_defaults_without_custom_targets_falls_back()
    {
        var plan = HealthCheckTargets.Resolve("", includeDefaultPublicEndpoints: false);

        Assert.True(plan.FellBackToDefaults);
        Assert.True(plan.IncludesDefaultPublicEndpoints);
        Assert.Equal(["google.com:443", "cloudflare.com:443"], plan.Targets.Select(t => t.ToString()));
    }

    [Fact]
    public void Custom_targets_are_used_before_defaults()
    {
        var plan = HealthCheckTargets.Resolve(
            """
            https://intranet.company.com/health
            internal-api.company.local
            10.0.0.1
            """,
            includeDefaultPublicEndpoints: true);

        Assert.Equal(
            [
                "intranet.company.com:443",
                "internal-api.company.local:443",
                "10.0.0.1:443",
                "google.com:443",
                "cloudflare.com:443"
            ],
            plan.Targets.Select(t => t.ToString()));
        Assert.Equal("intranet.company.com", plan.SuggestedPingTarget);
    }

    [Fact]
    public void Public_defaults_can_be_disabled()
    {
        var plan = HealthCheckTargets.Resolve(
            "http://intranet.company.com, 10.0.0.1:8443",
            includeDefaultPublicEndpoints: false);

        Assert.False(plan.IncludesDefaultPublicEndpoints);
        Assert.Equal(
            ["intranet.company.com:80", "10.0.0.1:8443"],
            plan.Targets.Select(t => t.ToString()));
        Assert.Equal("intranet.company.com:80", plan.SuggestedPingTarget);
    }

    [Theory]
    [InlineData("https://intranet.company.com", "intranet.company.com", 443)]
    [InlineData("http://internal-api.company.local/status", "internal-api.company.local", 80)]
    [InlineData("https://files.company.local:8443/health", "files.company.local", 8443)]
    [InlineData("internal-api.company.local", "internal-api.company.local", 443)]
    [InlineData("10.0.0.1", "10.0.0.1", 443)]
    [InlineData("10.0.0.1:8443", "10.0.0.1", 8443)]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", 443)]
    [InlineData("https://[2001:db8::1]/", "2001:db8::1", 443)]
    public void Parses_urls_hosts_and_ips(string raw, string host, int port)
    {
        Assert.True(HealthCheckTargets.TryParse(raw, out var endpoint));
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.True(HealthCheckTargets.TryParse(endpoint.Display, out var roundTrip));
        Assert.Equal(endpoint, roundTrip);
    }

    [Fact]
    public void Invalid_lines_are_skipped_and_duplicates_collapse()
    {
        var plan = HealthCheckTargets.Resolve(
            "google.com:443\nnot a host\nhttps://intranet.company.com\nintranet.company.com",
            includeDefaultPublicEndpoints: true);

        Assert.Equal(["not a host"], plan.InvalidEntries);
        Assert.Equal(
            ["google.com:443", "intranet.company.com:443", "cloudflare.com:443"],
            plan.Targets.Select(t => t.ToString()));
    }

    [Fact]
    public void Extra_custom_targets_beyond_the_cap_are_ignored()
    {
        var raw = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"10.0.0.{i}"));
        var plan = HealthCheckTargets.Resolve(raw, includeDefaultPublicEndpoints: false);

        Assert.Equal(HealthCheckTargets.MaxCustomEndpoints, plan.CustomEndpoints.Count);
        Assert.Equal(["10.0.0.5", "10.0.0.6"], plan.IgnoredExtraEntries);
        Assert.DoesNotContain(plan.Targets, t => t.Host == "google.com");
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.15.0.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    public void Private_and_link_local_addresses_are_identified(string ip, bool isPrivate)
    {
        Assert.Equal(isPrivate, HealthCheckTargets.IsPrivateOrLinkLocal(IPAddress.Parse(ip)));
    }
}
