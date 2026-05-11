using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
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
    private const int    MixedProxyPort   = 2080;  // sing-box SOCKS5/HTTP inbound for accurate ping

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
        _workDir    = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TunnelX", "singbox");
        _configPath = Path.Combine(_workDir, "config.json");

        // For regular builds sing-box.exe is copied next to the exe — use it directly.
        // For self-contained single-file builds the .exe is not bundled by the runtime
        // (only .dll files are extracted by IncludeNativeLibrariesForSelfExtract).
        // In that case we fall back to the copy we maintain in the work dir, which is
        // extracted from the embedded resource the first time ConnectAsync is called.
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        _singBoxExe = File.Exists(sideBySide)
            ? sideBySide
            : Path.Combine(_workDir, "sing-box.exe");
    }

    // =========================================================================
    // Embedded-resource extraction (self-contained single-file support)
    // =========================================================================

    /// <summary>
    /// Extracts sing-box.exe from the embedded assembly resource to the work dir
    /// so the self-contained single-file build can find it. Skipped if the file
    /// is already the correct size (i.e. already extracted or side-by-side copy used).
    /// </summary>
    private async Task EnsureSingBoxExtractedAsync(CancellationToken ct)
    {
        // Side-by-side path (regular build) — nothing to extract.
        if (!_singBoxExe.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
            return;

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("sing-box.exe");
        if (stream == null)
        {
            Logger.Warning("[sing-box] Embedded resource 'sing-box.exe' not found — standalone may not work.");
            return;
        }

        // Skip if already extracted and same size as embedded resource.
        if (File.Exists(_singBoxExe) && new FileInfo(_singBoxExe).Length == stream.Length)
            return;

        Logger.Info($"[sing-box] Extracting embedded sing-box.exe ({stream.Length / 1024 / 1024} MB) → {_singBoxExe}");
        using var fs = new FileStream(_singBoxExe, FileMode.Create, FileAccess.Write, FileShare.None,
                                      bufferSize: 81920, useAsync: true);
        await stream.CopyToAsync(fs, 81920, ct);
        Logger.Info("[sing-box] Extraction complete.");
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
        Status.Message = "در حال آماده‌سازی V2Ray...";
        Logger.Info("V2RayTunnelProvider: starting");

        try
        {
            Directory.CreateDirectory(_workDir);

            // Extract sing-box.exe from embedded resource if not already present
            // (only needed for the self-contained single-file build).
            await EnsureSingBoxExtractedAsync(ct);

            if (!File.Exists(_singBoxExe))
            {
                Status.State   = ConnectionState.Error;
                Status.Message = $"فایل sing-box.exe پیدا نشد: {_singBoxExe}";
                Logger.Error(Status.Message);
                return false;
            }

            // Build and write config
            int tunMtu = DefaultTunMtu;
            string singBoxJson;
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

                singBoxJson = BuildSingBoxConfig(config.V2RayConfig, tunMtu, config.EnableDnsOptimization);
            }
            catch (Exception ex)
            {
                Status.State   = ConnectionState.Error;
                Status.Message = $"خطا در پارس کانفیگ: {ex.Message}";
                Logger.Error("V2Ray config parse error", ex);
                return false;
            }

            await File.WriteAllTextAsync(_configPath, singBoxJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            Logger.Info($"V2Ray config written to {_configPath}");

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
            Status.Message = "در حال انتظار برای interface TunnelX-V2Ray...";

            // Wait up to 10 seconds for the TUN interface to appear
            int interfaceIndex = -1;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // Detect early crash before we even get the interface
                if (_process.HasExited)
                {
                    Status.State   = ConnectionState.Error;
                    Status.Message = $"sing-box زودتر خارج شد (exit code {_process.ExitCode}) — کانفیگ را بررسی کنید";
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
                Status.Message = "interface TunnelX-V2Ray ظاهر نشد (timeout 10s)";
                Logger.Error(Status.Message);
                await KillProcessAsync();
                return false;
            }

            _vpnInterfaceIndex = interfaceIndex;

            Status.State             = ConnectionState.Connected;
            Status.ConnectedSince    = DateTime.Now;
            Status.VpnLocalIp        = VpnLocalIp;
            Status.VpnServerIp       = ExtractServerHost(config.V2RayConfig);
            Status.VpnInterfaceIndex = interfaceIndex;
            Status.SingBoxMixedPort  = MixedProxyPort;
            Status.Message           = "V2Ray connected";
            Logger.Info($"V2Ray tunnel up — interface index {interfaceIndex}, server={Status.VpnServerIp}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State   = ConnectionState.Disconnected;
            Status.Message = "اتصال لغو شد";
            await KillProcessAsync();
            return false;
        }
        catch (Exception ex)
        {
            Status.State   = ConnectionState.Error;
            Status.Message = $"خطا: {ex.Message}";
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
        Status.Message = "در حال قطع اتصال V2Ray...";

        // Stop watchdog from firing during/after deliberate disconnect.
        _tunnelFailedFired = 1;

        await KillProcessAsync();

        try { if (File.Exists(_configPath)) File.Delete(_configPath); }
        catch { /* best effort */ }

        _vpnInterfaceIndex       = -1;
        Status.State             = ConnectionState.Disconnected;
        Status.ConnectedSince    = null;
        Status.VpnLocalIp        = string.Empty;
        Status.VpnInterfaceIndex = -1;
        Status.SingBoxMixedPort  = 0;
        Status.Message           = "قطع شد";
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

    private string BuildSingBoxConfig(string userConfig, int tunMtu, bool enableDnsOptimization)
    {
        userConfig = userConfig.Trim();

        JsonObject outbound;
        string outboundTag;

        if (userConfig.StartsWith("{"))
        {
            // Raw sing-box JSON: user is responsible for the full document.
            // Return as-is (overrides everything).
            return userConfig;
        }
        else if (userConfig.StartsWith("vmess://"))
        {
            (outbound, outboundTag) = ParseVmess(userConfig);
        }
        else if (userConfig.StartsWith("vless://"))
        {
            (outbound, outboundTag) = ParseVless(userConfig);
        }
        else if (userConfig.StartsWith("trojan://"))
        {
            (outbound, outboundTag) = ParseTrojan(userConfig);
        }
        else if (userConfig.StartsWith("ss://"))
        {
            (outbound, outboundTag) = ParseShadowsocks(userConfig);
        }
        else
        {
            throw new InvalidOperationException(
                "کانفیگ باید یک sing-box JSON ({…}) یا URI از نوع vmess:// / vless:// / trojan:// / ss:// باشد");
        }

        // Pre-resolve the server hostname to an IPv4 address and write the IP
        // into outbound["server"]. This is critical:
        //   1. sing-box never has to do runtime DNS for the server domain,
        //      which prevents a recursive lookup loop when the system DNS
        //      server has a host route through our own TUN interface.
        //   2. CDN behind the domain is captured at connect-time; the IP we
        //      use exactly matches the IP excluded from WinDivert filters.
        // SNI (tls.server_name) keeps the original hostname so TLS still
        // validates correctly against the CDN certificate.
        if (enableDnsOptimization &&
            outbound["server"] is JsonValue sv &&
            sv.TryGetValue<string>(out var srvHost) &&
            !string.IsNullOrEmpty(srvHost) &&
            !System.Net.IPAddress.TryParse(srvHost, out _))
        {
            try
            {
                var v4 = DnsResolverCache.ResolveFirstIpv4(srvHost);
                if (v4 != null)
                {
                    outbound["server"] = v4.ToString();
                    Logger.Info($"[CONFIG] Pre-resolved sing-box server '{srvHost}' → {v4} (SNI kept as '{srvHost}')");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[CONFIG] Could not pre-resolve server '{srvHost}': {ex.Message}");
            }
        }

        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"]           = "tun",
                    ["tag"]            = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"]        = new JsonArray { TunAddress },
                    ["mtu"]            = TunnelPerformanceTuner.ClampTunMtu(tunMtu),
                    ["auto_route"]     = false,
                    ["strict_route"]   = false,
                    ["stack"]          = "system"
                },
                // Mixed SOCKS5/HTTP proxy inbound — used for accurate end-to-end
                // ping measurement. Unlike the TUN inbound (which completes the
                // TCP handshake locally before the remote connection is ready),
                // this inbound only replies CONNECT_OK after the real upstream
                // TCP handshake through the proxy chain has completed, giving a
                // true end-to-end round-trip latency reading.
                new JsonObject
                {
                    ["type"]         = "mixed",
                    ["tag"]          = "mixed-in",
                    ["listen"]       = "127.0.0.1",
                    ["listen_port"]  = MixedProxyPort
                }
            },
            // direct outbound is required so sing-box can reach the proxy
            // server and any other untunneled endpoint without entering the
            // VLESS path. Without it, system DNS lookups by sing-box may
            // recurse back into the TUN.
            ["outbounds"] = new JsonArray
            {
                outbound,
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new JsonObject
            {
                // Ask sing-box to track the system default interface so it can
                // automatically reroute its own outbound (server) traffic when
                // the physical NIC changes (Wi-Fi reconnect, etc.).
                ["auto_detect_interface"] = true,
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inbound"]  = new JsonArray { "tun-in" },
                        ["outbound"] = outboundTag
                    },
                    new JsonObject
                    {
                        ["inbound"]  = new JsonArray { "mixed-in" },
                        ["outbound"] = outboundTag
                    }
                }
            }
        };

        // Serialize using Utf8JsonWriter to avoid JsonSerializerOptions.TypeInfoResolver
        // requirement introduced in .NET 8 trimming/AOT mode.
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
        // vmess://base64(json)
        var b64 = uri["vmess://".Length..];
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
            outbound["tls"] = new JsonObject
            {
                ["enabled"]     = true,
                ["server_name"] = v["sni"]?.GetValue<string>() ?? v["add"]?.GetValue<string>() ?? ""
            };
        }

        // Transport
        if (net is "ws" or "websocket")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = v["path"]?.GetValue<string>() ?? "/",
                ["headers"] = new JsonObject
                {
                    ["Host"] = v["host"]?.GetValue<string>() ?? v["add"]?.GetValue<string>() ?? ""
                }
            };
        }
        else if (net is "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"]         = "grpc",
                ["service_name"] = v["path"]?.GetValue<string>() ?? ""
            };
        }
        

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
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = query.GetValueOrDefault("path", "/"),
                ["headers"] = new JsonObject
                {
                    ["Host"] = query.GetValueOrDefault("host", u.Host)
                }
            };
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
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = query.GetValueOrDefault("path", "/"),
                ["headers"] = new JsonObject
                {
                    ["Host"] = query.GetValueOrDefault("host", u.Host)
                }
            };
        }

        return (outbound, tag);
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

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Extracts the proxy server hostname from a V2Ray URI (vmess/vless/trojan/ss).
    /// Returns empty string for raw JSON configs or unparseable input.
    /// </summary>
    private static string ExtractServerHost(string userConfig)
    {
        try
        {
            userConfig = userConfig.Trim();
            if (userConfig.StartsWith("{")) return ""; // raw JSON — no single server
            var uri = new Uri(userConfig.Split('#')[0]); // strip fragment before parsing
            return uri.Host;
        }
        catch { return ""; }
    }

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

        // Step 2: taskkill fallback — catches orphans, crashed-and-restarted instances,
        // or cases where _process was never assigned (e.g. Start() threw).
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "taskkill",
                Arguments              = "/F /IM sing-box.exe",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* sing-box may simply not be running */ }
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
