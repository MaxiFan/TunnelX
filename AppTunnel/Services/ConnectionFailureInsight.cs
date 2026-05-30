using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>Maps full connection failure messages to compact status-bar text (fa/en via LocalizationService).</summary>
public static class ConnectionFailureInsight
{
    private static readonly Dictionary<string, string> ShortKeyByMessageKey = new(StringComparer.Ordinal)
    {
        ["خطا در ایجاد VPN: {0}"] = "اتصال L2TP ناموفق",
        ["WireGuard for Windows نصب نیست. برای استفاده از WireGuard Split Tunnel، ابتدا WireGuard رسمی ویندوز را نصب کنید."] = "WireGuard نصب نیست",
        ["WireGuard رسمی ویندوز نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"] = "WireGuard نصب نیست",
        ["WireGuard service اجرا نشد: {0}"] = "اتصال WireGuard ناموفق",
        ["آداپتر WireGuard بالا نیامد (timeout)"] = "آداپتر VPN بالا نیامد",
        ["فایل sing-box.exe پیدا نشد: {0}"] = "پیش‌نیاز اتصال آماده نیست",
        ["خطا در پارس کانفیگ: {0}"] = "خطا در کانفیگ",
        ["sing-box زودتر خارج شد (exit code {0}) — کانفیگ را بررسی کنید"] = "اتصال V2Ray ناموفق",
        ["interface TunnelX-V2Ray ظاهر نشد (timeout {0}s)"] = "آداپتر VPN بالا نیامد",
        ["خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}"] = "خطا در اسپلیت‌تانلینگ",
        ["تأیید مسیر تونل ناموفق بود"] = "بررسی سلامت ناموفق",
        ["بررسی سلامت ناموفق"] = "بررسی سلامت ناموفق",
        ["اتصال بیش از حد طول کشید و متوقف شد"] = "اتصال بیش از حد طول کشید",
        ["فقط OpenVPN Connect پیدا شد. برای Split Tunneling باید OpenVPN Community (openvpn.exe) هم نصب باشد."] = "OpenVPN نصب نیست",
        ["OpenVPN Community پیدا نشد. برای Split Tunneling باید openvpn.exe نصب باشد."] = "OpenVPN نصب نیست",
        ["OpenVPN Community نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"] = "OpenVPN نصب نیست",
        ["OpenVPN Community نصب نیست؛ ابتدا از لینک رسمی نصب کنید"] = "OpenVPN نصب نیست",
        ["اجرای openvpn.exe ناموفق بود: {0}. OpenVPN Community را نصب کنید یا TunnelX را با Administrator اجرا کنید."] = "اتصال OpenVPN ناموفق",
        ["هیچ سرور remote قابل استفاده در فایل .ovpn باقی نمانده است. آدرس سرور، DNS یا نصب OpenVPN Community را بررسی کنید."] = "اتصال OpenVPN ناموفق",
        ["OpenVPN زودتر از اتصال بسته شد (exit={0})"] = "اتصال OpenVPN ناموفق",
        ["آداپتور OpenVPN بالا نیامد. لاگ OpenVPN را بررسی کنید؛ ممکن است ریموت اول پاسخ ندهد یا احراز هویت/شبکه مشکل داشته باشد."] = "آداپتر VPN بالا نیامد",
        ["TLS OpenVPN کامل نشد. ریموت‌های فایل .ovpn، فیلترینگ شبکه یا نسخه OpenVPN Community را بررسی کنید؛ TunnelX حالت DCO را غیرفعال کرده است."] = "اتصال OpenVPN ناموفق",
        ["پیش‌نیاز: TunnelX باید با دسترسی Administrator اجرا شود."] = "دسترسی Administrator لازم است",
        ["پیش‌نیاز: WinDivert.dll پیدا نشد. TunnelX را با Administrator اجرا کنید؛ در صورت تکرار، نسخه standalone را دوباره نصب کنید یا لاگ [ENGINE] را ارسال کنید."] = "پیش‌نیاز اتصال آماده نیست",
        ["پیش‌نیاز: sing-box.exe پیدا نشد. نسخه standalone TunnelX را دوباره نصب کنید یا لاگ [ENGINE] را برای پشتیبانی ارسال کنید."] = "پیش‌نیاز اتصال آماده نیست",
        ["پیش‌نیاز: wintun.dll برای ساخت آداپتر TunnelX-V2Ray لازم است. TunnelX را با Administrator اجرا کنید؛ VPN/آنتی‌ویروس دیگر را ببندید؛ در ncpa.cpl آداپتر TunnelX-V2Ray گیرکرده را حذف کنید؛ سپس دوباره اتصال بزنید."] = "پیش‌نیاز اتصال آماده نیست",
        ["پیش‌نیاز: این کانفیگ به Xray-core (xhttp) نیاز دارد ولی xray.exe در برنامه موجود نیست. از کانفیگ sing-box (بدون xhttp) استفاده کنید یا نسخه کامل TunnelX را نصب کنید."] = "پیش‌نیاز اتصال آماده نیست",
        ["کانفیگ باید یک sing-box JSON ({…}) یا URI از نوع vmess:// / vless:// / trojan:// / ss:// باشد"] = "خطا در کانفیگ",
    };

