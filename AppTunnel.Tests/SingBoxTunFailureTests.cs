using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class SingBoxTunFailureTests
{
    private const string Issue67Log =
        "INFO inbound/tun[tun-in]: open interface take too much time to finish!\n" +
        "FATAL start inbound/tun[tun-in]: configure tun interface: The device is not ready for use.";

    [Fact]
    public void Issue67_log_is_tun_readiness_not_config()
    {
        Assert.True(SingBoxTunFailure.IsReadinessFailure(Issue67Log));
        Assert.False(SingBoxTunFailure.LooksLikeConfigError(Issue67Log));
    }

    [Fact]
    public void Unknown_field_is_config_error()
    {
        const string log = "ERROR inbound: decode config: outbounds[0].users: unknown field \"users\"";
        Assert.False(SingBoxTunFailure.IsReadinessFailure(log));
        Assert.True(SingBoxTunFailure.LooksLikeConfigError(log));
        Assert.False(SingBoxTunFailure.ShouldRetryTunOpen(log, processExited: true));
    }

    [Fact]
    public void Early_exit_message_does_not_blame_config_for_tun_failure()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(LocalizationService.EnglishLanguage);
        try
        {
            var message = SingBoxTunFailure.FormatEarlyExitMessage(1, Issue67Log);
            Assert.DoesNotContain("check the config", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TUN/Wintun", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exit 1", message, StringComparison.Ordinal);
        }
        finally
        {
            loc.SetLanguage(LocalizationService.PersianLanguage);
        }
    }

    [Fact]
    public void Interface_timeout_uses_tun_copy_when_logs_match()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(LocalizationService.EnglishLanguage);
        try
        {
            var message = SingBoxTunFailure.FormatInterfaceWaitFailure(20, Issue67Log);
            Assert.Contains("driver/environment", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            loc.SetLanguage(LocalizationService.PersianLanguage);
        }
    }

    [Fact]
    public void Generic_early_exit_keeps_config_hint_without_tun_logs()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(LocalizationService.EnglishLanguage);
        try
        {
            var message = SingBoxTunFailure.FormatEarlyExitMessage(1, "FATAL decode config: unexpected end of JSON");
            Assert.Contains("check the config", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            loc.SetLanguage(LocalizationService.PersianLanguage);
        }
    }
}
