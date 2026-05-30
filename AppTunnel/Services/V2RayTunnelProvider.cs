using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// ITunnelProvider implementation for V2Ray/sing-box (vmess, vless, trojan, shadowsocks, raw JSON).
/// Starts sing-box as a child process with a TUN inbound called "TunnelX-V2Ray".
/// WinDivert in TrafficRouterService handles per-app routing into that interface.
/// </summary>
public class V2RayTunnelProvider : ITunnelProvider
{
    private const string TunInterfaceName = "TunnelX-V2Ray";
    private const string TunAddress       = "172.18.0.1/30";
    private const string VpnLocalIp       = "172.18.0.1";  // actual TUN interface address
    private const int    DefaultTunMtu    = 1500;
    private const int    DefaultMixedProxyPort   = 2080;  // preferred sing-box SOCKS5/HTTP inbound for accurate ping
    private const int    TunInterfaceWaitSeconds = 20;

    private readonly string _singBoxExe;
    private readonly string _workDir;
    private readonly string _configPath;

    private Process?  _process;
    private int       _vpnInterfaceIndex = -1;

    // ── Watchdog ──────────────────────────────────────────────────────────────
    // Permissive watchdog: i/o timeouts are diagnostic only; only a sustained
    // "missing default interface" condition (>= MissingDefaultThreshold within
    // MissingDefaultWindowMs) is treated as fatal and triggers disconnect.
    // This matches v2rayN behavior — transient errors are logged, not acted on.
    private const int  MissingDefaultThreshold = 5;
    private const long MissingDefaultWindowMs  = 10_000;
    private int  _singBoxTimeoutErrors;     // diagnostic counter, no action
    private int  _missingDefaultCount;
    private long _missingDefaultFirstTick;
    private int  _tunnelFailedFired;        // Interlocked flag — one-shot

    /// <summary>
    /// Invoked (on a background thread) when sing-box loses connectivity and
    /// cannot recover on its own.  Wire this up before calling ConnectAsync.
    /// </summary>
    public Action? OnTunnelFailed { get; set; }

    private void TriggerTunnelFailed()
    {
        // Fire exactly once per connection lifetime.
        if (Interlocked.Exchange(ref _tunnelFailedFired, 1) == 0)
        {
            Logger.Warning("[WATCHDOG] Invoking OnTunnelFailed callback");
            Task.Run(() => OnTunnelFailed?.Invoke());
        }
    }

    public ConnectionStatus Status { get; } = new();

    public V2RayTunnelProvider()
    {
        _workDir    = NativeEngineSupport.SingBoxWorkDir;
        _configPath = Path.Combine(_workDir, "config.json");
        _singBoxExe = NativeEngineSupport.ResolveSingBoxExePath();
    }

    // =========================================================================
    // ITunnelProvider — ConnectAsync
    // =========================================================================

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        // Reset watchdog state for this new connection attempt.
        _singBoxTimeoutErrors    = 0;
        _missingDefaultCount     = 0;
        _missingDefaultFirstTick = 0;
        _tunnelFailedFired       = 0;

        Status.State   = ConnectionState.Connecting;
        Status.Message = LocalizationService.Instance.T("در حال آماده‌سازی V2Ray...");
        Logger.Info("V2RayTunnelProvider: starting");

        try
        {
            Directory.CreateDirectory(_workDir);
            NativeEngineSupport.EnsureWintunBesideEngine(_workDir);
            if (Path.GetDirectoryName(_singBoxExe) is { Length: > 0 } exeDir &&
                !exeDir.Equals(_workDir, StringComparison.OrdinalIgnoreCase))
                NativeEngineSupport.EnsureWintunBesideEngine(exeDir);

            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Active, "راه‌اندازی هسته تونل (Xray/V2Ray)");

            // Extract sing-box.exe from embedded resource if not already present
            // (only needed for the self-contained single-file build).
            await NativeEngineSupport.EnsureEmbeddedExecutableAsync("sing-box.exe", _singBoxExe, ct);

            if (!File.Exists(_singBoxExe))
            {
                Status.State   = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.Format("فایل sing-box.exe پیدا نشد: {0}", _singBoxExe);
                Logger.Error(Status.Message);
                return false;
            }

