using System.Globalization;

namespace AppTunnel.Services;

/// <summary>Classified reason for an abrupt OpenVPN session drop (mid-session or final auth failure).</summary>
public enum OpenVpnDisconnectReason
{
    Unknown,
    AuthFailed,
    AuthFailedAfterUnstableControl,
    ServerControlChannelUnstable,
    ServerDisconnectedLikely,
    ProcessExited,
    TlsFailed,
    AdapterTimeout
}

/// <summary>Builds bilingual user messages and English debug log lines for OpenVPN disconnects.</summary>
public static class OpenVpnDisconnectInsight
{
    public static OpenVpnDisconnectReason Classify(
        bool authFailed,
        int controlResets,
        bool hadWsaEacces,
        bool tlsFailed,
        bool processExited)
    {
        if (authFailed)
        {
            if (controlResets >= 2 || hadWsaEacces)
                return OpenVpnDisconnectReason.AuthFailedAfterUnstableControl;
            return OpenVpnDisconnectReason.AuthFailed;
        }

        if (tlsFailed)
            return OpenVpnDisconnectReason.TlsFailed;

        if (controlResets >= 4)
            return OpenVpnDisconnectReason.ServerControlChannelUnstable;

        if (controlResets >= 1 || hadWsaEacces)
            return OpenVpnDisconnectReason.ServerDisconnectedLikely;

        if (processExited)
            return OpenVpnDisconnectReason.ProcessExited;

        return OpenVpnDisconnectReason.Unknown;
    }

    public static (string Key, string? FormatArg) GetMessageTemplate(OpenVpnDisconnectReason reason, int controlResets)
    {
        return reason switch
        {
            OpenVpnDisconnectReason.AuthFailedAfterUnstableControl => (
                "ارتباط VPN از سمت سرور قطع شد. قبل از بسته شدن، کانال کنترل OpenVPN چند بار قطع شد ({0} بار).\n\n" +
                "در پایان سرور احراز هویت را رد کرد (AUTH_FAILED). این معمولاً به معنی اشکال در نام کاربری/رمز نیست، بلکه:\n" +
                "• همان اکانت همزمان روی دستگاه یا برنامه دیگر وصل است\n" +
                "• محدودیت تعداد اتصال همزمان از طرف ارائه‌دهنده\n" +
                "• قطع ناگهانی قبلی — session هنوز روی سرور باز مانده (۳۰–۶۰ ثانیه صبر کنید)\n\n" +
                "اگر مطمئن هستید فقط یک دستگاه وصل است، نام کاربری و رمز را با OpenVPN GUI مقایسه کنید.",
                controlResets.ToString(CultureInfo.InvariantCulture)),

            OpenVpnDisconnectReason.AuthFailed => (
                "احراز هویت OpenVPN رد شد (AUTH_FAILED). نام کاربری و رمز را با همان حساب OpenVPN GUI یکسان کنید.\n\n" +
                "اگر تازه قطع شده یا چند بار reconnect شد، ۳۰–۶۰ ثانیه صبر کنید؛ ممکن است session قبلی روی سرور باز مانده یا محدودیت اتصال همزمان باشد.",
                null),

            OpenVpnDisconnectReason.ServerControlChannelUnstable => (
                "ارتباط از سمت سرور یا شبکه قطع شد. کانال کنترل OpenVPN بارها reset شد ({0} بار).\n\n" +
                "احتمال‌ها:\n" +
                "• بار زیاد یا محدودیت اتصال همزمان روی سرور\n" +
                "• فیلترینگ یا قطع موقت TCP به سرور\n" +
                "• مشکل موقت اپراتور اینترنت\n\n" +
                "چند دقیقه صبر کنید؛ فقط یک برنامه با این اکانت وصل باشد.",
                controlResets.ToString(CultureInfo.InvariantCulture)),

            OpenVpnDisconnectReason.ServerDisconnectedLikely => (
                "ارتباط VPN از سمت سرور قطع شد. کانال کنترل OpenVPN یک‌بار یا چند بار reset شد ({0} بار).\n\n" +
                "ممکن است سرور session را بسته باشد، محدودیت اتصال همزمان باشد، یا شبکه بین شما و سرور ناپایدار باشد. ۳۰–۶۰ ثانیه بعد دوباره Connect بزنید.",
                controlResets.ToString(CultureInfo.InvariantCulture)),

            OpenVpnDisconnectReason.ProcessExited => (
                "فرآیند OpenVPN بسته شد و اتصال VPN قطع شد.\n\n" +
                "اگر مدتی وصل بودید، احتمالاً سرور session را بسته یا کانال کنترل را reset کرده است. لاگ TunnelX را برای [OpenVPN-DROP] بررسی کنید.",
                null),

            OpenVpnDisconnectReason.TlsFailed => (
                "TLS OpenVPN کامل نشد. ریموت‌های فایل .ovpn، فیلترینگ شبکه یا نسخه OpenVPN Community را بررسی کنید؛ TunnelX حالت DCO را غیرفعال کرده است.",
                null),

            OpenVpnDisconnectReason.AdapterTimeout => (
                "آداپتور OpenVPN بالا نیامد. لاگ OpenVPN را بررسی کنید؛ ممکن است ریموت اول پاسخ ندهد یا احراز هویت/شبکه مشکل داشته باشد.",
                null),

            _ => (
                "اتصال VPN به‌طور ناگهانی قطع شد. آداپتور OpenVPN یا فرآیند تونل از کار افتاد.\n\n" +
                "اگر مدتی وصل بودید، احتمالاً سرور session را بسته یا کانال کنترل را reset کرده است. لاگ TunnelX را برای خطوط [OpenVPN-DROP] بررسی کنید.",
                null)
        };
    }

