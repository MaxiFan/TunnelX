using AppTunnel.Models;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

[Collection(LocalizationCollection.Name)]
public class SingBoxTunFailureTests
{
    private const string Issue67Log =
        "INFO inbound/tun[tun-in]: open interface take too much time to finish!\n" +
        "FATAL start inbound/tun[tun-in]: configure tun interface: The device is not ready for use.";

    private const string Issue78Log =
        "FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: set ipv4 address: The object already exists.";

    [Fact]
    public void Issue67_log_is_tun_readiness_not_config()
    {
        Assert.True(SingBoxTunFailure.IsReadinessFailure(Issue67Log));
        Assert.False(SingBoxTunFailure.LooksLikeConfigError(Issue67Log));
    }

    [Fact]
    public void Issue78_log_is_ipv4_conflict_not_missing_adapter_or_bad_config()
    {
        Assert.True(SingBoxTunFailure.IsIpv4AddressConflict(Issue78Log));
        Assert.True(SingBoxTunFailure.ShouldRetryAsAddressConflict(Issue78Log));
        Assert.False(SingBoxTunFailure.IsReadinessFailure(Issue78Log));
        Assert.False(SingBoxTunFailure.LooksLikeConfigError(Issue78Log));
        Assert.False(SingBoxTunFailure.ShouldRetryTunOpen(Issue78Log, processExited: true));
    }

    [Fact]
    public void Localized_set_ipv4_failure_still_retries_as_address_conflict()
    {
        const string log = "FATAL start inbound/tun[tun-in]: configure tun interface: set ipv4 address: Объект уже существует.";
        Assert.False(SingBoxTunFailure.IsIpv4AddressConflict(log));
        Assert.True(SingBoxTunFailure.ShouldRetryAsAddressConflict(log));
        Assert.False(SingBoxTunFailure.IsReadinessFailure(log));
    }

    [Fact]
    public void Set_ipv4_access_denied_stays_a_readiness_failure()
    {
        const string log = "FATAL configure tun interface: set ipv4 address: Access is denied.";
        Assert.False(SingBoxTunFailure.ShouldRetryAsAddressConflict(log));
        Assert.True(SingBoxTunFailure.IsReadinessFailure(log));
    }

    [Fact]
    public void Issue78_message_names_the_address_collision()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(LocalizationService.EnglishLanguage);
        try
        {
            var message = SingBoxTunFailure.FormatAddressConflict("172.18.0.1/30 (exit 1)");
            Assert.Contains("172.18.0.1/30", message, StringComparison.Ordinal);
            Assert.Contains("already in use", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exit 1", message, StringComparison.Ordinal);
            Assert.DoesNotContain("check the config", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was not ready", message, StringComparison.OrdinalIgnoreCase);

            var early = SingBoxTunFailure.FormatEarlyExitMessage(1, Issue78Log);
            Assert.Contains("already in use", early, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("check the config", early, StringComparison.OrdinalIgnoreCase);

            var shortStatus = ConnectionFailureInsight.GetShortStatus(TunnelType.SocksProxy, message);
            Assert.Equal("TUN address already in use", shortStatus);
        }
        finally
        {
            loc.SetLanguage(LocalizationService.PersianLanguage);
        }
    }

    [Fact]
    public void Issue78_message_is_localized_in_russian()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(LocalizationService.RussianLanguage);
        try
        {
            var message = SingBoxTunFailure.FormatAddressConflict("172.19.0.1/30 (exit 1)");
            Assert.Contains("уже занят", message, StringComparison.Ordinal);
            Assert.Contains("172.19.0.1/30", message, StringComparison.Ordinal);
            Assert.DoesNotContain("check the config", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            loc.SetLanguage(LocalizationService.PersianLanguage);
        }
    }

    [Fact]
    public void Address_conflict_retries_then_stops_with_conflict_disposition()
    {
        var session = new TunEndpointSession();
        Assert.Equal(TunFailureDisposition.RetryAddress, session.Classify(Issue78Log, 1, true));
        Assert.Equal(TunFailureDisposition.RetryAddress, session.Classify(Issue78Log, 2, true));
        Assert.Equal(TunFailureDisposition.RetryAddress, session.Classify(Issue78Log, 3, true));
        Assert.Equal(TunFailureDisposition.FailAddressConflict, session.Classify(Issue78Log, 4, true));
        Assert.Equal(4, session.AddressConflicts);
    }

    [Fact]
    public void Readiness_failure_retries_only_once()
    {
        var session = new TunEndpointSession();
        Assert.Equal(TunFailureDisposition.RetryReadiness, session.Classify(Issue67Log, 1, true));
        Assert.Equal(TunFailureDisposition.FailOther, session.Classify(Issue67Log, 2, true));
        Assert.Equal(0, session.AddressConflicts);
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
