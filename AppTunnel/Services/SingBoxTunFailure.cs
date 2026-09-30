namespace AppTunnel.Services;

/// <summary>
/// Distinguishes Wintun/TUN adapter readiness failures from bad sing-box JSON.
/// Issue #67: sing-box can exit 1 after "open interface take too much time" /
/// "device is not ready" while the UI previously blamed the profile config.
/// </summary>
internal static class SingBoxTunFailure
{
    public const string EarlyExitConfigKey =
        "sing-box زودتر خارج شد (exit code {0}) — کانفیگ را بررسی کنید";

    public const string BridgeEarlyExitKey =
        "sing-box bridge زودتر خارج شد (exit code {0})";

    public const string TunNotReadyExitKey =
        "آداپتر TUN/Wintun آماده نشد (exit {0}). این خطای کانفیگ نیست. TunnelX را با Administrator اجرا کنید؛ آنتی‌ویروس را بررسی کنید؛ در ncpa.cpl آداپتر گیرکرده TunnelX-V2Ray یا Wintun را حذف کنید؛ برنامه‌های دیگر Wintun را ببندید؛ ویندوز را ری‌استارت کنید و دوباره وصل شوید.";

    public const string TunNotReadyTimeoutKey =
        "آداپتر TUN/Wintun در زمان انتظار ظاهر نشد. این معمولاً مشکل درایور/محیط است نه کانفیگ. TunnelX را با Administrator اجرا کنید؛ آنتی‌ویروس را بررسی کنید؛ در ncpa.cpl آداپتر گیرکرده TunnelX-V2Ray یا Wintun را حذف کنید؛ برنامه‌های دیگر Wintun را ببندید؛ سپس ری‌استارت کنید.";

    public const string InterfaceTimeoutKey =
        "interface TunnelX-V2Ray ظاهر نشد (timeout {0}s)";

    public static bool IsReadinessFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ContainsAny(text,
            "open interface take too much time",
            "the device is not ready for use",
            "device is not ready",
            "configure tun interface",
            "ERROR_NOT_READY",
            "not ready for use");
    }

    public static bool LooksLikeConfigError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ContainsAny(text,
            "unknown field",
            "decode config",
            "unmarshal",
            "invalid config",
            "parse config",
            "legacy config",
            "json:");
    }

    public static string FormatEarlyExitMessage(int exitCode, string? capturedLogs, bool isBridge = false)
    {
        var loc = LocalizationService.Instance;
        if (IsReadinessFailure(capturedLogs))
            return loc.Format(TunNotReadyExitKey, exitCode);

        var fallbackKey = isBridge ? BridgeEarlyExitKey : EarlyExitConfigKey;
        return loc.Format(fallbackKey, exitCode);
    }

    public static string FormatInterfaceWaitFailure(int timeoutSeconds, string? capturedLogs)
    {
        var loc = LocalizationService.Instance;
        if (IsReadinessFailure(capturedLogs))
            return loc.T(TunNotReadyTimeoutKey);

        return loc.Format(InterfaceTimeoutKey, timeoutSeconds);
    }

    public static bool ShouldRetryTunOpen(string? capturedLogs, bool processExited)
    {
        _ = processExited;
        return IsReadinessFailure(capturedLogs);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
