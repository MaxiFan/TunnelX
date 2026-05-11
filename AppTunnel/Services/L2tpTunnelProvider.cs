using System.Diagnostics;
using System.Net.NetworkInformation;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// ITunnelProvider implementation for Windows built-in L2TP/IPsec (RAS / rasdial).
/// Contains the full connection logic previously hosted in VpnService.
/// </summary>
public class L2tpTunnelProvider : ITunnelProvider
{
    private ServerConfig? _config;
    private int _vpnInterfaceIndex = -1;

    public ConnectionStatus Status { get; } = new();

    // -------------------------------------------------------------------------
    // ITunnelProvider — ConnectAsync
    // -------------------------------------------------------------------------

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        _config = config;
        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Connecting;
        Status.Message = "در حال ایجاد اتصال VPN...";
        Logger.Info($"Starting VPN connection to {config.ServerAddress}");

        try
        {
            var safeName   = SanitizeForPowerShell(config.ConnectionName);
            var safeServer = SanitizeForPowerShell(config.ServerAddress);
            var safePsk    = SanitizeForPowerShell(config.PreSharedKey);

            // Step 1: Disconnect and remove any previous connection with the same name
            try
            {
                await RunProcessAsync("rasdial", $"\"{SanitizeForCmd(config.ConnectionName)}\" /disconnect", ct);
                await Task.Delay(500, ct);
            }
            catch { /* Best effort */ }

            bool removed = false;
            for (int i = 0; i < 5 && !removed; i++)
            {
                await RunPowerShellAsync(
                    $"Remove-VpnConnection -Name '{safeName}' -Force -ErrorAction SilentlyContinue; " +
                    $"Remove-VpnConnection -Name '{safeName}' -AllUserConnection -Force -ErrorAction SilentlyContinue",
                    ct);

                var checkResult = await RunPowerShellAsync(
                    $"$a = Get-VpnConnection -Name '{safeName}' -ErrorAction SilentlyContinue; " +
                    $"$b = Get-VpnConnection -Name '{safeName}' -AllUserConnection -ErrorAction SilentlyContinue; " +
                    $"if ($a -or $b) {{ 'EXISTS' }} else {{ 'GONE' }}",
                    ct);

                if (checkResult.Output.Contains("GONE"))
                {
                    removed = true;
                    break;
                }

                Logger.Warning($"Remove attempt #{i + 1}: profile still exists. Retrying after forcible disconnect...");

                try
                {
                    await RunProcessAsync("rasdial", $"\"{SanitizeForCmd(config.ConnectionName)}\" /disconnect", ct);
                    await RunProcessAsync("rasdial", "/disconnect", ct);
                }
                catch { }

                await Task.Delay(800, ct);
            }

            if (!removed)
            {
                try
                {
                    await RunPowerShellAsync(
                        "$pbk = \"$env:APPDATA\\Microsoft\\Network\\Connections\\Pbk\\rasphone.pbk\"; " +
                        $"if (Test-Path $pbk) {{ (Get-Content $pbk -Raw) -replace '(?ms)\\[{safeName}\\].*?(?=\\[|\\z)','' | Set-Content $pbk }}",
                        ct);
                    var pbkAll = "$env:PROGRAMDATA\\Microsoft\\Network\\Connections\\Pbk\\rasphone.pbk";
                    await RunPowerShellAsync(
                        $"$pbk = \"{pbkAll}\"; if (Test-Path $pbk) {{ (Get-Content $pbk -Raw) -replace '(?ms)\\[{safeName}\\].*?(?=\\[|\\z)','' | Set-Content $pbk }}",
                        ct);
                    Logger.Warning("Forcibly removed profile from rasphone.pbk");
                }
                catch (Exception ex) { Logger.Warning($"pbk cleanup failed: {ex.Message}"); }
            }

            // Step 2: Create L2TP/IPsec VPN connection with split tunneling
            var createCmd =
                $"Add-VpnConnection " +
                $"-Name '{safeName}' " +
                $"-ServerAddress '{safeServer}' " +
                $"-TunnelType L2tp " +
                $"-L2tpPsk '{safePsk}' " +
                $"-AuthenticationMethod MSChapv2 " +
                $"-EncryptionLevel Required " +
                $"-SplitTunneling " +
                $"-Force " +
                $"-RememberCredential";

            var createResult = await RunPowerShellAsync(createCmd, ct);
            if (!createResult.Success)
            {
                Status.State   = ConnectionState.Error;
                Status.Message = $"خطا در ایجاد VPN: {createResult.Error}";
                Logger.Error($"VPN creation failed: {createResult.Error}");
                return false;
            }
            Logger.Info("VPN connection profile created successfully");

            Status.Message = "در حال اتصال به سرور...";

            // Step 3: Connect using rasdial (60-second timeout for L2TP negotiation)
            var connectResult = await RunProcessAsync(
                "rasdial",
                $"\"{SanitizeForCmd(config.ConnectionName)}\" \"{SanitizeForCmd(config.Username)}\" \"{SanitizeForCmd(config.Password)}\"",
                TimeSpan.FromSeconds(60),
                ct);

            if (!connectResult.Success)
            {
                Status.State   = ConnectionState.Error;
                var errorMsg   = MapRasDialError(connectResult.ExitCode, connectResult.Output, connectResult.Error);
                Status.Message = $"خطا: {errorMsg}";
                Logger.Error($"VPN connection failed: ExitCode={connectResult.ExitCode}, Error={errorMsg}");
                return false;
            }
            Logger.Info("VPN connected via rasdial");

            // Step 4: Get VPN interface info
            await Task.Delay(1000, ct);
            var vpnInfo = GetVpnInterfaceInfo(config.ConnectionName);

            _vpnInterfaceIndex = vpnInfo.interfaceIndex;

            Status.State             = ConnectionState.Connected;
            Status.ConnectedSince    = DateTime.Now;
            Status.VpnLocalIp        = vpnInfo.localIp;
            Status.VpnServerIp       = config.ServerAddress;
            Status.VpnInterfaceIndex = vpnInfo.interfaceIndex;
            Status.Message           = $"متصل — IP: {vpnInfo.localIp}";
            Logger.Info($"VPN fully connected. Local IP: {vpnInfo.localIp}, Interface Index: {vpnInfo.interfaceIndex}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State   = ConnectionState.Disconnected;
            Status.Message = "اتصال لغو شد";
            Logger.Warning("VPN connection cancelled by user");
            return false;
        }
        catch (Exception ex)
        {
            Status.State   = ConnectionState.Error;
            Status.Message = $"خطا: {ex.Message}";
            Logger.Error("VPN connection failed with exception", ex);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // ITunnelProvider — DisconnectAsync
    // -------------------------------------------------------------------------

    public async Task DisconnectAsync()
    {
        if (_config == null) return;

        Status.State   = ConnectionState.Disconnecting;
        Status.Message = "در حال قطع اتصال...";

        try
        {
            await RunProcessAsync("rasdial", $"\"{SanitizeForCmd(_config.ConnectionName)}\" /disconnect", default);

            var safeName = SanitizeForPowerShell(_config.ConnectionName);
            await RunPowerShellAsync(
                $"Remove-VpnConnection -Name '{safeName}' -Force -ErrorAction SilentlyContinue",
                default);
        }
        catch
        {
            // Best effort disconnect
        }
        finally
        {
            _vpnInterfaceIndex       = -1;
            Status.State             = ConnectionState.Disconnected;
            Status.ConnectedSince    = null;
            Status.VpnLocalIp        = string.Empty;
            Status.VpnInterfaceIndex = -1;
            Status.Message           = "قطع شد";
        }
    }

    // -------------------------------------------------------------------------
    // ITunnelProvider — IsInterfaceUp
    // -------------------------------------------------------------------------

    public bool IsInterfaceUp()
    {
        if (_vpnInterfaceIndex < 0) return false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                var ipv4  = props.GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == _vpnInterfaceIndex)
                    return nic.OperationalStatus == OperationalStatus.Up;
            }
        }
        catch { }
        return false; // Interface not found
    }

    // -------------------------------------------------------------------------
    // Helpers (private — identical to original VpnService helpers)
    // -------------------------------------------------------------------------

    private (string localIp, int interfaceIndex) GetVpnInterfaceInfo(string connectionName)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                System.Threading.Thread.Sleep(500);

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (!ni.Name.Contains(connectionName, StringComparison.OrdinalIgnoreCase)) continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;

                        var ipv4Props = props.GetIPv4Properties();
                        int idx = ipv4Props?.Index ?? -1;

                        if (idx == -1)
                        {
                            foreach (var ni2 in interfaces)
                            {
                                try
                                {
                                    var p2   = ni2.GetIPProperties();
                                    var p2v4 = p2.GetIPv4Properties();
                                    if (p2v4 != null && p2.UnicastAddresses
                                            .Any(a => a.Address.Equals(addr.Address)))
                                    {
                                        idx = p2v4.Index;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (idx > 0)
                            return (addr.Address.ToString(), idx);
                    }
                }
            }
            catch { }
        }

        return ("N/A", -1);
    }

    private static string MapRasDialError(int exitCode, string output, string error) =>
        exitCode switch
        {
            691 => "نام کاربری یا رمز عبور اشتباه است",
            692 => "پورت یا دستگاه اشغال است",
            718 => "انتظار برای پاسخ سرور به اتمام رسید (زمان‌بر)",
            720 => "پروتکل PPP بین کلاینت و سرور مطابقت ندارد",
            734 => "پروتکل Link Control قطع شد",
            735 => "آدرس درخواستی توسط سرور رد شد",
            742 => "رایانه به اینترنت متصل نیست",
            768 => "رمزنگاری L2TP/IPsec شکست خورد - PSK یا تنظیمات سرور را بررسی کنید",
            769 => "مقصد در دسترس نیست (سرور خاموش یا آدرس اشتباه)",
            781 => "Pre-Shared Key (PSK) اشتباه است",
            787 => "رمزنگاری L2TP شکست خورد",
            800 => "سرور VPN در دسترس نیست",
            809 => "نوع شبکه را نمی‌توان مشخص کرد (فایروال مسدود کرده)",
            812 => "اتصال قبلی باعث تضاد شده",
            _ when !string.IsNullOrWhiteSpace(error)  => error.Trim(),
            _ when !string.IsNullOrWhiteSpace(output) => output.Trim(),
            _ => $"خطای ناشناخته (exit code: {exitCode})"
        };

    private static string SanitizeForPowerShell(string value) => value.Replace("'", "''");

    private static string SanitizeForCmd(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private async Task<ProcessResult> RunPowerShellAsync(string command, CancellationToken ct)
    {
        var bytes   = System.Text.Encoding.Unicode.GetBytes(command);
        var encoded = Convert.ToBase64String(bytes);
        return await RunProcessAsync("powershell",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}", ct);
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            }
        };

        process.Start();

        var outputTask  = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask   = process.StandardError.ReadToEndAsync(ct);
        var processTask = process.WaitForExitAsync(ct);

        var timeoutTask   = Task.Delay(timeout, ct);
        var completedTask = await Task.WhenAny(processTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            try { process.Kill(true); } catch { }
            return new ProcessResult { Success = false, ExitCode = -1, Output = "", Error = "زمان انتظار به اتمام رسید" };
        }

        var output = await outputTask;
        var error  = await errorTask;

        return new ProcessResult
        {
            Success  = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output   = output.Trim(),
            Error    = error.Trim()
        };
    }

    private Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
        => RunProcessAsync(fileName, arguments, TimeSpan.FromSeconds(60), ct);

    private class ProcessResult
    {
        public bool   Success  { get; init; }
        public int    ExitCode { get; init; }
        public string Output   { get; init; } = "";
        public string Error    { get; init; } = "";
    }
}
