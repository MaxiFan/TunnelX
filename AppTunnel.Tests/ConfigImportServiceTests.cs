using System.Text;
using AppTunnel.Models;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class ConfigImportServiceTests
{
    [Fact]
    public void ParseClipboard_DecodesWrappedBase64ShareList()
    {
        var plain = """
            vless://11111111-1111-1111-1111-111111111111@a.example:443?encryption=none#Alpha
            trojan://secret@b.example:443#Beta
            """;
        var wrapped = WrapBase64(plain, lineLength: 32);

        var drafts = Usable(ConfigImportService.ParseClipboard(wrapped));

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Alpha", drafts[0].SuggestedName);
        Assert.Equal("Beta", drafts[1].SuggestedName);
        Assert.Equal(TunnelType.V2Ray, drafts[0].TunnelType);
        Assert.StartsWith("vless://", drafts[0].ConfigText, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseClipboard_ReadsPlainShareLinksAndSingBoxJson()
    {
        var text = """
            ss://YWVzLTI1Ni1nY206cGFzcw@c.example:8388#Shadow
            {"outbounds":[{"type":"vless","tag":"sg-1","server":"d.example","server_port":443}]}
            """;

        var drafts = Usable(ConfigImportService.ParseClipboard(text));

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Shadow", drafts[0].SuggestedName);
        Assert.Equal("sg-1", drafts[1].SuggestedName);
    }

    [Fact]
    public void ParseClipboard_ExpandsJsonArrayOfLinks()
    {
        var text = """
            ["vless://11111111-1111-1111-1111-111111111111@a.example:443#One","trojan://secret@b.example:443#Two"]
            """;

        var drafts = Usable(ConfigImportService.ParseClipboard(text));

        Assert.Equal(2, drafts.Count);
        Assert.Equal("One", drafts[0].SuggestedName);
        Assert.Equal("Two", drafts[1].SuggestedName);
    }

    [Fact]
    public void ParseClipboard_EmptyAndUnknownTextProduceNoUsableConfigs()
    {
        Assert.Empty(Usable(ConfigImportService.ParseClipboard("   ")));
        Assert.Empty(Usable(ConfigImportService.ParseClipboard(null)));
        Assert.Empty(Usable(ConfigImportService.ParseClipboard("this is not a config")));
    }

    [Fact]
    public void ClassifySubscription_DetectsEmptyHtmlAndClash()
    {
        Assert.Equal(SubscriptionContentKind.Empty, ConfigImportService.ClassifySubscription("  "));
        Assert.Equal(SubscriptionContentKind.Html, ConfigImportService.ClassifySubscription("<!DOCTYPE html><html><body>expired</body></html>"));
        Assert.Equal(SubscriptionContentKind.Clash, ConfigImportService.ClassifySubscription("""
            proxies:
              - name: n1
                type: vmess
                server: example.com
                port: 443
            """));
        Assert.Equal(SubscriptionContentKind.Clash, ConfigImportService.ClassifySubscription(WrapBase64("""
            proxies:
              - name: n1
                type: vless
                server: example.com
            """)));
        Assert.NotNull(ConfigImportService.DescribeSubscriptionProblem("<html><body>nope</body></html>"));
        Assert.Null(ConfigImportService.DescribeSubscriptionProblem("vless://11111111-1111-1111-1111-111111111111@a.example:443#Ok"));
    }

    [Fact]
    public void GetSubscriptionNodeKey_IgnoresRemarks()
    {
        var first = Profile("vless://11111111-1111-1111-1111-111111111111@a.example:443?encryption=none#Alpha");
        var renamed = Profile("vless://11111111-1111-1111-1111-111111111111@a.example:443?encryption=none#Renamed");
        Assert.Equal(ConfigImportService.GetSubscriptionNodeKey(first), ConfigImportService.GetSubscriptionNodeKey(renamed));

        var vmessA = Profile(Vmess("alpha", "a.example", "443", "11111111-1111-1111-1111-111111111111"));
        var vmessB = Profile(Vmess("beta", "a.example", "443", "11111111-1111-1111-1111-111111111111"));
        var vmessOther = Profile(Vmess("alpha", "b.example", "443", "11111111-1111-1111-1111-111111111111"));
        Assert.Equal(ConfigImportService.GetSubscriptionNodeKey(vmessA), ConfigImportService.GetSubscriptionNodeKey(vmessB));
        Assert.NotEqual(ConfigImportService.GetSubscriptionNodeKey(vmessA), ConfigImportService.GetSubscriptionNodeKey(vmessOther));
    }

    private static List<ImportedConfigDraft> Usable(IReadOnlyList<ImportedConfigDraft> drafts)
        => drafts.Where(d => string.IsNullOrWhiteSpace(d.SkipReason)).ToList();

    private static ConnectionProfile Profile(string config) => new()
    {
        TunnelType = TunnelType.V2Ray,
        V2RayConfig = config
    };

    private static string Vmess(string remark, string host, string port, string id)
    {
        var json = "{\"v\":\"2\",\"ps\":\"" + remark + "\",\"add\":\"" + host + "\",\"port\":\"" + port + "\",\"id\":\"" + id + "\",\"aid\":\"0\",\"net\":\"tcp\",\"type\":\"none\",\"host\":\"\",\"path\":\"\",\"tls\":\"\"}";
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string WrapBase64(string text, int lineLength = 0)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (lineLength <= 0)
            return b64;

        var lines = new List<string>();
        for (var i = 0; i < b64.Length; i += lineLength)
            lines.Add(b64.Substring(i, Math.Min(lineLength, b64.Length - i)));
        return string.Join("\n", lines);
    }
}
