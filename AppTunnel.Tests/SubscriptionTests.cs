using System.Net;
using System.Net.Http;
using System.Text;
using AppTunnel.Models;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class SubscriptionUrlTests
{
    [Theory]
    [InlineData("https://panel.example/sub/abc")]
    [InlineData("  http://panel.example/api?token=1  ")]
    public void TryNormalize_AcceptsHttpUrls(string input)
    {
        Assert.True(SubscriptionUrl.TryNormalize(input, out var normalized, out var error));
        Assert.StartsWith("http", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vmess://abc")]
    [InlineData("not a url")]
    public void TryNormalize_RejectsNonHttp(string input)
    {
        Assert.False(SubscriptionUrl.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void LooksLikeSubscriptionUrl_IgnoresBareProxyEndpoints()
    {
        Assert.True(SubscriptionUrl.LooksLikeSubscriptionUrl("https://panel.example/sub/abc"));
        Assert.True(SubscriptionUrl.LooksLikeSubscriptionUrl("https://panel.example/?token=abc"));
        Assert.False(SubscriptionUrl.LooksLikeSubscriptionUrl("http://1.2.3.4:8080"));
        Assert.False(SubscriptionUrl.LooksLikeSubscriptionUrl("http://user:pass@1.2.3.4:8080"));
        Assert.False(SubscriptionUrl.LooksLikeSubscriptionUrl("vless://id@host:443#name"));
    }
}

public class SubscriptionHeaderTests
{
    [Fact]
    public void ParseProfileTitle_DecodesBase64Prefix()
    {
        var encoded = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("My Sub"));
        Assert.Equal("My Sub", SubscriptionHeaders.ParseProfileTitle(encoded));
        Assert.Equal("Plain", SubscriptionHeaders.ParseProfileTitle("Plain"));
    }

    [Fact]
    public void ParseContentDisposition_ReadsPlainAndExtendedNames()
    {
        Assert.Equal("nodes", SubscriptionHeaders.ParseContentDispositionFileName("attachment; filename=\"nodes\""));
        Assert.Equal("my sub", SubscriptionHeaders.ParseContentDispositionFileName("attachment; filename*=UTF-8''my%20sub"));
    }

    [Fact]
    public void ParseUserInfo_ReadsTrafficFields()
    {
        var info = SubscriptionHeaders.ParseUserInfo("upload=10; download=20; total=100; expire=1893456000");
        Assert.NotNull(info);
        Assert.Equal(10, info!.Upload);
        Assert.Equal(20, info.Download);
        Assert.Equal(100, info.Total);
        Assert.Equal(1893456000, info.Expire);
        Assert.Null(SubscriptionHeaders.ParseUserInfo("nope"));
    }
}

public class SubscriptionClientTests
{
    [Fact]
    public async Task FetchAsync_ReadsBodyTitleAndTraffic()
    {
        var body = "vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha";
        var client = new SubscriptionClient(new StubHandler(HttpStatusCode.OK, body, response =>
        {
            response.Headers.TryAddWithoutValidation("profile-title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Alpha Sub")));
            response.Headers.TryAddWithoutValidation("subscription-userinfo", "upload=1; download=2; total=9; expire=100");
        }));

        var result = await client.FetchAsync("https://panel.example/sub/abc");

        Assert.True(result.Success);
        Assert.Equal(body, result.Body);
        Assert.Equal("Alpha Sub", result.ProfileTitle);
        Assert.Equal(9, result.Traffic?.Total);
    }

    [Fact]
    public async Task FetchAsync_ReportsHttpErrorsEmptyBodiesAndInvalidUrls()
    {
        var httpError = await new SubscriptionClient(new StubHandler(HttpStatusCode.Forbidden, "no"))
            .FetchAsync("https://panel.example/sub/abc");
        Assert.False(httpError.Success);
        Assert.Contains("403", httpError.ErrorMessage, StringComparison.Ordinal);

        var empty = await new SubscriptionClient(new StubHandler(HttpStatusCode.OK, "   "))
            .FetchAsync("https://panel.example/sub/abc");
        Assert.False(empty.Success);

        var invalid = await new SubscriptionClient(new StubHandler(HttpStatusCode.OK, "vless://x"))
            .FetchAsync("not-a-url");
        Assert.False(invalid.Success);
    }

    [Fact]
    public async Task FetchAsync_ReportsOversizedBody()
    {
        var huge = new string('A', SubscriptionClient.MaxBodyBytes + 1);
        var result = await new SubscriptionClient(new StubHandler(HttpStatusCode.OK, huge))
            .FetchAsync("https://panel.example/sub/abc");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchAsync_Base64BodyImportsThroughSync()
    {
        var plain = "vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha\ntrojan://secret@b.example:443#Beta";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var fetched = await new SubscriptionClient(new StubHandler(HttpStatusCode.OK, body))
            .FetchAsync("https://panel.example/sub/abc");

        Assert.True(fetched.Success);
        var profiles = new List<ConnectionProfile>();
        var sync = SubscriptionSync.Apply("sub1", ConfigImportService.ParseClipboard(fetched.Body), profiles);

        Assert.Equal(2, sync.Added);
        Assert.Equal(new[] { "Alpha", "Beta" }, profiles.Select(p => p.Name).ToArray());
        Assert.All(profiles, p => Assert.Equal("sub1", p.SubscriptionId));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Action<HttpResponseMessage>? _configure;

        public StubHandler(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
        {
            _status = status;
            _body = body;
            _configure = configure;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/plain")
            };
            _configure?.Invoke(response);
            return Task.FromResult(response);
        }
    }
}

public class SubscriptionSyncTests
{
    [Fact]
    public void Apply_AddsUpdatesAndRemovesSubscriptionProfiles()
    {
        var profiles = new List<ConnectionProfile>();
        var first = Drafts("vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha", "trojan://secret@b.example:443#Beta");

        var added = SubscriptionSync.Apply("sub1", first, profiles);
        Assert.True(added.Applied);
        Assert.Equal(2, added.Added);
        Assert.Equal(2, profiles.Count);
        profiles[0].TunnelApps.Add(new ProfileApp { DisplayName = "Browser", ExecutableName = "browser.exe" });
        var keptId = profiles[0].Id;

        var renamed = Drafts("vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha-2", "trojan://secret@b.example:443#Beta");
        var updated = SubscriptionSync.Apply("sub1", renamed, profiles);
        Assert.Equal(0, updated.Added);
        Assert.Equal(2, updated.Updated);
        Assert.Equal(0, updated.Removed);
        Assert.Equal("Alpha-2", profiles.Single(p => p.Id == keptId).Name);
        Assert.Equal("browser.exe", profiles.Single(p => p.Id == keptId).TunnelApps[0].ExecutableName);

        var shrunk = Drafts("trojan://secret@b.example:443#Beta");
        var removed = SubscriptionSync.Apply("sub1", shrunk, profiles);
        Assert.Equal(1, removed.Removed);
        Assert.Single(profiles);
        Assert.Equal("Beta", profiles[0].Name);
    }

    [Fact]
    public void Apply_DoesNotWipeProfilesWhenNothingUsableArrives()
    {
        var profiles = new List<ConnectionProfile>
        {
            new()
            {
                Name = "Alpha",
                TunnelType = TunnelType.V2Ray,
                V2RayConfig = "vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha",
                SubscriptionId = "sub1",
                SubscriptionNodeKey = "keep"
            }
        };

        var result = SubscriptionSync.Apply("sub1", ConfigImportService.ParseClipboard("not a config"), profiles);

        Assert.False(result.Applied);
        Assert.Single(profiles);
        Assert.Equal("Alpha", profiles[0].Name);
    }

    [Fact]
    public void Apply_SkipsNodesThatDuplicateAManualProfile()
    {
        var link = "vless://11111111-1111-1111-1111-111111111111@a.example:443#Alpha";
        var profiles = new List<ConnectionProfile>
        {
            new()
            {
                Name = "Manual",
                TunnelType = TunnelType.V2Ray,
                V2RayConfig = link
            }
        };

        var result = SubscriptionSync.Apply("sub1", Drafts(link), profiles);

        Assert.True(result.Applied);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Single(profiles);
        Assert.Equal("", profiles[0].SubscriptionId);
    }

    private static IReadOnlyList<ImportedConfigDraft> Drafts(params string[] links)
        => ConfigImportService.ParseClipboard(string.Join('\n', links));
}
