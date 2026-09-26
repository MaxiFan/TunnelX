using System.IO;
using System.Security.Principal;
using AppTunnel.Models;

namespace AppTunnel.Services;

public enum PrerequisiteFailureKind
{
    None,
    NotElevated,
    WinDivertMissing,
    WintunMissing,
    SingBoxMissing,
    XrayMissing,
    OpenVpnInstall,
    WireGuardInstall,
    Generic
}

public sealed class TunnelPrerequisiteResult
{
    public bool Ok { get; init; }
    public string UserMessage { get; init; } = "";
    public PrerequisiteFailureKind FailureKind { get; init; } = PrerequisiteFailureKind.None;
}

/// <summary>
/// Verifies tunnel-type prerequisites before connect, auto-repairs bundled files when possible,
/// and returns localized guidance when repair fails.
/// </summary>
public static class TunnelPrerequisiteService
{
    public static async Task<TunnelPrerequisiteResult> EnsureReadyAsync(
        ServerConfig config,
        CancellationToken ct = default)
    {
        var loc = LocalizationService.Instance;
        Logger.Info($"[PREREQ] Checking prerequisites for {config.TunnelType}");

        EnsureAppNativeLibsExtracted();

        if (!IsProcessElevated())
        {
            Logger.Warning("[PREREQ] Process is not elevated (Administrator)");
            return Fail(
                loc.T("پیش‌نیاز: TunnelX باید با دسترسی Administrator اجرا شود."),
                PrerequisiteFailureKind.NotElevated);
        }

        if (!NativeEngineSupport.EnsureWinDivertPairReady())
        {
            Logger.Error("[PREREQ] WinDivert.dll/WinDivert64.sys unavailable after repair attempt");
            return Fail(
                loc.T("پیش‌نیاز: WinDivert.dll پیدا نشد. TunnelX را با Administrator اجرا کنید؛ در صورت تکرار، نسخه standalone را دوباره نصب کنید یا لاگ [ENGINE] را ارسال کنید."),
                PrerequisiteFailureKind.WinDivertMissing);
        }

        return config.TunnelType switch
        {
            TunnelType.V2Ray => await EnsureV2RayReadyAsync(config.V2RayConfig, ct),
            TunnelType.SocksProxy => await EnsureV2RayReadyAsync(
                config.BuildProxyUri(),
                ct),
            TunnelType.OpenVpn => EnsureOpenVpnReady(),
            TunnelType.WireGuard => EnsureWireGuardReady(),
            TunnelType.L2tpIpsec => Ok(loc.T("پیش‌نیازهای L2TP/IPsec آماده است.")),
            _ => Fail(loc.Format("نوع تانل ناشناخته: {0}", config.TunnelType), PrerequisiteFailureKind.Generic)
        };
    }

    private static void EnsureAppNativeLibsExtracted() =>
        NativeEngineSupport.EnsureAppNativeLibsExtracted();

    private static async Task<TunnelPrerequisiteResult> EnsureV2RayReadyAsync(
        string v2RayConfig,
        CancellationToken ct)
    {
        var loc = LocalizationService.Instance;
        v2RayConfig = v2RayConfig.Trim();
        if (!IsV2RayConfigRecognized(v2RayConfig))
        {
            Logger.Error("[PREREQ] V2Ray config format not recognized");
            return Fail(
                loc.T("کانفیگ باید یک sing-box JSON ({…}) یا URI از نوع vmess:// / vless:// / trojan:// / ss:// / hysteria:// / hysteria2:// / hy2:// باشد"),
                PrerequisiteFailureKind.Generic);
        }

        var requiresXray = TunnelProviderFactory.RequiresXray(v2RayConfig);
        Logger.Info($"[PREREQ] V2Ray core selection: {(requiresXray ? "Xray-core + sing-box bridge" : "sing-box")}");

        Directory.CreateDirectory(NativeEngineSupport.SingBoxWorkDir);
        if (requiresXray)
            Directory.CreateDirectory(NativeEngineSupport.XrayWorkDir);

        var singBoxExe = NativeEngineSupport.ResolveSingBoxExePath();
        await NativeEngineSupport.EnsureEmbeddedExecutableAsync("sing-box.exe", singBoxExe, ct);
        NativeEngineSupport.EnsureWintunBesideEngine(NativeEngineSupport.SingBoxWorkDir);
        NativeEngineSupport.EnsureWintunBesideExecutable(singBoxExe);

        NativeEngineSupport.LogFileProbe("sing-box.exe", singBoxExe);

        if (!File.Exists(singBoxExe))
        {
            Logger.Error($"[PREREQ] sing-box.exe missing: {singBoxExe}");
            return Fail(
                loc.T("پیش‌نیاز: sing-box.exe پیدا نشد. نسخه standalone TunnelX را دوباره نصب کنید یا لاگ [ENGINE] را برای پشتیبانی ارسال کنید."),
                PrerequisiteFailureKind.SingBoxMissing);
        }

        if (requiresXray)
        {
            var xrayExe = NativeEngineSupport.ResolveXrayExePath();
            await NativeEngineSupport.EnsureEmbeddedExecutableAsync("xray.exe", xrayExe, ct);
            NativeEngineSupport.EnsureWintunBesideEngine(NativeEngineSupport.XrayWorkDir);
            NativeEngineSupport.EnsureWintunBesideExecutable(xrayExe);
            NativeEngineSupport.LogFileProbe("xray.exe", xrayExe);

            if (!File.Exists(xrayExe))
            {
                Logger.Error($"[PREREQ] xray.exe required for xhttp/Xray config but missing: {xrayExe}");
                return Fail(
                    loc.T("پیش‌نیاز: این کانفیگ به Xray-core (xhttp) نیاز دارد ولی xray.exe در برنامه موجود نیست. از کانفیگ sing-box (بدون xhttp) استفاده کنید یا نسخه کامل TunnelX را نصب کنید."),
                    PrerequisiteFailureKind.XrayMissing);
            }
        }

        if (!VerifyWintunBesideEngine(singBoxExe, "sing-box"))
            return WintunFailure(loc);

        if (requiresXray && !VerifyWintunBesideEngine(NativeEngineSupport.XrayWorkDir, "xray work dir"))
            return WintunFailure(loc);

        var core = requiresXray ? "Xray-core + sing-box" : "sing-box";
        LogV2RayReadinessSummary(singBoxExe, requiresXray);
        return Ok(loc.Format("پیش‌نیازهای V2Ray آماده است (هسته: {0}).", core));
    }