    public static string GetShortStatus(
        TunnelType tunnelType,
        string? fullMessage,
        PrerequisiteFailureKind prerequisiteKind = PrerequisiteFailureKind.None)
    {
        var loc = LocalizationService.Instance;

        if (prerequisiteKind != PrerequisiteFailureKind.None)
        {
            var fromPrereq = TryGetShortStatusForPrerequisite(prerequisiteKind);
            if (!string.IsNullOrWhiteSpace(fromPrereq))
                return fromPrereq;
        }

        if (tunnelType == TunnelType.OpenVpn)
        {
            var openVpn = OpenVpnDisconnectInsight.TryGetShortStatusFromUserMessage(fullMessage);
            if (!string.IsNullOrWhiteSpace(openVpn))
                return openVpn;
        }

        var fromKey = TryGetShortStatusFromMessageKey(tunnelType, fullMessage);
        if (!string.IsNullOrWhiteSpace(fromKey))
            return fromKey;

        if (IsCompactStatusMessage(fullMessage))
            return fullMessage!;

        return loc.T(GetGenericFailureShortKey(tunnelType));
    }

    public static string? TryGetShortStatusFromMessageKey(TunnelType tunnelType, string? fullMessage)
    {
        if (string.IsNullOrWhiteSpace(fullMessage))
            return null;

        var loc = LocalizationService.Instance;
        var (resolved, _) = loc.ResolveStatusStorage(fullMessage);

        if (ShortKeyByMessageKey.TryGetValue(resolved, out var shortKey))
            return AdjustShortKeyForTunnelType(tunnelType, resolved, shortKey);

        if (resolved.StartsWith("خطا: {0}", StringComparison.Ordinal) ||
            string.Equals(resolved, "خطا: {0}", StringComparison.Ordinal))
            return loc.T(GetGenericFailureShortKey(tunnelType));

        if (resolved.Contains("WireGuard", StringComparison.Ordinal) &&
            (resolved.Contains("کانفیگ", StringComparison.Ordinal) ||
             resolved.Contains("نامعتبر", StringComparison.Ordinal) ||
             resolved.Contains("Endpoint", StringComparison.Ordinal)))
            return loc.T("خطا در کانفیگ WireGuard");

        return null;
    }

    private static string AdjustShortKeyForTunnelType(TunnelType tunnelType, string resolvedKey, string shortKey)
    {
        if (string.Equals(resolvedKey, "sing-box زودتر خارج شد (exit code {0}) — کانفیگ را بررسی کنید", StringComparison.Ordinal) &&
            tunnelType == TunnelType.SocksProxy)
            return LocalizationService.Instance.T("اتصال پراکسی ناموفق");

        if (string.Equals(resolvedKey, "خطا در پارس کانفیگ: {0}", StringComparison.Ordinal) &&
            tunnelType == TunnelType.SocksProxy)
            return LocalizationService.Instance.T("اتصال پراکسی ناموفق");

        return LocalizationService.Instance.T(shortKey);
    }

    private static string? TryGetShortStatusForPrerequisite(PrerequisiteFailureKind kind) =>
        LocalizationService.Instance.T(kind switch
        {
            PrerequisiteFailureKind.NotElevated => "دسترسی Administrator لازم است",
            PrerequisiteFailureKind.OpenVpnInstall => "OpenVPN نصب نیست",
            PrerequisiteFailureKind.WireGuardInstall => "WireGuard نصب نیست",
            PrerequisiteFailureKind.WintunMissing => "آداپتر VPN بالا نیامد",
            _ => "پیش‌نیاز اتصال آماده نیست"
        });

    private static string GetGenericFailureShortKey(TunnelType tunnelType) => tunnelType switch
    {
        TunnelType.OpenVpn => "اتصال OpenVPN ناموفق",
        TunnelType.WireGuard => "اتصال WireGuard ناموفق",
        TunnelType.L2tpIpsec => "اتصال L2TP ناموفق",
        TunnelType.SocksProxy => "اتصال پراکسی ناموفق",
        TunnelType.V2Ray => "اتصال V2Ray ناموفق",
        _ => "اتصال برقرار نشد"
    };

    private static bool IsCompactStatusMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var (key, _) = LocalizationService.Instance.ResolveStatusStorage(message);
        return !key.Contains('\n') && key.Length <= 56;
    }
}