    public static string BuildUserMessage(OpenVpnDisconnectReason reason, int controlResets)
    {
        var (key, formatArg) = GetMessageTemplate(reason, controlResets);
        return string.IsNullOrWhiteSpace(formatArg)
            ? LocalizationService.Instance.T(key)
            : LocalizationService.Instance.Format(key, formatArg);
    }

    public static string BuildDialogTitle(OpenVpnDisconnectReason reason)
    {
        var loc = LocalizationService.Instance;
        return reason is OpenVpnDisconnectReason.AuthFailed
            or OpenVpnDisconnectReason.AuthFailedAfterUnstableControl
            or OpenVpnDisconnectReason.ServerControlChannelUnstable
            or OpenVpnDisconnectReason.ServerDisconnectedLikely
            ? loc.T("قطع اتصال از سرور OpenVPN")
            : loc.T("قطع اتصال VPN");
    }

    public static string? TryGetShortStatusFromUserMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var (resolved, _) = LocalizationService.Instance.ResolveStatusStorage(message);
        if (!LocalizationService.Instance.IsPersianSourceKey(resolved))
            return null;

        foreach (OpenVpnDisconnectReason reason in Enum.GetValues<OpenVpnDisconnectReason>())
        {
            if (reason == OpenVpnDisconnectReason.Unknown)
                continue;

            var (templateKey, _) = GetMessageTemplate(reason, 0);
            if (string.Equals(resolved, templateKey, StringComparison.Ordinal))
                return BuildShortStatus(reason);
        }

        if (string.Equals(resolved, "OpenVPN زودتر از اتصال بسته شد (exit={0})", StringComparison.Ordinal))
            return LocalizationService.Instance.T("اتصال OpenVPN ناموفق");

        return null;
    }

    public static string BuildShortStatus(OpenVpnDisconnectReason reason)
    {
        var loc = LocalizationService.Instance;
        return reason switch
        {
            OpenVpnDisconnectReason.AuthFailedAfterUnstableControl => loc.T("احراز هویت ناموفق"),
            OpenVpnDisconnectReason.AuthFailed => loc.T("احراز هویت ناموفق"),
            OpenVpnDisconnectReason.ServerControlChannelUnstable => loc.T("قطع از سرور — کانال کنترل ناپایدار"),
            OpenVpnDisconnectReason.ServerDisconnectedLikely => loc.T("قطع از سرور OpenVPN"),
            OpenVpnDisconnectReason.ProcessExited => loc.T("فرآیند OpenVPN بسته شد"),
            OpenVpnDisconnectReason.TlsFailed => loc.T("TLS OpenVPN کامل نشد"),
            OpenVpnDisconnectReason.AdapterTimeout => loc.T("آداپتر OpenVPN بالا نیامد"),
            _ => loc.T("اتصال VPN به‌طور غیرمنتظره قطع شد")
        };
    }

    public static string BuildLogLine(
        OpenVpnDisconnectReason reason,
        int controlResets,
        bool hadWsaEacces,
        string remote,
        string localIp,
        int? exitCode,
        string? authUser)
    {
        var userPart = string.IsNullOrWhiteSpace(authUser) ? "" : $" user={authUser}";
        var exitPart = exitCode.HasValue ? $" exitCode={exitCode.Value}" : "";
        return $"[OpenVPN-DROP] reason={reason} controlResets={controlResets} wsaEacces={hadWsaEacces} remote={remote} localIp={localIp}{exitPart}{userPart}";
    }
}
