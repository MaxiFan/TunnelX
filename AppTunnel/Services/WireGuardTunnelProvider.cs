using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// WireGuard provider that delegates tunnel creation to WireGuard for Windows.
///
/// This intentionally exposes a real Windows adapter, matching OpenVPN/L2TP.
/// TunnelX can then reuse TrafficRouterService for app split-tunneling instead
/// of mixing WinDivert packet rewriting with sing-box's internal WireGuard TUN.
/// </summary>
public class WireGuardTunnelProvider : ITunnelProvider
{
    public const string TunnelServiceName = "TunnelX-WireGuard";
    private const string TunnelName = TunnelServiceName;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _workDir;
    private readonly string _configPath;
    private string _wireGuardExe = "";
    private int _vpnInterfaceIndex = -1;
    private string _vpnLocalIp = "";

    public ConnectionStatus Status { get; } = new();

    // Kept for VpnService parity. The WireGuard service is monitored through
    // IsInterfaceUp(), not through a child process lifetime.
    public Action? OnTunnelFailed { get; set; }

    public WireGuardTunnelProvider()
    {
        _workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TunnelX",
            "wireguard");
        _configPath = Path.Combine(_workDir, $"{TunnelName}.conf");
    }

    public static Task CleanupTunnelXServiceAsync()
        => TunnelXCleanupService.CleanupWireGuardAsync();

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        Status.State = ConnectionState.Connecting;
        Status.Message = LocalizationService.Instance.T("در حال آماده‌سازی WireGuard...");
        Logger.Info("[WireGuard] ConnectAsync started (WireGuard for Windows adapter mode)");

        try
        {
            Directory.CreateDirectory(_workDir);

            _wireGuardExe = ResolveWireGuardExecutable();
            if (string.IsNullOrWhiteSpace(_wireGuardExe))
            {
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.T("WireGuard for Windows نصب نیست. برای استفاده از WireGuard Split Tunnel، ابتدا WireGuard رسمی ویندوز را نصب کنید.");
                Logger.Error("[WireGuard] wireguard.exe not found. Install WireGuard for Windows from https://www.wireguard.com/install/");
                return false;
            }

            if (!WireGuardConfigParser.TryParse(config.WireGuardConfig, out var profile, out var parseError))
            {
                Status.State = ConnectionState.Error;
                Status.Message = parseError;
                Logger.Error($"[WireGuard] Config parse failed: {parseError}");
                return false;
            }

            var tunnelAddress = SelectTunnelAddress(profile);
            _vpnLocalIp = ExtractAddressIp(tunnelAddress);
            LogIgnoredDnsServers(profile);
            var preparedConfig = BuildWindowsWireGuardConfig(profile, tunnelAddress, disableTable: true);
            await File.WriteAllTextAsync(_configPath, preparedConfig, Utf8NoBom, ct);

            await UninstallTunnelServiceAsync(ignoreErrors: true, ct);

            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Active, "راه‌اندازی سرویس WireGuard");
            Status.Message = LocalizationService.Instance.T("در حال اجرای سرویس WireGuard...");
            var install = await RunWireGuardAsync($"/installtunnelservice \"{_configPath}\"", TimeSpan.FromSeconds(20), ct);
            var installedWithoutTableOff = false;
            if (install.ExitCode != 0 && WireGuardSplitRouteOptions.LooksLikeUnsupportedTableOption(install.Output, install.Error))
            {
                Logger.Warning("[WireGuard] Table=off was rejected by wireguard.exe; retrying without Table=off. AllowedIPs 0.0.0.0/0 may install a system default route until TunnelX strips it.");
                preparedConfig = BuildWindowsWireGuardConfig(profile, tunnelAddress, disableTable: false);
                await File.WriteAllTextAsync(_configPath, preparedConfig, Utf8NoBom, ct);
                await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
                install = await RunWireGuardAsync($"/installtunnelservice \"{_configPath}\"", TimeSpan.FromSeconds(20), ct);
                installedWithoutTableOff = install.ExitCode == 0;
            }
            if (install.ExitCode != 0)
            {
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.Format("WireGuard service اجرا نشد: {0}", FirstNonEmptyLine(install.Error, install.Output));
                Logger.Error($"[WireGuard] installtunnelservice failed exit={install.ExitCode} stdout='{install.Output.Trim()}' stderr='{install.Error.Trim()}'");
                await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
                return false;
            }

            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Complete, "راه‌اندازی سرویس WireGuard");
            ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Active, "انتظار برای آداپتر WireGuard");

            var interfaceIndex = await WaitForWireGuardInterfaceAsync(_vpnLocalIp, ct);
            if (interfaceIndex <= 0)
            {
                ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Fail, "آداپتر WireGuard بالا نیامد (timeout)");
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.T("آداپتر WireGuard بالا نیامد (timeout)");
                Logger.Error("[WireGuard] Adapter was not found after service install.");
                await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
                return false;
            }

            _vpnInterfaceIndex = interfaceIndex;
            if (installedWithoutTableOff)
            {
                var stripped = VpnDefaultRouteInspector.DeleteDefaultRoutesOnInterface(interfaceIndex);
                Logger.Warning($"[ROUTE] WireGuard without Table=off: stripped {stripped} 0.0.0.0/0 row(s) on IF {interfaceIndex} (split mode)");
            }
            else
            {
                var leftover = VpnDefaultRouteInspector.DeleteDefaultRoutesOnInterface(interfaceIndex);
                if (leftover > 0)
                    Logger.Warning($"[ROUTE] WireGuard Table=off still left {leftover} 0.0.0.0/0 row(s) on IF {interfaceIndex}; stripped");
            }
            ConnectionProgressService.Report(
                "tun_interface",
                ConnectionProgressPhase.Complete,
                "انتظار برای آداپتر WireGuard",
                "آداپتر WireGuard آماده شد (شماره {0})",
                interfaceIndex.ToString());
            var serverIp = profile.EndpointHost;
            if (!IPAddress.TryParse(serverIp, out _))
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dnsCts.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var resolved = await DnsResolverCache.ResolveFirstIpv4Async(serverIp, dnsCts.Token);
                    if (resolved != null)
                        serverIp = resolved.ToString();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[WireGuard] Endpoint resolve timed out/failed for '{profile.EndpointHost}': {ex.Message}");
                }
            }

            Status.State = ConnectionState.Connected;
            Status.ConnectedSince = DateTime.Now;
            Status.VpnLocalIp = _vpnLocalIp;
            Status.VpnServerHost = profile.EndpointHost;
            Status.VpnServerIp = serverIp;
            Status.VpnServerPort = profile.EndpointPort;
            Status.VpnGatewayIp = string.Empty;
            Status.VpnInterfaceIndex = interfaceIndex;
            Status.DnsRedirectIp = SelectDnsRedirectIp(profile);
            Status.SingBoxMixedPort = 0;
            Status.Message = LocalizationService.Instance.T("WireGuard متصل شد (Split Tunnel)");
            Logger.Info($"[WireGuard] Connected via Windows adapter. LocalIP={_vpnLocalIp} IF={interfaceIndex} Endpoint={profile.EndpointHost}:{profile.EndpointPort} DnsRedirect={Status.DnsRedirectIp}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State = ConnectionState.Disconnected;
            Status.Message = LocalizationService.Instance.T("اتصال لغو شد");
            await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
            return false;
        }
        catch (Exception ex)
        {
            Status.State = ConnectionState.Error;
            Status.Message = LocalizationService.Instance.Format("خطا: {0}", ex.Message);
            Logger.Error("WireGuardTunnelProvider.ConnectAsync failed", ex);
            await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        Status.State = ConnectionState.Disconnecting;
        Status.Message = LocalizationService.Instance.T("در حال قطع اتصال WireGuard...");

        await UninstallTunnelServiceAsync(ignoreErrors: true, CancellationToken.None);
        TryDelete(_configPath);

        _vpnInterfaceIndex = -1;
        _vpnLocalIp = "";
        Status.State = ConnectionState.Disconnected;
        Status.ConnectedSince = null;
        Status.VpnLocalIp = string.Empty;
        Status.VpnServerHost = string.Empty;
        Status.VpnServerIp = string.Empty;
        Status.VpnServerPort = 0;
        Status.VpnGatewayIp = string.Empty;
        Status.VpnInterfaceIndex = -1;
        Status.DnsRedirectIp = string.Empty;
        Status.SingBoxMixedPort = 0;
        Status.Message = LocalizationService.Instance.T("قطع شد");
    }

    public bool IsInterfaceUp()
    {
        if (_vpnInterfaceIndex <= 0 || string.IsNullOrWhiteSpace(_vpnLocalIp))
            return false;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                if (ipv4?.Index != _vpnInterfaceIndex)
                    continue;

                return nic.OperationalStatus == OperationalStatus.Up &&
                       props.UnicastAddresses.Any(a =>
                           a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                           string.Equals(a.Address.ToString(), _vpnLocalIp, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }

        return false;
    }

    private async Task UninstallTunnelServiceAsync(bool ignoreErrors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_wireGuardExe))
            _wireGuardExe = ResolveWireGuardExecutable();
        if (string.IsNullOrWhiteSpace(_wireGuardExe))
            return;

        var result = await RunWireGuardAsync($"/uninstalltunnelservice {TunnelName}", TimeSpan.FromSeconds(15), ct);
        if (!ignoreErrors && result.ExitCode != 0)
            throw new InvalidOperationException(FirstNonEmptyLine(result.Error, result.Output));

        if (result.ExitCode == 0)
            Logger.Info($"[WireGuard] Removed existing tunnel service '{TunnelName}'");
    }

    private async Task<int> WaitForWireGuardInterfaceAsync(string localIp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var byIp = FindInterfaceIndexByIp(localIp);
            if (byIp > 0)
                return byIp;

            var byName = FindInterfaceIndexByName(TunnelName);
            if (byName > 0)
                return byName;

            await Task.Delay(500, ct);
        }

        return -1;
    }

    private static int FindInterfaceIndexByIp(string localIp)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                if (!props.UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        string.Equals(a.Address.ToString(), localIp, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var ipv4 = props.GetIPv4Properties();
                if (ipv4 != null && ipv4.Index > 0)
                    return ipv4.Index;
            }
        }
        catch { }

        return -1;
    }

    private static int FindInterfaceIndexByName(string name)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index > 0)
                    return ipv4.Index;
            }
        }
        catch { }

        return -1;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunWireGuardAsync(
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _wireGuardExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("wireguard.exe did not start");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, await SafeReadAsync(outputTask), "wireguard.exe timed out");
        }

        return (process.ExitCode, await SafeReadAsync(outputTask), await SafeReadAsync(errorTask));
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; }
        catch { return ""; }
    }

    public static string FindWireGuardExecutable() => ResolveWireGuardExecutable();

    private static string ResolveWireGuardExecutable()
    {
        var candidates = new List<string>();

        var sideBySide = Path.Combine(AppContext.BaseDirectory, "wireguard.exe");
        candidates.Add(sideBySide);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(programFiles, "WireGuard", "wireguard.exe"));
        candidates.Add(Path.Combine(programFilesX86, "WireGuard", "wireguard.exe"));
        candidates.Add(Path.Combine(local, "Programs", "WireGuard", "wireguard.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(dir, "wireguard.exe"));

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string BuildWindowsWireGuardConfig(WireGuardProfile profile, string tunnelAddress, bool disableTable)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Interface]");
        builder.AppendLine($"PrivateKey = {profile.PrivateKey}");
        builder.AppendLine($"Address = {tunnelAddress}");
        if (disableTable)
            builder.AppendLine("Table = off");
        // Do not write DNS into the WireGuard service config. WireGuard for
        // Windows applies it at the system level, while TunnelX is an app-level
        // split tunnel. TrafficRouter DNS redirection also stays disabled for
        // WireGuard because many providers drop plain UDP/53 through the tunnel.
        if (profile.Mtu is > 0)
            builder.AppendLine($"MTU = {profile.Mtu.Value}");
        builder.AppendLine();
        builder.AppendLine("[Peer]");
        builder.AppendLine($"PublicKey = {profile.PeerPublicKey}");
        if (!string.IsNullOrWhiteSpace(profile.PreSharedKey))
            builder.AppendLine($"PresharedKey = {profile.PreSharedKey}");
        builder.AppendLine($"AllowedIPs = {string.Join(", ", profile.AllowedIps)}");
        builder.AppendLine($"Endpoint = {FormatEndpoint(profile.EndpointHost, profile.EndpointPort)}");
        if (profile.PersistentKeepalive is > 0)
            builder.AppendLine($"PersistentKeepalive = {profile.PersistentKeepalive.Value}");

        if (profile.PeerReserved.Count == 3)
            Logger.Warning("[WireGuard] Reserved bytes are not supported by WireGuard for Windows and were omitted from the service config.");

        return builder.ToString();
    }

    private static void LogIgnoredDnsServers(WireGuardProfile profile)
    {
        if (profile.DnsServers.Count == 0)
        {
            Logger.Info("[WireGuard] Config has no DNS servers; using 8.8.8.8 for TunnelX DNS redirect in split mode.");
            return;
        }

        Logger.Info($"[WireGuard] Config DNS not applied to WireGuard service (split mode). TunnelX DNS redirect uses: {SelectDnsRedirectIp(profile)} (from config: {string.Join(", ", profile.DnsServers)})");
    }

    /// <summary>
    /// Pick the DNS server used by TrafficRouter DNS redirect for split-tunnel apps.
    /// WireGuard for Windows must not receive Interface DNS (system-wide), but target
    /// apps still need public DNS routed through the tunnel.
    /// </summary>
    private static string SelectDnsRedirectIp(WireGuardProfile profile)
    {
        foreach (var dns in profile.DnsServers)
        {
            var value = dns.Trim();
            if (IPAddress.TryParse(value, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return ip.ToString();
        }

        return "8.8.8.8";
    }

    private static string SelectTunnelAddress(WireGuardProfile profile)
    {
        foreach (var address in profile.Addresses)
        {
            var value = address.Trim();
            if (value.Length == 0)
                continue;

            var slash = value.IndexOf('/');
            var ipPart = slash >= 0 ? value[..slash] : value;
            if (IPAddress.TryParse(ipPart, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return slash >= 0 ? value : $"{ip}/32";
        }

        throw new FormatException(LocalizationService.Instance.T("کانفیگ WireGuard باید یک Address IPv4 داشته باشد"));
    }

    private static string ExtractAddressIp(string address)
    {
        var slash = address.IndexOf('/');
        return slash >= 0 ? address[..slash] : address;
    }

    private static string FormatEndpoint(string host, int port)
        => IPAddress.TryParse(host, out var ip) &&
           ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]:{port}"
            : $"{host}:{port}";

    private static string FirstNonEmptyLine(params string[] values)
    {
        foreach (var value in values)
        {
            var line = value
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return "unknown error";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