    private static bool VerifyWintunBesideEngine(string engineDirectoryOrExe, string label)
    {
        var dir = File.Exists(engineDirectoryOrExe)
            ? Path.GetDirectoryName(engineDirectoryOrExe)
            : engineDirectoryOrExe;
        if (string.IsNullOrWhiteSpace(dir))
            return false;

        var wintunPath = Path.Combine(dir, "wintun.dll");
        NativeEngineSupport.LogFileProbe($"wintun.dll ({label})", wintunPath);
        return File.Exists(wintunPath);
    }

    private static TunnelPrerequisiteResult WintunFailure(LocalizationService loc) =>
        Fail(
            loc.T("پیش‌نیاز: wintun.dll برای ساخت آداپتر TunnelX-V2Ray لازم است. TunnelX را با Administrator اجرا کنید؛ VPN/آنتی‌ویروس دیگر را ببندید؛ در ncpa.cpl آداپتر TunnelX-V2Ray گیرکرده را حذف کنید؛ سپس دوباره اتصال بزنید."),
            PrerequisiteFailureKind.WintunMissing);

    private static void LogV2RayReadinessSummary(string singBoxExe, bool requiresXray)
    {
        Logger.Info($"[PREREQ] readiness sing-box={singBoxExe}");
        if (requiresXray)
            Logger.Info($"[PREREQ] readiness xray={NativeEngineSupport.ResolveXrayExePath()} workDir={NativeEngineSupport.XrayWorkDir}");
        var wintunSource = NativeEngineSupport.ResolveWintunSourcePath();
        if (wintunSource != null)
            NativeEngineSupport.LogFileProbe("wintun.dll (source)", wintunSource);
    }

    private static bool IsV2RayConfigRecognized(string config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return false;

        if (config.StartsWith('{'))
            return true;

        ReadOnlySpan<string> schemes =
        [
            "vmess://", "vless://", "trojan://", "ss://",
            "hysteria2://", "hy2://", "hysteria://",
            "socks5://", "socks://", "http://"
        ];

        foreach (var scheme in schemes)
        {
            if (config.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static TunnelPrerequisiteResult EnsureOpenVpnReady()
    {
        var loc = LocalizationService.Instance;
        var exe = OpenVpnTunnelProvider.FindOpenVpnExecutable();
        if (!string.IsNullOrWhiteSpace(exe))
        {
            NativeEngineSupport.LogFileProbe("openvpn.exe", exe);
            Logger.Info("[PREREQ] OpenVPN Community ready");
            return Ok(loc.T("پیش‌نیاز OpenVPN Community آماده است."));
        }

        Logger.Error("[PREREQ] openvpn.exe not found");
        var onlyConnect = Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "OpenVPN Connect")) ||
            Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "OpenVPN Connect"));

        var message = onlyConnect
            ? loc.T("فقط OpenVPN Connect پیدا شد. برای Split Tunneling باید OpenVPN Community (openvpn.exe) هم نصب باشد.")
            : loc.T("OpenVPN Community پیدا نشد. برای Split Tunneling باید openvpn.exe نصب باشد.");

        return Fail(message, PrerequisiteFailureKind.OpenVpnInstall);
    }

    private static TunnelPrerequisiteResult EnsureWireGuardReady()
    {
        var loc = LocalizationService.Instance;
        var exe = WireGuardTunnelProvider.FindWireGuardExecutable();
        if (!string.IsNullOrWhiteSpace(exe))
        {
            NativeEngineSupport.LogFileProbe("wireguard.exe", exe);
            Logger.Info("[PREREQ] WireGuard ready");
            return Ok(loc.T("پیش‌نیاز WireGuard آماده است."));
        }

        Logger.Error("[PREREQ] wireguard.exe not found");
        return Fail(
            loc.T("WireGuard رسمی ویندوز نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"),
            PrerequisiteFailureKind.WireGuardInstall);
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static TunnelPrerequisiteResult Ok(string message)
    {
        Logger.Info($"[PREREQ] {message}");
        return new TunnelPrerequisiteResult { Ok = true, UserMessage = message };
    }

    private static TunnelPrerequisiteResult Fail(string message, PrerequisiteFailureKind kind)
    {
        Logger.Error($"[PREREQ] {message}");
        return new TunnelPrerequisiteResult
        {
            Ok = false,
            UserMessage = message,
            FailureKind = kind
        };
    }
}
