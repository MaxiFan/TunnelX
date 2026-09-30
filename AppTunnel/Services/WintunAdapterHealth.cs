using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;

namespace AppTunnel.Services;

/// <summary>
/// Best-effort Wintun / TunnelX-V2Ray adapter inventory and leftover bounce.
/// Cannot repair a broken Wintun driver; logs conflicts and tries a short
/// disable/enable of a leftover TunnelX adapter before sing-box opens TUN.
/// </summary>
internal static class WintunAdapterHealth
{
    public const string TunnelXInterfaceName = "TunnelX-V2Ray";

    public static void LogInventory(string reason)
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var relevant = 0;
            var otherWintun = 0;
            foreach (var nic in nics)
            {
                if (!IsRelevant(nic))
                    continue;

                relevant++;
                var ipv4 = TryGetIpv4Index(nic);
                Logger.Info(
                    $"[WINTUN] {reason}: name='{nic.Name}' status={nic.OperationalStatus} " +
                    $"desc='{nic.Description}' ifIdx={ipv4} id={nic.Id}");

                if (!nic.Name.Equals(TunnelXInterfaceName, StringComparison.OrdinalIgnoreCase) &&
                    DescriptionLooksLikeWintun(nic.Description))
                    otherWintun++;
            }

            if (relevant == 0)
            {
                Logger.Info($"[WINTUN] {reason}: no TunnelX-V2Ray or Wintun adapters visible");
                return;
            }

            if (otherWintun > 0)
            {
                Logger.Warning(
                    $"[WINTUN] {reason}: {otherWintun} other Wintun-like adapter(s) present — " +
                    "simultaneous Wintun apps (WireGuard, WARP, Outline, …) can block TunnelX-V2Ray");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[WINTUN] inventory failed ({reason}): {ex.Message}");
        }
    }

    public static async Task PrepareForTunOpenAsync(string reason, CancellationToken ct)
    {
        LogInventory(reason);

        NetworkInterface? leftover = null;
        try
        {
            leftover = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(TunnelXInterfaceName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[WINTUN] leftover lookup failed: {ex.Message}");
        }

        if (leftover == null)
            return;

        Logger.Warning(
            $"[WINTUN] leftover adapter '{leftover.Name}' status={leftover.OperationalStatus} " +
            $"desc='{leftover.Description}' — bouncing admin state before TUN open");

        await TrySetAdminStateAsync(leftover.Name, enabled: false, ct);
        await Task.Delay(400, ct);
        await TrySetAdminStateAsync(leftover.Name, enabled: true, ct);
        await Task.Delay(400, ct);
        LogInventory($"{reason}-after-bounce");
    }

    private static bool IsRelevant(NetworkInterface nic) =>
        nic.Name.Equals(TunnelXInterfaceName, StringComparison.OrdinalIgnoreCase) ||
        nic.Name.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
        nic.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        DescriptionLooksLikeWintun(nic.Description);

    private static bool DescriptionLooksLikeWintun(string? description) =>
        !string.IsNullOrEmpty(description) &&
        (description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("Cloudflare WARP", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("sing-box", StringComparison.OrdinalIgnoreCase));

    private static int TryGetIpv4Index(NetworkInterface nic)
    {
        try
        {
            var ipv4 = nic.GetIPProperties().GetIPv4Properties();
            return ipv4?.Index ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task TrySetAdminStateAsync(string interfaceName, bool enabled, CancellationToken ct)
    {
        var state = enabled ? "ENABLED" : "DISABLED";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface set interface name=\"{interfaceName}\" admin={state}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Logger.Warning($"[WINTUN] netsh admin={state} timed out for '{interfaceName}'");
                return;
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode == 0)
                Logger.Info($"[WINTUN] netsh admin={state} ok for '{interfaceName}'");
            else
                Logger.Warning($"[WINTUN] netsh admin={state} exit={process.ExitCode} stdout={stdout} stderr={stderr}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[WINTUN] netsh admin={state} failed for '{interfaceName}': {ex.Message}");
        }
    }

    internal static string FormatCapturedLogs(StringBuilder captured)
    {
        lock (captured)
            return captured.ToString();
    }

    internal static void AppendCaptured(StringBuilder captured, string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        lock (captured)
        {
            if (captured.Length > 24_000)
                return;
            captured.AppendLine(line);
        }
    }
}
