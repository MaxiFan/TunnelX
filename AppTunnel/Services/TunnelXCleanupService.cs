using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AppTunnel.Services;

internal static class TunnelXCleanupService
{
    private static readonly SemaphoreSlim CleanupGate = new(1, 1);
    private static readonly string AppDataDir = AppTunnel.App.AppDataDir;
    private static readonly string SingBoxDir = Path.Combine(AppDataDir, "singbox");
    private static readonly string XrayDir = Path.Combine(AppDataDir, "xray");
    private static readonly string OpenVpnDir = Path.Combine(AppDataDir, "openvpn");
    private static readonly string WireGuardDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelX",
        "wireguard");

    private static readonly string OpenVpnPidPath = Path.Combine(OpenVpnDir, "tunnelx-openvpn.pid");

    public static async Task CleanupAllAsync(string reason, CancellationToken ct = default)
    {
        await CleanupGate.WaitAsync(ct);
        try
        {
            Logger.Info($"[CLEANUP] Starting TunnelX cleanup: {reason}");

            await CleanupV2RaySingBoxAsync(ct);
            await CleanupXrayAsync(ct);
            await CleanupOpenVpnAsync(ct);
            await CleanupWireGuardAsync(ct);
            CleanupTempConfigs();
            TunIpv4ConflictCleanup.RemoveStaleLeftovers(reason);

            Logger.Info("[CLEANUP] TunnelX cleanup finished");
        }
        finally
        {
            CleanupGate.Release();
        }
    }

    public static async Task CleanupWireGuardAsync(CancellationToken ct = default)
    {
        var wireGuardExe = ResolveWireGuardExecutable();
        if (string.IsNullOrWhiteSpace(wireGuardExe))
            return;

        await RunProcessAsync(
            wireGuardExe,
            "/uninstalltunnelservice TunnelX-WireGuard",
            TimeSpan.FromSeconds(15),
            ct,
            "[CLEANUP][WireGuard]");
    }

    private static async Task CleanupV2RaySingBoxAsync(CancellationToken ct)
    {
        var configPath = Path.Combine(SingBoxDir, "config.json");
        await KillProcessesByCommandLineAsync(
            ["sing-box.exe"],
            [NormalizeForQuery(configPath), NormalizeForQuery(SingBoxDir)],
            ct);
    }

    private static async Task CleanupXrayAsync(CancellationToken ct)
    {
        await KillProcessesByCommandLineAsync(
            ["xray.exe", "sing-box.exe"],
            [
                NormalizeForQuery(Path.Combine(XrayDir, "xray-config.json")),
                NormalizeForQuery(Path.Combine(XrayDir, "tun-bridge.json")),
                NormalizeForQuery(XrayDir)
            ],
            ct);
    }

    private static async Task CleanupOpenVpnAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(OpenVpnPidPath))
                return;

            var raw = await File.ReadAllTextAsync(OpenVpnPidPath, ct);
            if (!int.TryParse(raw.Trim(), out var pid))
            {
                TryDelete(OpenVpnPidPath);
                return;
            }

            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                TryDelete(OpenVpnPidPath);
                return;
            }

            if (!process.ProcessName.Equals("openvpn", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"[CLEANUP][OpenVPN] Ignoring stale pid file; PID {pid} is '{process.ProcessName}'");
                TryDelete(OpenVpnPidPath);
                return;
            }

            Logger.Warning($"[CLEANUP][OpenVPN] Killing stale TunnelX OpenVPN PID={pid}");
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); } catch { }
            TryDelete(OpenVpnPidPath);
        }
        catch (ArgumentException)
        {
            TryDelete(OpenVpnPidPath);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CLEANUP][OpenVPN] Cleanup failed: {ex.Message}");
        }
    }

    private static async Task KillProcessesByCommandLineAsync(
        string[] imageNames,
        string[] ownedCommandLineMarkers,
        CancellationToken ct)
    {
        foreach (var processInfo in await QueryProcessesAsync(imageNames, ct))
        {
            ct.ThrowIfCancellationRequested();

            var commandLine = NormalizeForQuery(processInfo.CommandLine);
            if (!ownedCommandLineMarkers.Any(marker =>
                    !string.IsNullOrWhiteSpace(marker) &&
                    commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                using var process = Process.GetProcessById(processInfo.ProcessId);
                if (process.HasExited)
                    continue;

                Logger.Warning($"[CLEANUP] Killing TunnelX-owned {processInfo.Name} PID={processInfo.ProcessId}");
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); } catch { }
            }
            catch (ArgumentException) { }
            catch (Exception ex)
            {
                Logger.Warning($"[CLEANUP] Failed to kill {processInfo.Name} PID={processInfo.ProcessId}: {ex.Message}");
            }
        }
    }

    private static async Task<List<(int ProcessId, string Name, string CommandLine)>> QueryProcessesAsync(
        string[] imageNames,
        CancellationToken ct)
    {
        var results = new List<(int ProcessId, string Name, string CommandLine)>();
        if (imageNames.Length == 0)
            return results;

        var namesLiteral = string.Join(",", imageNames.Select(n => $"'{n.Replace("'", "''")}'"));
        var command =
            "$names=@(" + namesLiteral + "); " +
            "Get-CimInstance Win32_Process | " +
            "Where-Object { $names -contains $_.Name } | " +
            "Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return results;

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return results;
            }

            var output = await outputTask;
            if (string.IsNullOrWhiteSpace(output))
                return results;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                    TryAddProcessInfo(item, results);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                TryAddProcessInfo(doc.RootElement, results);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CLEANUP] Process query failed: {ex.Message}");
        }

        return results;
    }

    private static void TryAddProcessInfo(
        JsonElement item,
        List<(int ProcessId, string Name, string CommandLine)> results)
    {
        if (!item.TryGetProperty("ProcessId", out var pidElement) ||
            !pidElement.TryGetInt32(out var pid))
            return;

        var name = item.TryGetProperty("Name", out var nameElement)
            ? nameElement.GetString() ?? ""
            : "";
        var commandLine = item.TryGetProperty("CommandLine", out var cmdElement)
            ? cmdElement.GetString() ?? ""
            : "";

        results.Add((pid, name, commandLine));
    }

    private static async Task RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct,
        string logPrefix)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }

            if (process.ExitCode == 0)
                Logger.Info($"{logPrefix} command completed: {Path.GetFileName(fileName)} {arguments}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"{logPrefix} command failed: {ex.Message}");
        }
    }

    private static void CleanupTempConfigs()
    {
        TryDelete(Path.Combine(SingBoxDir, "config.json"));
        TryDelete(Path.Combine(XrayDir, "xray-config.json"));
        TryDelete(Path.Combine(XrayDir, "tun-bridge.json"));
        TryDelete(Path.Combine(WireGuardDir, "TunnelX-WireGuard.conf"));
    }

    private static string ResolveWireGuardExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard", "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WireGuard", "wireguard.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "";
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

    private static string NormalizeForQuery(string? value)
        => (value ?? "").Replace('/', '\\').Trim();

}
