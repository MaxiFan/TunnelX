using AppTunnel.Models;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class ConnectionProfileLatencyDisplayTests
{
    [Fact]
    public void LatencyDisplayText_keeps_full_connect_error_for_row_tooltip()
    {
        var previous = LocalizationService.Instance.LanguageSetting;
        try
        {
            LocalizationService.Instance.SetLanguage(LocalizationService.EnglishLanguage);

            var profile = new ConnectionProfile
            {
                TunnelType = TunnelType.V2Ray,
                V2RayConfig = "vless://guid@example.com:443?type=tcp&security=none#node"
            };
            profile.LastLatencyError = "SOCKS5 connect failed (code 1)";

            Assert.Contains("SOCKS5 connect failed (code 1)", profile.LatencyDisplayText, StringComparison.Ordinal);
            Assert.Contains(LocalizationService.Instance.T("اتصال:"), profile.LatencyDisplayText);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    [Fact]
    public void ServerLatencyDisplayText_keeps_full_error_for_row_tooltip()
    {
        var previous = LocalizationService.Instance.LanguageSetting;
        try
        {
            LocalizationService.Instance.SetLanguage(LocalizationService.EnglishLanguage);

            var profile = new ConnectionProfile
            {
                TunnelType = TunnelType.V2Ray,
                V2RayConfig = "vless://guid@example.com:443?type=tcp&security=none#node"
            };
            profile.LastServerLatencyError = "پاسخی از مقصد پینگ نیامد";

            Assert.Contains("پاسخی از مقصد پینگ نیامد", profile.ServerLatencyDisplayText, StringComparison.Ordinal);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(previous);
        }
    }
}
