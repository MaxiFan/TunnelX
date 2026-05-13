using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// ITunnelProvider implementation for OpenVPN.
/// Launches OpenVPN Connect (or community openvpn.exe if Connect is not installed)
/// and waits for its network adapter to come Up.
/// The user manages profiles and credentials inside the OpenVPN Connect UI —
/// TunnelX only controls traffic routing on top of the established tunnel.
/// </summary>
public class OpenVpnTunnelProvider : ITunnelProvider
{
    private Process? _process;
    private int _vpnInterfaceIndex = -1;

    public ConnectionStatus Status { get; } = new();

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Connecting;
        Status.Message = "در حال اجرای OpenVPN Connect...";
        Logger.Info("[OpenVPN] ConnectAsync started");

        try
        {
            var openVpnExe = ResolveOpenVpnExecutable(config);
            if (string.IsNullOrWhiteSpace(openVpnExe))
            {
                Status.State = ConnectionState.Error;
                Status.Message = "OpenVPN Connect پیدا نشد. لطفاً نصب کنید.";
                Logger.Error("[OpenVPN] Executable not found. Searched:");
                foreach (var p in GetCandidatePaths(config))
                    Logger.Error($"  '{p}' → {(File.Exists(p) ? "FOUND" : "not found")}");
                return false;
            }
            Logger.Info($"[OpenVPN] Launching: {openVpnExe}");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = openVpnExe,
                    UseShellExecute = true
                },
                EnableRaisingEvents = true
            };

            _process.Start();
            Logger.Info($"[OpenVPN] Process started PID={_process.Id}");

            Status.Message = "لطفاً در OpenVPN Connect روی Connect کلیک کنید...";
            Logger.Info("[OpenVPN] Waiting up to 90s for VPN adapter to come Up...");

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var idx = FindOpenVpnInterfaceIndex();
                if (idx > 0)
                {
                    Logger.Info($"[OpenVPN] Adapter came Up: index={idx}");
                    _vpnInterfaceIndex = idx;
                    break;
                }

                var remaining = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                Status.Message = $"لطفاً در OpenVPN Connect روی Connect کلیک کنید... ({remaining}s)";
                await Task.Delay(500, ct);
            }

            if (_vpnInterfaceIndex <= 0)
            {
                Logger.Error("[OpenVPN] Adapter not found after timeout. Current NICs:");
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    Logger.Error($"  name='{nic.Name}' desc='{nic.Description}' status={nic.OperationalStatus}");
                Status.State = ConnectionState.Error;
                Status.Message = "آداپتور OpenVPN بالا نیامد. آیا در OpenVPN Connect متصل شدید؟";
                await KillProcessAsync();
                return false;
            }

            Status.State = ConnectionState.Connected;
            Status.ConnectedSince = DateTime.Now;
            Status.VpnInterfaceIndex = _vpnInterfaceIndex;
            Status.VpnLocalIp = GetInterfaceIpv4(_vpnInterfaceIndex);
            Status.VpnServerIp = "OpenVPN";
            Status.Message = "OpenVPN connected";
            Logger.Info($"[OpenVPN] Connected. LocalIP={Status.VpnLocalIp}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State = ConnectionState.Disconnected;
            Status.Message = "اتصال لغو شد";
            await KillProcessAsync();
            return false;
        }
        catch (Exception ex)
        {
            Status.State = ConnectionState.Error;
            Status.Message = $"خطا: {ex.Message}";
            Logger.Error("OpenVpnTunnelProvider.ConnectAsync failed", ex);
            await KillProcessAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        Status.State = ConnectionState.Disconnecting;
        Status.Message = "در حال قطع اتصال OpenVPN...";
        await KillProcessAsync();
        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Disconnected;
        Status.ConnectedSince = null;
        Status.VpnLocalIp = string.Empty;
        Status.VpnInterfaceIndex = -1;
        Status.Message = "قطع شد";
    }

    public bool IsInterfaceUp()
    {
        if (_vpnInterfaceIndex < 0) return false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == _vpnInterfaceIndex)
                    return nic.OperationalStatus == OperationalStatus.Up;
            }
        }
        catch { }
        return false;
    }

    private async Task KillProcessAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }

    private static string? ResolveOpenVpnExecutable(ServerConfig config)
    {
        foreach (var c in GetCandidatePaths(config))
        {
            Logger.Debug($"[OpenVPN] Checking: '{c}' -> {(File.Exists(c) ? "FOUND" : "not found")}");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static IEnumerable<string> GetCandidatePaths(ServerConfig config)
    {
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(config.OpenVpnExePath))
            yield return config.OpenVpnExePath;

        yield return Path.Combine(pf,    "OpenVPN Connect", "OpenVPNConnect.exe");
        yield return Path.Combine(pfx86, "OpenVPN Connect", "OpenVPNConnect.exe");
        yield return Path.Combine(local, "Programs", "OpenVPN Connect", "OpenVPNConnect.exe");
        yield return Path.Combine(local, "TunnelX", "openvpn.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "openvpn.exe");
        yield return Path.Combine(pf,    "OpenVPN", "bin", "openvpn.exe");
        yield return Path.Combine(pfx86, "OpenVPN", "bin", "openvpn.exe");
    }

    private static int FindOpenVpnInterfaceIndex()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var match =
                nic.Name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Data Channel Offload", StringComparison.OrdinalIgnoreCase);

            if (!match) continue;

            var ipv4 = nic.GetIPProperties().GetIPv4Properties();
            if (ipv4 != null) return ipv4.Index;
        }
        return -1;
    }

    private static string GetInterfaceIpv4(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                if (ipv4 == null || ipv4.Index != interfaceIndex) continue;
                return props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address.ToString() ?? "N/A";
            }
        }
        catch { }
        return "N/A";
    }
}
