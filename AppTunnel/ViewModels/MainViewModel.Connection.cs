using System.Collections.ObjectModel;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    #region Connection Methods

    private async Task ToggleConnectionAsync()
    {
        if (_connectionState == ConnectionState.Connecting)
        {
            // Cancel ongoing connection attempt
            _connectionCts?.Cancel();
            return;
        }

        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        var tunnelType = _selectedProfile?.TunnelType ?? TunnelType.L2tpIpsec;

        // Guard: validate required fields per tunnel type
        if (tunnelType == TunnelType.L2tpIpsec && string.IsNullOrWhiteSpace(ServerAddress))
        {
            Logger.Warning("ConnectAsync: ServerAddress is empty for L2TP");
            StatusText = "آدرس سرور را وارد کنید";
            return;
        }
        if (tunnelType == TunnelType.V2Ray && string.IsNullOrWhiteSpace(_selectedProfile?.V2RayConfig))
        {
            Logger.Warning("ConnectAsync: V2RayConfig is empty");
            StatusText = "کانفیگ V2Ray را وارد کنید";
            return;
        }
        if (!ValidateSocks5Port(out var socksError))
        {
            StatusText = socksError;
            Socks5PortStatusText = socksError;
            return;
        }

        // Save current state before connecting
        SaveCurrentProfileState();
        if (_selectedProfile != null)
        {
            _selectedProfile.LastUsedAt = DateTime.Now;
            SaveProfiles();
        }

        // Create new CancellationTokenSource for this connection attempt
        _connectionCts?.Dispose();
        _connectionCts = new CancellationTokenSource();

        IsBusy = true;
        ConnectionState = ConnectionState.Connecting;
        StatusText = "در حال اتصال...";

        var config = _selectedProfile?.ToServerConfig() ?? new ServerConfig
        {
            ServerAddress = ServerAddress.Trim(),
            Username = Username.Trim(),
            Password = Password,
            PreSharedKey = PreSharedKey,
            AutoTuneMtu = AutoTuneMtu,
            EnableDnsOptimization = IsDnsOptimizationEnabled,
            EnableGameMode = IsGameModeEnabled
        };

        bool success;
        try
        {
            // Register the watchdog callback before connecting so V2RayTunnelProvider
            // can invoke it if sing-box collapses mid-session.
            _vpnService.OnTunnelFailed = () =>
                Application.Current?.Dispatcher.BeginInvoke(() => _ = HandleVpnDroppedAsync());

            success = await _vpnService.ConnectAsync(config, _connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
            ConnectionState = ConnectionState.Disconnected;
            StatusText = "اتصال لغو شد";
            IsBusy = false;
            return;
        }

        if (success)
        {
            ConnectionState = ConnectionState.Connected;
            StatusText = _vpnService.Status.Message;
            VpnIp = _vpnService.Status.VpnLocalIp;
            VpnAdapterName = ResolveInterfaceName(_vpnService.Status.VpnInterfaceIndex);
            _connectionStartTime = DateTime.Now;
            RaiseHealthStatusChanged();

            // Start traffic routing for enabled apps
            var enabledApps = TunnelApps.Where(a => a.IsEnabled).ToList();
            _trafficRouter.ClearTargetApps();
            foreach (var app in enabledApps)
            {
                app.BytesSent = 0;
                app.BytesReceived = 0;
                _trafficRouter.AddTargetApp(app.ExecutableName);
            }

            // Load user's exclude list (domains/IPs to bypass tunnel)
            _trafficRouter.SetExcludedDestinations(ExcludedDestinations);
            _trafficRouter.SetIncludedDestinations(IncludedDestinations);
            _trafficRouter.Socks5Port = Socks5Port;
            _trafficRouter.EnableDnsOptimization = IsDnsOptimizationEnabled;
            _trafficRouter.EnableGameMode = IsGameModeEnabled;

            _trafficRouter.Start(
                _vpnService.Status.VpnInterfaceIndex,
                _vpnService.Status.VpnLocalIp,
                _vpnService.Status.VpnServerIp); // actual proxy/VPN server host, resolved by TrafficRouter

            _vpnHealthCheckCounter = 0;
            _timer.Start();


        }
        else
        {
            ConnectionState = ConnectionState.Error;
            StatusText = _vpnService.Status.Message;
        }

        IsBusy = false;
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        ConnectionState = ConnectionState.Disconnecting;
        StatusText = "در حال قطع اتصال...";

        _timer.Stop();
        _pingCts?.Cancel();
        IsPinging = false;

        // Save connection to history before stopping router
        SaveConnectionToHistory();

        await _trafficRouter.StopAsync();
        await _vpnService.DisconnectAsync();

        ConnectionState = ConnectionState.Disconnected;
        StatusText = "قطع شد";
        VpnIp = "";
        VpnAdapterName = "";
        _isFullRouteEnabled = false;
        OnPropertyChanged(nameof(IsFullRouteEnabled));
        OnPropertyChanged(nameof(FullRouteStatusText));
        RaiseHealthStatusChanged();
        ConnectionDuration = "--:--:--";
        TotalTraffic = "0 B";
        AppTrafficTotal = "0 B";
        OtherTunnelTraffic = "0 B";
        DirectTraffic = "0 B";
        foreach (var app in TunnelApps)
        {
            app.BytesSent = 0;
            app.BytesReceived = 0;
        }
        IsBusy = false;
    }

    /// <summary>
    /// Called from code-behind during window close to reliably disconnect.
    /// </summary>
    public async Task DisconnectAndCleanupAsync()
    {
        if (!IsConnected) return;
        await DisconnectAsync();
    }

    /// <summary>
    /// Called when VPN interface is detected as down. Cleans up and notifies user.
    /// </summary>
    private async Task HandleVpnDroppedAsync()
    {
        if (!IsConnected) return;

        _timer.Stop();
        _pingCts?.Cancel();
        IsPinging = false;

        ConnectionState = ConnectionState.Disconnecting;
        StatusText = "اتصال VPN قطع شد...";

        SaveConnectionToHistory();

        await _trafficRouter.StopAsync();
        // VPN is already gone — just clean up the profile
        try { await _vpnService.DisconnectAsync(); } catch { }

        ConnectionState = ConnectionState.Disconnected;
        StatusText = "اتصال VPN به‌طور غیرمنتظره قطع شد";
        VpnIp = "";
        VpnAdapterName = "";
        _isFullRouteEnabled = false;
        OnPropertyChanged(nameof(IsFullRouteEnabled));
        OnPropertyChanged(nameof(FullRouteStatusText));
        RaiseHealthStatusChanged();
        ConnectionDuration = "--:--:--";
        TotalTraffic = "0 B";
        AppTrafficTotal = "0 B";
        OtherTunnelTraffic = "0 B";
        DirectTraffic = "0 B";
        foreach (var app in TunnelApps)
        {
            app.BytesSent = 0;
            app.BytesReceived = 0;
        }

        Logger.Warning("[VPN-MONITOR] Cleanup complete — UI reset to disconnected state");

        // Bring main window to front so user notices the alert
        if (Application.Current.MainWindow is { } mainWin)
        {
            if (mainWin.WindowState == WindowState.Minimized)
                mainWin.WindowState = WindowState.Normal;
            mainWin.Activate();
        }

        Helpers.DialogService.Warning(
            "اتصال VPN به‌طور غیرمنتظره قطع شد.\nلطفاً دوباره متصل شوید.",
            "قطع اتصال");
    }

    private int _vpnHealthCheckCounter;

    private void UpdateTimerTick()
    {
        if (!IsConnected) return;

        // Check VPN interface health every 5 seconds
        if (++_vpnHealthCheckCounter >= 5)
        {
            _vpnHealthCheckCounter = 0;
            if (!_trafficRouter.IsVpnInterfaceUp())
            {
                Logger.Warning("[VPN-MONITOR] VPN interface is down — triggering auto-disconnect");
                _ = HandleVpnDroppedAsync();
                return;
            }
        }

        ConnectionDuration = _vpnService.Status.Duration;

        // Update per-app traffic stats (for the apps-tab list).
        foreach (var app in TunnelApps)
        {
            var (sent, received) = _trafficRouter.GetTraffic(app.ExecutableName);
            app.BytesSent = sent;
            app.BytesReceived = received;
        }

        // Total tunnel usage: use the authoritative VPN-interface counter,
        // and also expose the attributed per-app sum so UI can show both
        // values without ambiguity.
        var (totalSent, totalReceived) = _trafficRouter.GetTotalVpnTraffic();
        long vpnTotal = totalSent + totalReceived;
        TotalTraffic = FormatBytes(vpnTotal);

        var (trackedSent, trackedReceived) = _trafficRouter.GetTrackedAppsTraffic();
        long trackedTotal = trackedSent + trackedReceived;
        AppTrafficTotal = FormatBytes(trackedTotal);
        OtherTunnelTraffic = FormatBytes(Math.Max(0, vpnTotal - trackedTotal));

        var (directSent, directReceived) = _trafficRouter.GetDirectTraffic();
        DirectTraffic = FormatBytes(directSent + directReceived);
        RaiseHealthStatusChanged();
    }

    private void OnTrafficUpdated(string exeName, long sent, long received)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var app = TunnelApps.FirstOrDefault(a =>
                a.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
            if (app != null)
            {
                app.BytesSent = sent;
                app.BytesReceived = received;
            }
        });
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    private static string ResolveInterfaceName(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == interfaceIndex)
                    return nic.Name;
            }
        }
        catch { }

        return interfaceIndex > 0 ? $"IF {interfaceIndex}" : "-";
    }

    #endregion

    #region Pre-connect server test

    private async Task TestServerPingAsync()
    {
        if (IsTestingServerPing) return;

        IsTestingServerPing = true;
        ServerPingResult = "در حال تست...";

        try
        {
            if (CurrentTunnelType == TunnelType.L2tpIpsec)
            {
                var host = ServerAddress.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    ServerPingResult = "آدرس سرور خالی است";
                    return;
                }

                using var ping = new Ping();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(host, 3000);
                sw.Stop();
                ServerPingResult = reply.Status == IPStatus.Success
                    ? $"ICMP {reply.RoundtripTime} ms"
                    : $"ICMP {reply.Status}";
                return;
            }

            var rawConfig = SelectedV2RayConfig.Trim();
            if (!TryExtractProxyEndpointDetails(rawConfig, out var endpoint, out var error))
            {
                ServerPingResult = error;
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ms = await MeasureEndpointLatencyAsync(endpoint, cts.Token);
            var mode = endpoint.UseTls ? "TLS handshake" : "TCP connect";
            ServerPingResult = $"{mode} {endpoint.Server}:{endpoint.Port}  {ms} ms";
        }
        catch (OperationCanceledException)
        {
            ServerPingResult = "timeout";
        }
        catch (Exception ex)
        {
            ServerPingResult = $"خطا: {ex.Message}";
        }
        finally
        {
            IsTestingServerPing = false;
        }
    }

    private readonly record struct ProxyEndpoint(string Server, int Port, bool UseTls, string? Sni);

    private static async Task<long> MeasureEndpointLatencyAsync(ProxyEndpoint endpoint, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tcp.ConnectAsync(endpoint.Server, endpoint.Port, ct);

        if (endpoint.UseTls)
        {
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(endpoint.Sni) ? endpoint.Server : endpoint.Sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            };
            await ssl.AuthenticateAsClientAsync(options, ct);
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static bool TryExtractProxyEndpoint(string config, out string server, out int port, out string error)
    {
        if (TryExtractProxyEndpointDetails(config, out var endpoint, out error))
        {
            server = endpoint.Server;
            port = endpoint.Port;
            return true;
        }

        server = "";
        port = 443;
        return false;
    }

    private static bool TryExtractProxyEndpointDetails(string config, out ProxyEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(config))
        {
            error = "کانفیگ خالی است";
            return false;
        }

        try
        {
            if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = config["vmess://".Length..].Split('#')[0];
                var json = TryBase64DecodeConfig(b64);
                var node = JsonNode.Parse(json)?.AsObject();
                var server = node?["add"]?.GetValue<string>() ?? "";
                var port = int.TryParse(node?["port"]?.ToString(), out var p) ? p : 443;
                var useTls = string.Equals(node?["tls"]?.ToString(), "tls", StringComparison.OrdinalIgnoreCase);
                var sni = node?["sni"]?.GetValue<string>() ?? node?["host"]?.GetValue<string>();
                endpoint = new ProxyEndpoint(server, port, useTls, sni);
                return ValidateEndpoint(server, port, out error);
            }

            if (config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                config.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(config.Split('#')[0]);
                var query = ParseQuery(uri.Query);
                var useTls = query.TryGetValue("security", out var security) &&
                             security.Contains("tls", StringComparison.OrdinalIgnoreCase);
                var sni = query.TryGetValue("sni", out var sniValue) ? sniValue : null;
                endpoint = new ProxyEndpoint(uri.Host, uri.Port > 0 ? uri.Port : 443, useTls, sni);
                return ValidateEndpoint(endpoint.Server, endpoint.Port, out error);
            }

            if (config.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryExtractShadowsocksEndpoint(config, out var server, out var port, out error))
                    return false;
                endpoint = new ProxyEndpoint(server, port, false, null);
                return true;
            }

            if (config.StartsWith("{"))
            {
                var root = JsonNode.Parse(config)?.AsObject();
                if (root?["outbounds"] is JsonArray outbounds)
                {
                    foreach (var item in outbounds.OfType<JsonObject>())
                    {
                        var server = item["server"]?.GetValue<string>() ??
                                     item["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>() ??
                                     item["settings"]?["servers"]?[0]?["address"]?.GetValue<string>() ?? "";
                        var port = item["server_port"]?.GetValue<int>() ??
                                   item["settings"]?["vnext"]?[0]?["port"]?.GetValue<int>() ??
                                   item["settings"]?["servers"]?[0]?["port"]?.GetValue<int>() ?? 443;
                        if (!string.IsNullOrWhiteSpace(server))
                        {
                            var tlsNode = item["tls"]?.AsObject();
                            var streamSettings = item["streamSettings"]?.AsObject();
                            var useTls =
                                tlsNode?["enabled"]?.GetValue<bool>() == true ||
                                string.Equals(streamSettings?["security"]?.ToString(), "tls", StringComparison.OrdinalIgnoreCase);
                            var sni = tlsNode?["server_name"]?.GetValue<string>() ??
                                      streamSettings?["tlsSettings"]?["serverName"]?.GetValue<string>();
                            endpoint = new ProxyEndpoint(server, port, useTls, sni);
                            return ValidateEndpoint(server, port, out error);
                        }
                    }
                }
            }

            error = "endpoint سرور از کانفیگ تشخیص داده نشد";
            return false;
        }
        catch (Exception ex)
        {
            error = $"پارس کانفیگ ناموفق بود: {ex.Message}";
            return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
            result[key] = value;
        }
        return result;
    }

    private static bool TryExtractShadowsocksEndpoint(string config, out string server, out int port, out string error)
    {
        server = "";
        port = 443;
        error = "";

        var noFragment = config.Split('#')[0];
        var uri = new Uri(noFragment);
        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            server = uri.Host;
            port = uri.Port > 0 ? uri.Port : 443;
            return ValidateEndpoint(server, port, out error);
        }

        var encoded = noFragment["ss://".Length..];
        var decoded = TryBase64DecodeConfig(encoded);
        var atIdx = decoded.LastIndexOf('@');
        var hostPart = atIdx >= 0 ? decoded[(atIdx + 1)..] : decoded;
        var colonIdx = hostPart.LastIndexOf(':');
        if (colonIdx < 0)
        {
            error = "آدرس ss:// نامعتبر است";
            return false;
        }

        server = hostPart[..colonIdx];
        port = int.TryParse(hostPart[(colonIdx + 1)..], out var p) ? p : 443;
        return ValidateEndpoint(server, port, out error);
    }

    private static bool ValidateEndpoint(string server, int port, out string error)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            error = "سرور پیدا نشد";
            return false;
        }
        if (port <= 0 || port > 65535)
        {
            error = "پورت نامعتبر است";
            return false;
        }

        error = "";
        return true;
    }

    private static string TryBase64DecodeConfig(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    #endregion

    #region Ping

    private CancellationTokenSource? _pingCts;

    private void TogglePing()
    {
        if (IsPinging)
        {
            _pingCts?.Cancel();
            IsPinging = false;
            return;
        }

        var raw = PingTarget?.Trim() ?? "";
        // Accept IP or hostname:port — extract host for route installation
        var host = raw.Contains(':') ? raw.Split(':')[0] : raw;
        if (string.IsNullOrWhiteSpace(host))
        {
            PingResult = "آدرس نامعتبر";
            return;
        }

        IsPinging = true;
        PingResult = "...";
        _pingCts = new CancellationTokenSource();

        // For V2Ray mode, ping through sing-box's own mixed SOCKS5 inbound.
        // sing-box only replies CONNECT_OK after the real upstream TCP handshake
        // through the proxy chain completes → accurate end-to-end RTT.
        // For L2TP, the TunnelX SOCKS5 proxy (bound to VPN IP) gives accurate
        // RTT because L2TP routes packets through the real PPP adapter without
        // a fake local handshake.
        int pingProxyPort = _vpnService.Status.SingBoxMixedPort > 0
            ? _vpnService.Status.SingBoxMixedPort
            : _trafficRouter.Socks5Port;

        _ = RunPingLoopAsync(host, raw, pingProxyPort, _pingCts.Token);
    }

    /// <summary>
    /// TCP-connect latency loop. Uses a SOCKS5 CONNECT through the appropriate
    /// proxy to measure true end-to-end round-trip:
    ///   • V2Ray mode → sing-box mixed SOCKS5 inbound (port 2080).
    ///     sing-box only replies CONNECT_OK after the real upstream TCP handshake
    ///     through the proxy chain completes.  The TUN path is NOT used here
    ///     because the TUN's "system" stack completes the local TCP handshake
    ///     immediately (1-2 ms) before the remote connection is established.
    ///   • L2TP mode  → TunnelX built-in SOCKS5 (port 1080), bound to VPN IP.
    ///     Packets route through the real PPP adapter; no fake local handshake.
    /// Format of <paramref name="target"/>: "host" or "host:port" (default port 443).
    /// </summary>
    private async Task RunPingLoopAsync(string host, string target, int proxyPort, CancellationToken ct)
    {
        // Parse optional port from "host:port"
        int port = 443;
        if (target.Contains(':') && int.TryParse(target.Split(':')[^1], out var p))
            port = p;

        int socks5Port = proxyPort;

        int sent = 0, success = 0;

        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                sent++;
                try
                {
                    long ms = await PingViaSocks5Async(host, port, socks5Port, ct);
                    success++;
                    PingResult = $"TCP {ms} ms  ({success}/{sent})";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    PingResult = $"✗ timeout  ({success}/{sent})";
                }
                catch (Exception ex)
                {
                    PingResult = $"✗ {ex.Message}  ({success}/{sent})";
                }

                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsPinging = false;
            if (sent > 0)
                PingResult += "  [پایان]";
        }
    }

    /// <summary>
    /// Measures the true end-to-end TCP round-trip through the SOCKS5 proxy.
    ///
    /// IMPORTANT: We CANNOT just time the SOCKS5 CONNECT reply — sing-box (and
    /// many modern SOCKS5 implementations) deliberately send the CONNECT success
    /// reply IMMEDIATELY, before dialing the upstream, as a latency optimization.
    /// On loopback this returns in 1-2 ms regardless of the real path.
    ///
    /// Instead we do CONNECT (untimed), then send a probe and time how long
    /// until the FIRST response byte from the upstream server arrives. That gives
    /// us exactly one round-trip through the entire proxy chain to the remote
    /// host. For port 443 we send a minimal TLS ClientHello (server replies with
    /// ServerHello after 1 RTT). For other ports we send an HTTP GET (server
    /// replies with response data or RST after 1 RTT). Either way, time-to-first-
    /// byte is the real RTT.
    /// </summary>
    private static async Task<long> PingViaSocks5Async(
        string host, int port, int socks5Port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);

        using var tcp = new System.Net.Sockets.TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", socks5Port, cts.Token);

        var stream = tcp.GetStream();

        // ── SOCKS5 greeting (untimed — local loopback only) ──
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, cts.Token);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new Exception("SOCKS5 handshake rejected");

        // ── SOCKS5 CONNECT request (untimed — proxy may pre-reply) ──
        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;                              // VER
        req[1] = 0x01;                              // CMD = CONNECT
        req[2] = 0x00;                              // RSV
        req[3] = 0x03;                              // ATYP = DOMAINNAME
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(req, cts.Token);

        // Read SOCKS5 CONNECT response header (4 fixed bytes + bound addr + port).
        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, cts.Token);
        if (resp[1] != 0x00)
            throw new Exception($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], cts.Token); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, cts.Token);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], cts.Token);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], cts.Token); break;
        }

        // ── Real round-trip: send probe + read first response byte ──
        // For port 443 we send a minimal TLS ClientHello so the remote server
        // replies with ServerHello in exactly 1 RTT. For other ports an HTTP
        // GET works (most servers reply with data or RST in 1 RTT).
        byte[] probe = port == 443
            ? BuildTlsClientHello(host)
            : System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stream.WriteAsync(probe, cts.Token);

        // Wait for the first byte of any upstream response. The connection may
        // be RST/closed by the remote (returns 0 from Read) — that's still a
        // valid 1-RTT measurement.
        var oneByte = new byte[1];
        try
        {
            int got = await stream.ReadAsync(oneByte, 0, 1, cts.Token);
            sw.Stop();
            // Guard: replies arriving in <= 1 ms almost certainly came from the
            // local proxy (closed connection, refused, etc.) rather than the
            // upstream server. Treat as a failure to avoid showing fake numbers.
            if (sw.ElapsedMilliseconds <= 1 && got == 0)
                throw new Exception("upstream closed (no data)");
            return sw.ElapsedMilliseconds;
        }
        catch (System.IO.IOException) when (sw.ElapsedMilliseconds > 1)
        {
            // Remote sent RST after a real round-trip — still a valid measurement.
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Builds a minimal TLS 1.2 ClientHello with the given SNI hostname.
    /// Any compliant TLS server replies with ServerHello after one full RTT,
    /// so the time between sending this and receiving the first response byte
    /// equals the network round-trip through the proxy chain to the server.
    /// </summary>
    private static byte[] BuildTlsClientHello(string sniHost)
    {
        var sni = System.Text.Encoding.ASCII.GetBytes(sniHost);

        // ── extensions ──
        // server_name (0x0000): list_len(2) + name_type(1) + host_len(2) + host
        var sniExt = new List<byte> { 0x00, 0x00 }; // ext type
        int sniListLen = 1 + 2 + sni.Length;
        int sniExtLen  = 2 + sniListLen;
        sniExt.AddRange(new byte[] { (byte)(sniExtLen >> 8), (byte)sniExtLen });
        sniExt.AddRange(new byte[] { (byte)(sniListLen >> 8), (byte)sniListLen });
        sniExt.Add(0x00); // host_name type
        sniExt.AddRange(new byte[] { (byte)(sni.Length >> 8), (byte)sni.Length });
        sniExt.AddRange(sni);

        // supported_versions (0x002b): TLS 1.3 + TLS 1.2 — accepted by all modern servers
        var verExt = new byte[] { 0x00, 0x2b, 0x00, 0x05, 0x04, 0x03, 0x04, 0x03, 0x03 };
        // supported_groups (0x000a): x25519
        var grpExt = new byte[] { 0x00, 0x0a, 0x00, 0x04, 0x00, 0x02, 0x00, 0x1d };
        // signature_algorithms (0x000d): rsa_pss_rsae_sha256, ecdsa_secp256r1_sha256
        var sigExt = new byte[] { 0x00, 0x0d, 0x00, 0x06, 0x00, 0x04, 0x08, 0x04, 0x04, 0x03 };

        var extensions = new List<byte>();
        extensions.AddRange(sniExt);
        extensions.AddRange(verExt);
        extensions.AddRange(grpExt);
        extensions.AddRange(sigExt);

        // ── ClientHello body ──
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });           // legacy_version = TLS 1.2
        for (int i = 0; i < 32; i++) body.Add(0xAA);        // random (fixed bytes are fine)
        body.Add(0x00);                                     // session_id length
        body.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // cipher_suites: TLS_AES_128_GCM_SHA256
        body.AddRange(new byte[] { 0x01, 0x00 });           // compression_methods: null
        body.AddRange(new byte[] {
            (byte)(extensions.Count >> 8), (byte)extensions.Count
        });
        body.AddRange(extensions);

        // Handshake header: type(1) + length(3)
        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(new byte[] {
            0x00, (byte)(body.Count >> 8), (byte)body.Count
        });
        handshake.AddRange(body);

        // TLS record header: type(1) + version(2) + length(2)
        var record = new List<byte> {
            0x16, 0x03, 0x01,
            (byte)(handshake.Count >> 8), (byte)handshake.Count
        };
        record.AddRange(handshake);

        return record.ToArray();
    }

    private static async Task ReadExactlyAsync(
        System.Net.Sockets.NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
            if (read == 0) throw new Exception("SOCKS5 connection closed unexpectedly");
            offset += read;
        }
    }

    #endregion
}