            // Build and write config
            int tunMtu = DefaultTunMtu;
            string singBoxJson;
            using var mixedProxyPortReservation = LocalPortReservation.ReservePreferredOrRandom(DefaultMixedProxyPort);
            var mixedProxyPort = mixedProxyPortReservation.Port;
            Logger.Info($"[PORT] sing-box mixed proxy=127.0.0.1:{mixedProxyPort}");
            try
            {
                if (config.AutoTuneMtu)
                {
                    string serverHost = ExtractServerHost(config.V2RayConfig);
                    tunMtu = await TunnelPerformanceTuner.GetRecommendedTunMtuAsync(
                        serverHost,
                        highOverheadTransport: false,
                        ct);
                    Logger.Info($"[MTU] Auto-tuned TUN MTU={tunMtu} (server={serverHost})");
                }
                else
                {
                    tunMtu = DefaultTunMtu;
                    Logger.Info($"[MTU] Auto-tune disabled; using MTU={tunMtu}");
                }

                singBoxJson = BuildSingBoxConfig(config.V2RayConfig, tunMtu, config.EnableDnsOptimization, mixedProxyPort);
            }
            catch (Exception ex)
            {
                mixedProxyPortReservation.Dispose();
                Status.State   = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.Format("خطا در پارس کانفیگ: {0}", ex.Message);
                Logger.Error("V2Ray config parse error", ex);
                return false;
            }

            await File.WriteAllTextAsync(_configPath, singBoxJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            Logger.Info($"V2Ray config written to {_configPath}");
            mixedProxyPortReservation.Dispose();

            // Start sing-box process
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = _singBoxExe,
                    Arguments              = $"run -c \"{_configPath}\"",
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory       = _workDir
                },
                EnableRaisingEvents = true
            };

            // Pipe stderr/stdout to Logger; stderr also feeds the watchdog.
            //
            // Watchdog policy (intentionally permissive — matches v2rayN behavior):
            //   * i/o timeouts and "context deadline exceeded" are LOGGED ONLY.
            //     They are common transient errors when CDN POPs are slow or a
            //     specific destination is being throttled. sing-box retries
            //     internally; we should not tear the whole tunnel down.
            //   * "missing default interface" is also transient — it can fire
            //     during a brief Wi-Fi roam or routing-table flux. We require it
            //     to be reported repeatedly within a short window before acting.
            //   * Process exit is the only definitive failure (handled elsewhere).
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Logger.ProcessOutput("[sing-box stderr]", e.Data, isError: true);

                if (e.Data.Contains("network: missing default interface"))
                {
                    int n = Interlocked.Increment(ref _missingDefaultCount);
                    var nowTicks = Environment.TickCount64;
                    var first = Interlocked.Read(ref _missingDefaultFirstTick);
                    if (first == 0)
                    {
                        Interlocked.Exchange(ref _missingDefaultFirstTick, nowTicks);
                    }
                    else if (nowTicks - first > MissingDefaultWindowMs)
                    {
                        // Stale window — reset and start over.
                        Interlocked.Exchange(ref _missingDefaultCount, 1);
                        Interlocked.Exchange(ref _missingDefaultFirstTick, nowTicks);
                    }
                    else if (n >= MissingDefaultThreshold)
                    {
                        Logger.Error($"[WATCHDOG] sing-box: 'missing default interface' ×{n} within {(nowTicks - first)/1000}s — triggering tunnel failure");
                        TriggerTunnelFailed();
                    }
                    return;
                }

                if (e.Data.Contains("i/o timeout") || e.Data.Contains("context deadline exceeded"))
                {
                    // Just count for diagnostics, but never trigger disconnect.
                    Interlocked.Increment(ref _singBoxTimeoutErrors);
                    return;
                }

                if (e.Data.Contains("accepted") || e.Data.Contains("established") ||
                    e.Data.Contains("connected") || e.Data.Contains("inbound/"))
                {
                    // Reset diagnostic counter on healthy log lines.
                    Interlocked.Exchange(ref _singBoxTimeoutErrors, 0);
                    Interlocked.Exchange(ref _missingDefaultCount, 0);
                    Interlocked.Exchange(ref _missingDefaultFirstTick, 0);
                }
            };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Logger.ProcessOutput("[sing-box]", e.Data, isError: false);
            };

            _process.Start();
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            Logger.Info($"sing-box started (PID {_process.Id})");
            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Active, "راه‌اندازی هسته تونل (Xray/V2Ray)");
            ConnectionProgressService.Report("tun_bridge", ConnectionProgressPhase.Active, "راه‌اندازی پل TUN (sing-box)");
            ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Active, "شناسایی آداپتر مجازی");
            Status.Message = LocalizationService.Instance.T("در حال انتظار برای interface TunnelX-V2Ray...");

            int interfaceIndex = -1;
            var deadline = DateTime.UtcNow.AddSeconds(TunInterfaceWaitSeconds);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // Detect early crash before we even get the interface
                if (_process.HasExited)
                {
                    Status.State   = ConnectionState.Error;
                    Status.Message = LocalizationService.Instance.Format("sing-box زودتر خارج شد (exit code {0}) — کانفیگ را بررسی کنید", _process.ExitCode);
                    Logger.Error(Status.Message);
                    await KillProcessAsync();
                    return false;
                }

                interfaceIndex = FindInterfaceIndex(TunInterfaceName);
                if (interfaceIndex > 0) break;
                await Task.Delay(500, ct);
            }

            if (interfaceIndex <= 0)
            {
                Status.State   = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.Format(
                    "interface TunnelX-V2Ray ظاهر نشد (timeout {0}s)",
                    TunInterfaceWaitSeconds);
                Logger.Error(Status.Message);
                ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Fail, Status.Message);
                await KillProcessAsync();
                return false;
            }

            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Complete, "راه‌اندازی هسته تونل (Xray/V2Ray)");
            ConnectionProgressService.Report("tun_bridge", ConnectionProgressPhase.Complete, "راه‌اندازی پل TUN (sing-box)");
            ConnectionProgressService.Report(
                "tun_interface",
                ConnectionProgressPhase.Complete,
                "شناسایی آداپتر مجازی",
                "آداپتر TunnelX-V2Ray آماده شد (شماره {0})",
                interfaceIndex.ToString());

            _vpnInterfaceIndex = interfaceIndex;

            var statusServerHost = ExtractServerHost(config.V2RayConfig);
            var statusServerPort = ExtractServerPort(config.V2RayConfig);
            Status.State             = ConnectionState.Connected;
            Status.ConnectedSince    = DateTime.Now;
            Status.VpnLocalIp        = VpnLocalIp;
            Status.VpnServerHost     = statusServerHost;
            Status.VpnServerIp       = statusServerHost;
            Status.VpnServerPort     = statusServerPort;
            Status.VpnInterfaceIndex = interfaceIndex;
            Status.SingBoxMixedPort  = mixedProxyPort;
            Status.Message           = config.TunnelType == TunnelType.SocksProxy ? "Proxy connected" : "V2Ray connected";
            Logger.Info($"{(config.TunnelType == TunnelType.SocksProxy ? "Proxy" : "V2Ray")} tunnel up — interface index {interfaceIndex}, server={Status.VpnServerIp}:{Status.VpnServerPort}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State   = ConnectionState.Disconnected;
            Status.Message = LocalizationService.Instance.T("اتصال لغو شد");
            await KillProcessAsync();
            return false;
        }
        catch (Exception ex)
        {
            Status.State   = ConnectionState.Error;
            Status.Message = LocalizationService.Instance.Format("خطا: {0}", ex.Message);
            Logger.Error("V2RayTunnelProvider.ConnectAsync failed", ex);
            await KillProcessAsync();
            return false;
        }
    }

    // =========================================================================
    // ITunnelProvider — DisconnectAsync
    // =========================================================================

    public async Task DisconnectAsync()
    {
        Status.State   = ConnectionState.Disconnecting;
        Status.Message = LocalizationService.Instance.T("در حال قطع اتصال V2Ray...");

        // Stop watchdog from firing during/after deliberate disconnect.
        _tunnelFailedFired = 1;

        await KillProcessAsync();

        try { if (File.Exists(_configPath)) File.Delete(_configPath); }
        catch { /* best effort */ }

        _vpnInterfaceIndex       = -1;
        Status.State             = ConnectionState.Disconnected;
        Status.ConnectedSince    = null;
        Status.VpnLocalIp        = string.Empty;
        Status.VpnServerHost     = string.Empty;
        Status.VpnServerIp       = string.Empty;
        Status.VpnServerPort     = 0;
        Status.VpnInterfaceIndex = -1;
        Status.SingBoxMixedPort  = 0;
        Status.Message           = LocalizationService.Instance.T("قطع شد");
    }

    // =========================================================================
    // ITunnelProvider — IsInterfaceUp
    // =========================================================================

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

    // =========================================================================
    // Config builder
    // =========================================================================

    private string BuildSingBoxConfig(string userConfig, int tunMtu, bool enableDnsOptimization, int mixedProxyPort)
    {
        userConfig = userConfig.Trim();
        if (userConfig.StartsWith("{"))
            return userConfig;

        var (outbound, outboundTag) = ParseShareLinkOutbound(userConfig);
        ApplyServerPreResolve(outbound, enableDnsOptimization);
        return SerializeSingBoxDocument(outbound, outboundTag, mixedProxyPort, includeTun: true, tunMtu);
    }

    internal string BuildMixedOnlySingBoxConfig(string userConfig, int mixedProxyPort, bool enableDnsOptimization = true)
    {
        userConfig = userConfig.Trim();
        if (userConfig.StartsWith("{"))
            throw new InvalidOperationException(LocalizationService.Instance.T("تست Real Delay برای JSON کامل پشتیبانی نمی‌شود"));

        var (outbound, outboundTag) = ParseShareLinkOutbound(userConfig);
        ApplyServerPreResolve(outbound, enableDnsOptimization);
        return SerializeSingBoxDocument(outbound, outboundTag, mixedProxyPort, includeTun: false, DefaultTunMtu);
    }

    internal async Task<long> ProbeMixedProxyLatencyAsync(
        string userConfig,
        CancellationToken ct,
        string probeHost = Socks5LatencyProbe.DefaultProbeHost,
        int probePort = Socks5LatencyProbe.DefaultProbePort)
    {
        Directory.CreateDirectory(_workDir);
        await NativeEngineSupport.EnsureEmbeddedExecutableAsync("sing-box.exe", _singBoxExe, ct);
        if (!File.Exists(_singBoxExe))
            throw new FileNotFoundException(LocalizationService.Instance.Format("فایل sing-box.exe پیدا نشد: {0}", _singBoxExe));

        using var portReservation = LocalPortReservation.ReservePreferredOrRandom(DefaultMixedProxyPort + 17);
        var mixedPort = portReservation.Port;
        var probeConfigPath = Path.Combine(_workDir, $"probe-{Guid.NewGuid():N}.json");
        Process? process = null;

        try
        {
            var json = BuildMixedOnlySingBoxConfig(userConfig, mixedPort);
            await File.WriteAllTextAsync(probeConfigPath, json, new UTF8Encoding(false), ct);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _singBoxExe,
                    Arguments = $"run -c \"{probeConfigPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = _workDir
                },
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Logger.ProcessOutput("[sing-box probe stderr]", e.Data, isError: true);
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Logger.ProcessOutput("[sing-box probe]", e.Data, isError: false);
            };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await WaitForLocalPortAsync(mixedPort, TimeSpan.FromSeconds(12), ct);
            if (process.HasExited)
                throw new InvalidOperationException(LocalizationService.Instance.Format("sing-box probe exited early (code {0})", process.ExitCode));

            return await Socks5LatencyProbe.MeasureAsync(probeHost, probePort, mixedPort, ct);
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch
            {
                // ignored
            }

            process?.Dispose();
            try { if (File.Exists(probeConfigPath)) File.Delete(probeConfigPath); } catch { }
        }
    }

    private static async Task WaitForLocalPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, ct);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                if (ex is OperationCanceledException)
                    throw;
                await Task.Delay(250, ct);
            }
        }

        throw new TimeoutException(lastError?.Message ?? LocalizationService.Instance.T("پورت محلی sing-box آماده نشد"));
    }

    private (JsonObject outbound, string outboundTag) ParseShareLinkOutbound(string userConfig)
    {
        if (userConfig.StartsWith("vmess://"))
        {
            var parsed = ParseVmess(userConfig);
            LogWebSocketEarlyDataIfPresent(parsed.outbound);
            return parsed;
        }

        if (userConfig.StartsWith("vless://"))
            return ParseVless(userConfig);
        if (userConfig.StartsWith("trojan://"))
            return ParseTrojan(userConfig);
        if (userConfig.StartsWith("ss://"))
            return ParseShadowsocks(userConfig);
        if (userConfig.StartsWith("socks5://") || userConfig.StartsWith("socks://"))
            return ParseSocks5(userConfig);
        if (userConfig.StartsWith("http://"))
            return ParseHttp(userConfig);

        throw new InvalidOperationException(
            "کانفیگ باید یک sing-box JSON ({…}) یا URI از نوع vmess:// / vless:// / trojan:// / ss:// باشد");
    }

    private static void ApplyServerPreResolve(JsonObject outbound, bool enableDnsOptimization)
    {
        var skipServerPreResolve = outbound["transport"]?["type"]?.GetValue<string>() == "ws";
        if (!enableDnsOptimization ||
            skipServerPreResolve ||
            outbound["server"] is not JsonValue sv ||
            !sv.TryGetValue<string>(out var srvHost) ||
            string.IsNullOrEmpty(srvHost) ||
            System.Net.IPAddress.TryParse(srvHost, out _))
            return;

        try
        {
            var v4 = DnsResolverCache.ResolveFirstIpv4(srvHost);
            if (v4 == null)
                return;

            outbound["server"] = v4.ToString();
            var tlsSni = outbound["tls"]?["server_name"]?.GetValue<string>();
            Logger.Info($"[CONFIG] Pre-resolved sing-box server '{srvHost}' → {v4} (tls.server_name='{tlsSni ?? srvHost}')");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CONFIG] Could not pre-resolve server '{srvHost}': {ex.Message}");
        }
    }

    private static string SerializeSingBoxDocument(
        JsonObject outbound,
        string outboundTag,
        int mixedProxyPort,
        bool includeTun,
        int tunMtu)
    {
        var inbounds = new JsonArray();
        if (includeTun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunInterfaceName,
                ["address"] = new JsonArray { TunAddress },
                ["mtu"] = TunnelPerformanceTuner.ClampTunMtu(tunMtu),
                ["auto_route"] = false,
                ["strict_route"] = false,
                ["stack"] = "gvisor"
            });
        }

        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = mixedProxyPort
        });

        var rules = new JsonArray();
        if (includeTun)
        {
            rules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray { "tun-in" },
                ["outbound"] = outboundTag
            });
        }

        rules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray { "mixed-in" },
            ["outbound"] = outboundTag
        });

        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = false },
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray
            {
                outbound,
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["rules"] = rules
            }
        };

        using var ms = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // =========================================================================
    // URI parsers
    // =========================================================================

    private static (JsonObject outbound, string tag) ParseVmess(string uri)
    {
        // vmess://base64(json)#remark
        var b64 = uri["vmess://".Length..].Split('#')[0];
        // Pad base64
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var v = JsonNode.Parse(json)!.AsObject();

        var tag = v["ps"]?.GetValue<string>() ?? "vmess-out";

        var outbound = new JsonObject
        {
            ["type"]    = "vmess",
            ["tag"]     = tag,
            ["server"]  = v["add"]?.GetValue<string>() ?? "",
            ["server_port"] = int.TryParse(v["port"]?.ToString(), out var p) ? p : 443,
            ["uuid"]    = v["id"]?.GetValue<string>() ?? "",
            ["alter_id"] = int.TryParse(v["aid"]?.ToString(), out var aid) ? aid : 0,
            ["security"] = v["scy"]?.GetValue<string>() ?? "auto"
        };

        // TLS
        var net  = v["net"]?.GetValue<string>() ?? "tcp";
        var tls  = v["tls"]?.GetValue<string>() ?? "";
        if (tls == "tls")
        {
            var tlsObj = new JsonObject
            {
                ["enabled"]     = true,
                ["server_name"] = v["sni"]?.GetValue<string>() ?? v["add"]?.GetValue<string>() ?? ""
            };
            ApplyVmessTlsExtras(tlsObj, v);
            outbound["tls"] = tlsObj;
        }

        // Transport
        if (net is "ws" or "websocket")
        {
            var wsTransport = new JsonObject
            {
                ["type"] = "ws",
                ["headers"] = new JsonObject
                {
                    ["Host"] = v["host"]?.GetValue<string>() ?? v["add"]?.GetValue<string>() ?? ""
                }
            };
            V2RayWebSocketHelper.ApplyEarlyDataToSingBox(wsTransport, v["path"]?.GetValue<string>() ?? "/");
            outbound["transport"] = wsTransport;
        }
        else if (net is "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"]         = "grpc",
                ["service_name"] = v["path"]?.GetValue<string>() ?? ""
            };
        }

        ApplyWebSocketTlsCompat(outbound);
        return (outbound, tag);
    }

    private static (JsonObject outbound, string tag) ParseVless(string uri)
    {
        // vless://uuid@host:port?params#remark
        var u = new Uri(uri);
        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrEmpty(tag)) tag = "vless-out";

        var query = ParseQuery(u.Query);

        var outbound = new JsonObject
        {
            ["type"]        = "vless",
            ["tag"]         = tag,
            ["server"]      = u.Host,
            ["server_port"] = u.Port > 0 ? u.Port : 443,
            ["uuid"]        = u.UserInfo
        };

        // TLS / Reality
        var security = query.GetValueOrDefault("security", "");
        if (security is "tls" or "reality")
        {
            var tlsObj = new JsonObject
            {
                ["enabled"]     = true,
                ["server_name"] = query.GetValueOrDefault("sni", u.Host)
            };
            ApplyVlessTlsExtras(tlsObj, query);
            if (security == "reality")
            {
                tlsObj["reality"] = new JsonObject
                {
                    ["enabled"]    = true,
                    ["public_key"] = query.GetValueOrDefault("pbk", ""),
                    ["short_id"]   = query.GetValueOrDefault("sid", "")
                };
            }
            outbound["tls"] = tlsObj;
        }

        // Flow
        var flow = query.GetValueOrDefault("flow", "");
        if (!string.IsNullOrEmpty(flow)) outbound["flow"] = flow;

        // Transport
        var net = query.GetValueOrDefault("type", "tcp");
        if (net is "ws")
        {
            var wsTransport = new JsonObject
            {
                ["type"] = "ws",
                ["headers"] = new JsonObject
                {
                    ["Host"] = query.GetValueOrDefault("host", u.Host)
                }
            };
            V2RayWebSocketHelper.ApplyEarlyDataToSingBox(wsTransport, query.GetValueOrDefault("path", "/"));
            outbound["transport"] = wsTransport;
        }
        else if (net is "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"]         = "grpc",
                ["service_name"] = query.GetValueOrDefault("serviceName", "")
            };
        }
        else if (net is "xhttp")
        {
            var host = query.GetValueOrDefault("host", "");
            var sni  = query.GetValueOrDefault("sni", u.Host);

            outbound["transport"] = new JsonObject
            {
                ["type"] = "http",

                ["host"] = new JsonArray
                {
                    string.IsNullOrWhiteSpace(host)
                        ? sni
                        : host
                },

                ["path"] = query.GetValueOrDefault("path", "/"),

                ["mode"] = query.GetValueOrDefault("mode", "auto")
            };
        }

        ApplyWebSocketTlsCompat(outbound);
        return (outbound, tag);
    }

    private static (JsonObject outbound, string tag) ParseTrojan(string uri)
    {
        // trojan://password@host:port?params#remark
        var u = new Uri(uri);
        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrEmpty(tag)) tag = "trojan-out";

        var query = ParseQuery(u.Query);

        var outbound = new JsonObject
        {
            ["type"]        = "trojan",
            ["tag"]         = tag,
            ["server"]      = u.Host,
            ["server_port"] = u.Port > 0 ? u.Port : 443,
            ["password"]    = u.UserInfo,
            ["tls"] = new JsonObject
            {
                ["enabled"]     = true,
                ["server_name"] = query.GetValueOrDefault("sni", u.Host),
                ["insecure"]    = query.GetValueOrDefault("allowInsecure", "0") == "1"
            }
        };

        var net = query.GetValueOrDefault("type", "tcp");
        if (net is "ws")
        {
            var wsTransport = new JsonObject
            {
                ["type"] = "ws",
                ["headers"] = new JsonObject
                {
                    ["Host"] = query.GetValueOrDefault("host", u.Host)
                }
            };
            V2RayWebSocketHelper.ApplyEarlyDataToSingBox(wsTransport, query.GetValueOrDefault("path", "/"));
            outbound["transport"] = wsTransport;
        }

        ApplyWebSocketTlsCompat(outbound);
        return (outbound, tag);
    }

    /// <summary>
    /// v2rayNG/Xray encode WebSocket 0-RTT as path query <c>?ed=NNNN</c> or <c>?edNNNN</c>. sing-box needs
    /// <c>max_early_data</c> plus <c>Sec-WebSocket-Protocol</c> (Xray-compatible mode).
    /// </summary>
    private static void LogWebSocketEarlyDataIfPresent(JsonObject outbound)
    {
        if (outbound["transport"] is not JsonObject transport ||
            transport["type"]?.GetValue<string>() != "ws")
            return;

        var path = transport["path"]?.GetValue<string>() ?? "/";
        if (transport["max_early_data"] is JsonValue ed && ed.TryGetValue<uint>(out var n) && n > 0)
            Logger.Info($"[CONFIG] WebSocket path='{path}' max_early_data={n} (Xray ?ed= compat)");
    }

    /// <summary>
    /// sing-box rejects or mishandles some ALPN values on WebSocket (e.g. h3, vless+h2).
    /// uTLS on vless+ws should use fingerprint defaults without an explicit alpn list.
    /// </summary>
    private static void ApplyWebSocketTlsCompat(JsonObject outbound)
    {
        if (outbound["transport"] is not JsonObject transport ||
            transport["type"]?.GetValue<string>() != "ws" ||
            outbound["tls"] is not JsonObject tls)
        {
            return;
        }

        var proxyType = outbound["type"]?.GetValue<string>() ?? "";
        if (proxyType == "vless" &&
            tls["utls"] is JsonObject utls &&
            utls["enabled"]?.GetValue<bool>() == true)
        {
            tls.Remove("alpn");
            return;
        }

        if (tls["alpn"] is not JsonArray alpn)
            return;

        var kept = new List<JsonNode>();
        foreach (var item in alpn)
        {
            var proto = item?.GetValue<string>() ?? "";
            if (string.Equals(proto, "h3", StringComparison.OrdinalIgnoreCase))
                continue;
            if (proxyType == "vless" &&
                string.Equals(proto, "h2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(item!.DeepClone());
        }

        if (kept.Count == 0)
            tls.Remove("alpn");
        else
            tls["alpn"] = new JsonArray(kept.ToArray());
    }

    private static void ApplyVlessTlsExtras(JsonObject tlsObj, IReadOnlyDictionary<string, string> query)
    {
        if (query.GetValueOrDefault("allowInsecure", "0") == "1" ||
            query.GetValueOrDefault("insecure", "0") == "1")
        {
            tlsObj["insecure"] = true;
        }

        var alpn = query.GetValueOrDefault("alpn", "");
        if (!string.IsNullOrWhiteSpace(alpn))
        {
            tlsObj["alpn"] = new JsonArray(
                alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => JsonValue.Create(a))
                    .ToArray());
        }

        var fp = V2RayWebSocketHelper.NormalizeFingerprint(query.GetValueOrDefault("fp", ""));
        if (!string.IsNullOrWhiteSpace(fp))
        {
            tlsObj["utls"] = new JsonObject
            {
                ["enabled"]     = true,
                ["fingerprint"] = fp
            };
        }
    }

    private static void ApplyVmessTlsExtras(JsonObject tlsObj, JsonObject vmessJson)
    {
        var alpn = vmessJson["alpn"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(alpn))
        {
            tlsObj["alpn"] = new JsonArray(
                alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => JsonValue.Create(a))
                    .ToArray());
        }

        var fp = V2RayWebSocketHelper.NormalizeFingerprint(vmessJson["fp"]?.GetValue<string>());
        if (!string.IsNullOrWhiteSpace(fp))
        {
            tlsObj["utls"] = new JsonObject
            {
                ["enabled"]     = true,
                ["fingerprint"] = fp
            };
        }

        if (vmessJson["insecure"]?.ToString() is "1" or "true")
            tlsObj["insecure"] = true;
    }

    private static (JsonObject outbound, string tag) ParseShadowsocks(string uri)
    {
        // ss://base64(method:password)@host:port#remark
        // or ss://base64(method:password@host:port)#remark
        var u = new Uri(uri);
        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrEmpty(tag)) tag = "ss-out";

        string method, password, host;
        int port;

        if (!string.IsNullOrEmpty(u.Host))
        {
            // SIP002 format: ss://userinfo@host:port
            var userInfoDecoded = TryBase64Decode(u.UserInfo) ?? u.UserInfo;
            var colonIdx = userInfoDecoded.IndexOf(':');
            method   = colonIdx >= 0 ? userInfoDecoded[..colonIdx] : userInfoDecoded;
            password = colonIdx >= 0 ? userInfoDecoded[(colonIdx + 1)..] : "";
            host     = u.Host;
            port     = u.Port > 0 ? u.Port : 443;
        }
        else
        {
            // Legacy format: ss://base64(method:password@host:port)
            var b64 = uri["ss://".Length..].Split('#')[0];
            var decoded = TryBase64Decode(b64) ?? throw new FormatException("ss:// URI decode failed");
            // decoded = method:password@host:port
            var atIdx = decoded.LastIndexOf('@');
            var userPart = decoded[..atIdx];
            var hostPart = decoded[(atIdx + 1)..];
            var colonIdx = userPart.IndexOf(':');
            method   = colonIdx >= 0 ? userPart[..colonIdx] : userPart;
            password = colonIdx >= 0 ? userPart[(colonIdx + 1)..] : "";
            var lastColon = hostPart.LastIndexOf(':');
            host = lastColon >= 0 ? hostPart[..lastColon] : hostPart;
            port = lastColon >= 0 && int.TryParse(hostPart[(lastColon + 1)..], out var p) ? p : 443;
        }

        var outbound = new JsonObject
        {
            ["type"]        = "shadowsocks",
            ["tag"]         = tag,
            ["server"]      = host,
            ["server_port"] = port,
            ["method"]      = method,
            ["password"]    = password
        };

        return (outbound, tag);
    }

    private static (JsonObject outbound, string tag) ParseSocks5(string uri)
    {
        var u = new Uri(uri);
        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrEmpty(tag)) tag = "socks5-out";

        var outbound = new JsonObject
        {
            ["type"]        = "socks",
            ["tag"]         = tag,
            ["server"]      = u.Host,
            ["server_port"] = u.Port > 0 ? u.Port : 1080,
            ["version"]     = "5"
        };

        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            var userInfo = Uri.UnescapeDataString(u.UserInfo);
            var colonIdx = userInfo.IndexOf(':');
            if (colonIdx >= 0)
            {
                outbound["users"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["username"] = userInfo[..colonIdx],
                        ["password"] = userInfo[(colonIdx + 1)..]
                    }
                };
            }
        }

        return (outbound, tag);
    }

    private static (JsonObject outbound, string tag) ParseHttp(string uri)
    {
        var u = new Uri(uri);
        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrEmpty(tag)) tag = "http-out";

        var outbound = new JsonObject
        {
            ["type"]        = "http",
            ["tag"]         = tag,
            ["server"]      = u.Host,
            ["server_port"] = u.Port > 0 ? u.Port : 3128
        };

        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            var userInfo = Uri.UnescapeDataString(u.UserInfo);
            var colonIdx = userInfo.IndexOf(':');
            if (colonIdx >= 0)
            {
                outbound["users"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["username"] = userInfo[..colonIdx],
                        ["password"] = userInfo[(colonIdx + 1)..]
                    }
                };
            }
        }

        return (outbound, tag);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Extracts the proxy server hostname from a V2Ray URI (vmess/vless/trojan/ss).
    /// Returns empty string for raw JSON configs or unparseable input.
    /// </summary>
    private static string ExtractServerHost(string userConfig) =>
        V2RayEndpointHelper.ExtractServerHost(userConfig);

    private static int ExtractServerPort(string userConfig) =>
        V2RayEndpointHelper.ExtractServerPort(userConfig);

    private static int FindInterfaceIndex(string interfaceName)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)) continue;
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index > 0) return ipv4.Index;
            }
        }
        catch { }
        return -1;
    }

    private async Task KillProcessAsync()
    {
        // Step 1: kill the tracked process reference
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await _process.WaitForExitAsync(cts.Token); } catch { }
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        // Stale/orphan sing-box instances are cleaned by TunnelXCleanupService,
        // scoped to TunnelX command lines. Do not taskkill every sing-box.exe on
        // the machine; users may run their own clients.
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private static string? TryBase64Decode(string s)
    {
        try
        {
            s = s.PadRight((s.Length + 3) / 4 * 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch { return null; }
    }
}
