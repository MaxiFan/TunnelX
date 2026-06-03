using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppTunnel.Models;

namespace AppTunnel.Services;

public class XrayTunnelProvider : ITunnelProvider
{
    private const string TunInterfaceName = "TunnelX-V2Ray";
    private const string TunAddress = "172.18.0.1/30";
    private const string VpnLocalIp = "172.18.0.1";
    private const int DefaultTunMtu = 1500;
    private const int DefaultMixedProxyPort = 2080;
    private const int DefaultXraySocksPort = 2081;
    private const int DefaultXrayLoopbackBridgePort = 2082;
    private const int TunInterfaceWaitSeconds = 20;

    private readonly string _workDir;
    private readonly string _xrayConfigPath;
    private readonly string _singBoxConfigPath;
    private readonly string _xrayExe;
    private readonly string _singBoxExe;

    private Process? _xrayProcess;
    private Process? _singBoxProcess;
    private int _vpnInterfaceIndex = -1;

    public ConnectionStatus Status { get; } = new();

    public XrayTunnelProvider()
    {
        _workDir = NativeEngineSupport.XrayWorkDir;
        _xrayConfigPath = Path.Combine(_workDir, "xray-config.json");
        _singBoxConfigPath = Path.Combine(_workDir, "tun-bridge.json");
        _xrayExe = NativeEngineSupport.ResolveXrayExePath();
        // TUN bridge uses the same sing-box binary path as the V2Ray provider (singbox work dir or side-by-side).
        _singBoxExe = NativeEngineSupport.ResolveSingBoxExePath();
    }

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        Status.State = ConnectionState.Connecting;
        Status.Message = LocalizationService.Instance.T("در حال آماده سازی Xray...");

        try
        {
            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Active, "راه‌اندازی هسته تونل (Xray/V2Ray)");
            Directory.CreateDirectory(_workDir);
            NativeEngineSupport.EnsureWintunBesideEngine(_workDir);
            if (Path.GetDirectoryName(_singBoxExe) is { Length: > 0 } singBoxDir &&
                !singBoxDir.Equals(_workDir, StringComparison.OrdinalIgnoreCase))
                NativeEngineSupport.EnsureWintunBesideEngine(singBoxDir);
            if (Path.GetDirectoryName(_xrayExe) is { Length: > 0 } xrayDir &&
                !xrayDir.Equals(_workDir, StringComparison.OrdinalIgnoreCase))
                NativeEngineSupport.EnsureWintunBesideEngine(xrayDir);

            await NativeEngineSupport.EnsureEmbeddedExecutableAsync("xray.exe", _xrayExe, ct);
            await NativeEngineSupport.EnsureEmbeddedExecutableAsync("sing-box.exe", _singBoxExe, ct);

            if (!File.Exists(_xrayExe))
                return Fail(LocalizationService.Instance.Format("فایل xray.exe پیدا نشد: {0}", _xrayExe));
            if (!File.Exists(_singBoxExe))
                return Fail(LocalizationService.Instance.Format("فایل sing-box.exe پیدا نشد: {0}", _singBoxExe));

            var outbound = BuildXrayOutbound(config.V2RayConfig);
            if (config.EnableDnsOptimization)
                ApplyDnsOptimizationToOutbound(outbound);

            var serverHost = ExtractServerHost(config.V2RayConfig);
            var tunMtu = DefaultTunMtu;
            if (config.AutoTuneMtu)
            {
                tunMtu = await TunnelPerformanceTuner.GetRecommendedTunMtuAsync(
                    serverHost,
                    highOverheadTransport: true,
                    ct);
                Logger.Info($"[MTU] Auto-tuned TUN bridge MTU={tunMtu} (server={serverHost})");
            }
            else
            {
                Logger.Info($"[MTU] Auto-tune disabled; using MTU={tunMtu}");
            }

            using var xraySocksPortReservation = LocalPortReservation.ReservePreferredOrRandom(DefaultXraySocksPort);
            using var loopbackBridgePortReservation = LocalPortReservation.ReservePreferredOrRandom(
                DefaultXrayLoopbackBridgePort,
                xraySocksPortReservation.Port);
            using var mixedProxyPortReservation = LocalPortReservation.ReservePreferredOrRandom(
                DefaultMixedProxyPort,
                xraySocksPortReservation.Port,
                loopbackBridgePortReservation.Port);
            var xraySocksPort = xraySocksPortReservation.Port;
            var loopbackBridgePort = loopbackBridgePortReservation.Port;
            var mixedProxyPort = mixedProxyPortReservation.Port;
            var resolvedServerIp = ExtractOutboundServerAddress(outbound);
            var tlsServerName = ExtractOutboundTlsServerName(outbound);
            Logger.Info(
                $"[PORT] Xray SOCKS=127.0.0.1:{xraySocksPort}, HTTP bridge=127.0.0.1:{loopbackBridgePort}, mixed proxy=127.0.0.1:{mixedProxyPort}");

            var xrayJson = BuildXrayBridgeConfig(outbound, xraySocksPort, loopbackBridgePort);
            var singBoxJson = BuildTunBridgeConfig(
                tunMtu,
                mixedProxyPort,
                xraySocksPort,
                loopbackBridgePort,
                resolvedServerIp,
                tlsServerName,
                serverHost);

            await File.WriteAllTextAsync(_xrayConfigPath, xrayJson, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(_singBoxConfigPath, singBoxJson, new UTF8Encoding(false), ct);

            xraySocksPortReservation.Dispose();
            loopbackBridgePortReservation.Dispose();
            mixedProxyPortReservation.Dispose();

            _xrayProcess = StartProcess(
                _xrayExe,
                $"run -c \"{_xrayConfigPath}\"",
                "[xray]",
                "[xray stderr]");

            Logger.Info($"xray started (PID {_xrayProcess.Id})");
            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Complete, "راه‌اندازی هسته تونل (Xray/V2Ray)");

            ConnectionProgressService.Report("tun_bridge", ConnectionProgressPhase.Active, "راه‌اندازی پل TUN (sing-box)");
            _singBoxProcess = StartProcess(
                _singBoxExe,
                $"run -c \"{_singBoxConfigPath}\"",
                "[sing-box bridge]",
                "[sing-box bridge stderr]");

            Logger.Info($"sing-box TUN bridge started (PID {_singBoxProcess.Id})");
            ConnectionProgressService.Report("tun_bridge", ConnectionProgressPhase.Complete, "راه‌اندازی پل TUN (sing-box)");

            ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Active, "شناسایی آداپتر مجازی");
            Status.Message = LocalizationService.Instance.T("در حال انتظار برای interface TunnelX-V2Ray...");

            var interfaceIndex = await WaitForTunInterfaceAsync(ct);
            if (interfaceIndex <= 0)
            {
                await KillProcessAsync();
                var tunTimeoutMessage = LocalizationService.Instance.Format(
                    "interface TunnelX-V2Ray ظاهر نشد (timeout {0}s)",
                    TunInterfaceWaitSeconds);
                ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Fail, tunTimeoutMessage);
                return Fail(tunTimeoutMessage);
            }

            ConnectionProgressService.Report(
                "tun_interface",
                ConnectionProgressPhase.Complete,
                "شناسایی آداپتر مجازی",
                "آداپتر TunnelX-V2Ray آماده شد (شماره {0})",
                interfaceIndex.ToString());

            _vpnInterfaceIndex = interfaceIndex;
            Status.State = ConnectionState.Connected;
            Status.ConnectedSince = DateTime.Now;
            Status.VpnLocalIp = VpnLocalIp;
            Status.VpnServerHost = ExtractServerHost(config.V2RayConfig);
            Status.VpnServerIp = Status.VpnServerHost;
            Status.VpnServerPort = ExtractServerPort(config.V2RayConfig);
            Status.VpnInterfaceIndex = interfaceIndex;
            Status.SingBoxMixedPort = mixedProxyPort;
            Status.XraySocksInboundPort = xraySocksPort;
            Status.Message = "Xray connected";

            Logger.Info($"Xray tunnel up via sing-box TUN bridge — interface index {interfaceIndex}, server={Status.VpnServerIp}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State = ConnectionState.Disconnected;
            Status.Message = LocalizationService.Instance.T("اتصال لغو شد");
            await KillProcessAsync();
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("XrayTunnelProvider.ConnectAsync failed", ex);
            Status.State = ConnectionState.Error;
            Status.Message = LocalizationService.Instance.Format("خطا: {0}", ex.Message);
            await KillProcessAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        Status.State = ConnectionState.Disconnecting;
        Status.Message = LocalizationService.Instance.T("در حال قطع اتصال Xray...");

        await KillProcessAsync();

        TryDelete(_xrayConfigPath);
        TryDelete(_singBoxConfigPath);

        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Disconnected;
        Status.ConnectedSince = null;
        Status.VpnLocalIp = string.Empty;
        Status.VpnServerHost = string.Empty;
        Status.VpnServerIp = string.Empty;
        Status.VpnServerPort = 0;
        Status.VpnInterfaceIndex = -1;
        Status.SingBoxMixedPort = 0;
        Status.XraySocksInboundPort = 0;
        Status.Message = LocalizationService.Instance.T("قطع شد");
    }

    public bool IsInterfaceUp()
    {
        if (_vpnInterfaceIndex < 0) return false;
        if (_xrayProcess is not { HasExited: false }) return false;
        if (_singBoxProcess is not { HasExited: false }) return false;

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

    private static JsonObject BuildXrayOutbound(string userConfig)
    {
        userConfig = userConfig.Trim();

        if (userConfig.StartsWith("{"))
        {
            var root = JsonNode.Parse(userConfig)?.AsObject()
                ?? throw new InvalidOperationException("JSON نامعتبر است");

            if (root["streamSettings"] != null || root["protocol"] != null)
                return root;

            if (root["outbounds"] is JsonArray outbounds)
            {
                foreach (var item in outbounds.OfType<JsonObject>())
                {
                    var transportType = item["transport"]?["type"]?.GetValue<string>();
                    if (string.Equals(transportType, "xhttp", StringComparison.OrdinalIgnoreCase))
                        return ConvertSingBoxVlessOutboundToXray(item);
                }
            }

            throw new InvalidOperationException("برای Xray باید outbound با transport.type=xhttp یا JSON خروجی Xray وارد شود");
        }

        if (userConfig.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return ParseVmessXrayUri(userConfig);

        if (userConfig.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(userConfig.Split('#')[0]);
            var query = ParseQuery(u.Query);
            var net = query.GetValueOrDefault("type", "tcp");
            if (net.Equals("ws", StringComparison.OrdinalIgnoreCase))
                return ParseVlessWsXrayUri(userConfig);
            if (net.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
                return ParseVlessXhttpUri(userConfig);
        }

        throw new InvalidOperationException(
            "برای Xray باید vmess://، vless:// با type=ws یا xhttp، یا JSON خروجی Xray وارد شود");
    }

    private static JsonObject ParseVmessXrayUri(string uri)
    {
        var b64 = uri["vmess://".Length..].Split('#')[0];
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        var v = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!.AsObject();

        var tag = v["ps"]?.GetValue<string>() ?? "vmess-out";
        var address = v["add"]?.GetValue<string>() ?? "";
        var port = int.TryParse(v["port"]?.ToString(), out var p) ? p : 443;
        var uuid = v["id"]?.GetValue<string>() ?? "";
        var alterId = int.TryParse(v["aid"]?.ToString(), out var aid) ? aid : 0;
        var security = v["scy"]?.GetValue<string>() ?? "auto";

        var wsHost = v["host"]?.GetValue<string>() ?? v["sni"]?.GetValue<string>() ?? address;
        var stream = new JsonObject();
        var net = v["net"]?.GetValue<string>() ?? "tcp";
        if (net is "ws" or "websocket")
        {
            var wsSettings = V2RayWebSocketHelper.CreateXrayWsSettings(wsHost, v["path"]?.GetValue<string>() ?? "/");
            stream["network"] = "ws";
            stream["wsSettings"] = wsSettings;
        }
        else if (net is "grpc")
        {
            stream["network"] = "grpc";
            stream["grpcSettings"] = new JsonObject
            {
                ["serviceName"] = v["path"]?.GetValue<string>() ?? ""
            };
        }

        if (v["tls"]?.GetValue<string>() == "tls")
        {
            stream["security"] = "tls";
            stream["tlsSettings"] = BuildXrayTlsSettings(
                v["sni"]?.GetValue<string>() ?? address,
                v["alpn"]?.GetValue<string>(),
                v["fp"]?.GetValue<string>(),
                v["insecure"]?.ToString() is "1" or "true");
        }

        var (path, ed) = V2RayWebSocketHelper.ParseEarlyDataFromPath(v["path"]?.GetValue<string>() ?? "/");
        Logger.Info($"[CONFIG] Xray VMess WebSocket server={address}:{port} host='{wsHost}' tls.serverName='{v["sni"]?.GetValue<string>() ?? address}' path='{path}'{(ed > 0 ? $" maxEarlyData={ed}" : "")}");

        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = "vmess",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = address,
                        ["port"] = port,
                        ["users"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = uuid,
                                ["alterId"] = alterId,
                                ["security"] = security
                            }
                        }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static JsonObject ParseVlessWsXrayUri(string uri)
    {
        var u = new Uri(uri.Split('#')[0]);
        var query = ParseQuery(u.Query);

        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrWhiteSpace(tag)) tag = "vless-ws";

        var security = query.GetValueOrDefault("security", "tls");
        var sni = query.GetValueOrDefault("sni", u.Host);
        var host = query.GetValueOrDefault("host", sni);
        if (string.IsNullOrWhiteSpace(host))
            host = sni;

        var rawPath = query.GetValueOrDefault("path", "/");
        var wsSettings = V2RayWebSocketHelper.CreateXrayWsSettings(host, rawPath);

        var stream = new JsonObject
        {
            ["network"] = "ws",
            ["security"] = security,
            ["wsSettings"] = wsSettings
        };

        if (security.Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            stream["tlsSettings"] = BuildXrayTlsSettings(
                sni,
                query.GetValueOrDefault("alpn", ""),
                query.GetValueOrDefault("fp", ""),
                query.GetValueOrDefault("allowInsecure", "0") == "1" ||
                query.GetValueOrDefault("insecure", "0") == "1");
        }
        else if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = sni,
                ["publicKey"] = query.GetValueOrDefault("pbk", ""),
                ["shortId"] = query.GetValueOrDefault("sid", ""),
                ["fingerprint"] = V2RayWebSocketHelper.NormalizeFingerprint(query.GetValueOrDefault("fp", "chrome"))
            };
        }

        var (pathOnly, ed) = V2RayWebSocketHelper.ParseEarlyDataFromPath(rawPath);
        Logger.Info($"[CONFIG] Xray VLESS WebSocket server={u.Host}:{(u.Port > 0 ? u.Port : 443)} host='{host}' tls.serverName='{sni}' path='{pathOnly}'{(ed > 0 ? $" maxEarlyData={ed}" : "")}");

        // VLESS flow (xtls-rprx-vision, etc.) is invalid on WebSocket; omit to match v2rayNG WS profiles.
        return BuildVlessOutbound(tag, u.Host, u.Port > 0 ? u.Port : 443, u.UserInfo, stream, flow: null);
    }

    private static JsonObject BuildXrayTlsSettings(string serverName, string? alpnCsv, string? fingerprint, bool allowInsecure)
    {
        var tlsSettings = new JsonObject
        {
            ["serverName"] = serverName,
            ["allowInsecure"] = allowInsecure
        };

        if (!string.IsNullOrWhiteSpace(alpnCsv))
        {
            var alpnList = alpnCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !p.Equals("h3", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (alpnList.Count > 0)
            {
                tlsSettings["alpn"] = new JsonArray(
                    alpnList.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
            }
        }

        var fp = V2RayWebSocketHelper.NormalizeFingerprint(fingerprint);
        if (!string.IsNullOrWhiteSpace(fp))
            tlsSettings["fingerprint"] = fp;

        return tlsSettings;
    }

    private static string BuildXrayBridgeConfig(JsonObject outbound, int xraySocksPort, int loopbackBridgePort)
    {
        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["policy"] = new JsonObject
            {
                ["levels"] = new JsonObject
                {
                    ["0"] = new JsonObject
                    {
                        ["handshake"] = 12,
                        ["connIdle"] = 600
                    }
                }
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = xraySocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["udp"] = true,
                        ["auth"] = "noauth"
                    }
                },
                new JsonObject
                {
                    ["tag"] = "http-bridge",
                    ["listen"] = "127.0.0.1",
                    ["port"] = loopbackBridgePort,
                    ["protocol"] = "http",
                    ["settings"] = new JsonObject
                    {
                        ["timeout"] = 600,
                        ["allowTransparent"] = false
                    },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray { "http", "tls", "quic" }
                    }
                }
            },
            ["outbounds"] = new JsonArray { outbound }
        };

        return JsonString(doc);
    }

    private static string? ExtractOutboundServerAddress(JsonObject outbound)
        => outbound["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>();

    private static string? ExtractOutboundTlsServerName(JsonObject outbound)
    {
        var tlsSni = outbound["streamSettings"]?["tlsSettings"]?["serverName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(tlsSni))
            return tlsSni;

        return outbound["streamSettings"]?["xhttpSettings"]?["host"]?.GetValue<string>();
    }

    private static IEnumerable<string> CollectVpnServerBypassDomains(string serverHost, string? tlsServerName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { serverHost, tlsServerName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (System.Net.IPAddress.TryParse(candidate, out _))
                continue;
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private static void ApplyDnsOptimizationToOutbound(JsonObject outbound)
    {
        try
        {
            var network = outbound["streamSettings"]?["network"]?.GetValue<string>() ?? "";
            if (network.Equals("ws", StringComparison.OrdinalIgnoreCase))
                return;

            var addressNode = outbound["settings"]?["vnext"]?[0]?["address"];
            var server = addressNode?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(server) || System.Net.IPAddress.TryParse(server, out _))
                return;

            var v4 = DnsResolverCache.ResolveFirstIpv4(server);
            if (v4 == null) return;

            outbound["settings"]!["vnext"]![0]!["address"] = v4.ToString();
            Logger.Info($"[CONFIG] Pre-resolved xray server '{server}' → {v4} (SNI preserved)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CONFIG] Could not pre-resolve xray server: {ex.Message}");
        }
    }

    private static string BuildTunBridgeConfig(
        int tunMtu,
        int mixedProxyPort,
        int xraySocksPort,
        int loopbackBridgePort,
        string? resolvedServerIp,
        string? tlsServerName,
        string serverHost)
    {
        var routeRules = new JsonArray
        {
            new JsonObject
            {
                ["ip_cidr"] = new JsonArray { "127.0.0.0/8" },
                ["outbound"] = "direct"
            }
        };

        if (!string.IsNullOrWhiteSpace(resolvedServerIp) &&
            System.Net.IPAddress.TryParse(resolvedServerIp, out _))
        {
            routeRules.Add(new JsonObject
            {
                ["ip_cidr"] = new JsonArray { $"{resolvedServerIp}/32" },
                ["outbound"] = "direct"
            });
        }

        foreach (var domain in CollectVpnServerBypassDomains(serverHost, tlsServerName))
        {
            routeRules.Add(new JsonObject
            {
                ["domain"] = new JsonArray { domain },
                ["outbound"] = "direct"
            });
        }

        // HTTP bridge handles TUN TCP only; SOCKS carries TUN UDP (DNS/QUIC) to Xray.
        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray { "tun-in" },
            ["ip_is_private"] = true,
            ["outbound"] = "direct"
        });
        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray { "tun-in" },
            ["network"] = new JsonArray { "udp" },
            ["outbound"] = "xray-socks"
        });
        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray { "tun-in" },
            ["network"] = new JsonArray { "tcp" },
            ["outbound"] = "xray-http-bridge"
        });
        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray { "mixed-in" },
            ["outbound"] = "xray-socks"
        });

        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = false },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"] = new JsonArray { TunAddress },
                    ["mtu"] = TunnelPerformanceTuner.ClampTunMtu(tunMtu),
                    ["auto_route"] = false,
                    ["strict_route"] = false,
                    ["stack"] = "gvisor",
                    ["sniff"] = true
                },
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = mixedProxyPort
                }
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "http",
                    ["tag"] = "xray-http-bridge",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = loopbackBridgePort
                },
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "xray-socks",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = xraySocksPort,
                    ["version"] = "5"
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["rules"] = routeRules
            }
        };

        return JsonString(doc);
    }

    private static JsonObject ConvertSingBoxVlessOutboundToXray(JsonObject singBoxOutbound)
    {
        var server = singBoxOutbound["server"]?.GetValue<string>() ?? "";
        var port = singBoxOutbound["server_port"]?.GetValue<int>() ?? 443;
        var uuid = singBoxOutbound["uuid"]?.GetValue<string>() ?? "";
        var tag = singBoxOutbound["tag"]?.GetValue<string>() ?? "vless-xhttp";
        var transport = singBoxOutbound["transport"]?.AsObject()
            ?? throw new InvalidOperationException("transport xhttp پیدا نشد");
        var tls = singBoxOutbound["tls"]?.AsObject();

        var stream = new JsonObject
        {
            ["network"] = "xhttp",
            ["security"] = tls != null ? "tls" : "none",
            ["xhttpSettings"] = BuildXhttpSettings(
                transport["path"]?.GetValue<string>() ?? "/",
                ReadSingBoxHost(transport, tls?["server_name"]?.GetValue<string>() ?? server),
                transport["mode"]?.GetValue<string>(),
                transport["headers"]?.DeepClone(),
                transport["extra"]?.DeepClone())
        };

        if (tls != null)
        {
            var tlsSettings = new JsonObject
            {
                ["serverName"] = tls["server_name"]?.GetValue<string>() ?? server
            };
            if (tls["alpn"] is JsonArray alpn)
                tlsSettings["alpn"] = alpn.DeepClone();
            stream["tlsSettings"] = tlsSettings;
        }

        return BuildVlessOutbound(tag, server, port, uuid, stream, singBoxOutbound["flow"]?.GetValue<string>());
    }

    private static JsonObject ParseVlessXhttpUri(string uri)
    {
        var u = new Uri(uri);
        var query = ParseQuery(u.Query);

        var tag = Uri.UnescapeDataString(u.Fragment.TrimStart('#').Trim());
        if (string.IsNullOrWhiteSpace(tag)) tag = "vless-xhttp";

        var security = query.GetValueOrDefault("security", "tls");
        var sni = query.GetValueOrDefault("sni", u.Host);
        var host = query.GetValueOrDefault("host", sni);
        if (string.IsNullOrWhiteSpace(host))
            host = sni;

        var stream = new JsonObject
        {
            ["network"] = "xhttp",
            ["security"] = security,
            ["xhttpSettings"] = BuildXhttpSettings(
                query.GetValueOrDefault("path", "/"),
                host,
                GetQueryValue(query, "mode"),
                null,
                query: query)
        };

        if (security.Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            var tlsSettings = new JsonObject
            {
                ["serverName"] = sni,
                ["allowInsecure"] = query.GetValueOrDefault("allowInsecure", "0") == "1"
            };

            var alpn = query.GetValueOrDefault("alpn", "");
            if (!string.IsNullOrWhiteSpace(alpn))
            {
                tlsSettings["alpn"] = new JsonArray(
                    alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => JsonValue.Create(v))
                        .ToArray<JsonNode?>());
            }

            stream["tlsSettings"] = tlsSettings;
        }
        else if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = sni,
                ["publicKey"] = query.GetValueOrDefault("pbk", ""),
                ["shortId"] = query.GetValueOrDefault("sid", ""),
                ["fingerprint"] = query.GetValueOrDefault("fp", "chrome")
            };
        }

        return BuildVlessOutbound(tag, u.Host, u.Port > 0 ? u.Port : 443, u.UserInfo, stream, GetQueryValue(query, "flow"));
    }

    private static JsonObject BuildVlessOutbound(string tag, string server, int port, string uuid, JsonObject stream, string? flow)
    {
        var user = new JsonObject
        {
            ["id"] = uuid,
            ["encryption"] = "none"
        };
        if (!string.IsNullOrWhiteSpace(flow))
            user["flow"] = flow;

        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = server,
                        ["port"] = port,
                        ["users"] = new JsonArray { user }
                    }
                }
            },
            ["streamSettings"] = stream
        };
    }

    private static JsonObject BuildXhttpSettings(
        string path,
        string host,
        string? mode,
        JsonNode? headers,
        JsonNode? transportExtra = null,
        IReadOnlyDictionary<string, string>? query = null)
    {
        var settings = new JsonObject
        {
            ["path"] = string.IsNullOrWhiteSpace(path) ? "/" : path,
            ["host"] = host
        };

        if (!string.IsNullOrWhiteSpace(mode))
            settings["mode"] = mode;
        if (headers != null)
            settings["headers"] = headers;

        var extra = transportExtra?.DeepClone() ?? BuildXhttpExtraFromQuery(query);
        if (extra != null)
            settings["extra"] = extra;

        return settings;
    }

    private static JsonNode? BuildXhttpExtraFromQuery(IReadOnlyDictionary<string, string>? query)
    {
        if (query == null)
            return null;

        var padding = TryGetQueryValue(query, "x_padding_bytes");
        if (string.IsNullOrWhiteSpace(padding))
            padding = TryGetQueryValue(query, "xPaddingBytes");

        if (!string.IsNullOrWhiteSpace(padding))
            return new JsonObject { ["xPaddingBytes"] = padding };

        var extraRaw = TryGetQueryValue(query, "extra");
        if (string.IsNullOrWhiteSpace(extraRaw))
            return null;

        try
        {
            return JsonNode.Parse(Uri.UnescapeDataString(extraRaw))?.DeepClone();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetQueryValue(IReadOnlyDictionary<string, string> query, string key)
        => query.TryGetValue(key, out var value) ? value : null;

    private static string ReadSingBoxHost(JsonObject transport, string fallback)
    {
        if (transport["host"] is JsonArray hostArray &&
            hostArray.FirstOrDefault() is JsonValue first &&
            first.TryGetValue<string>(out var firstHost) &&
            !string.IsNullOrWhiteSpace(firstHost))
            return firstHost;

        if (transport["host"] is JsonValue hostValue &&
            hostValue.TryGetValue<string>(out var host) &&
            !string.IsNullOrWhiteSpace(host))
            return host;

        if (transport["headers"]?["Host"] is JsonValue headerValue &&
            headerValue.TryGetValue<string>(out var headerHost) &&
            !string.IsNullOrWhiteSpace(headerHost))
            return headerHost;

        return fallback;
    }

    private async Task<int> WaitForTunInterfaceAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(TunInterfaceWaitSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_xrayProcess?.HasExited == true)
                throw new InvalidOperationException(LocalizationService.Instance.Format("xray زودتر خارج شد (exit code {0})", _xrayProcess.ExitCode));
            if (_singBoxProcess?.HasExited == true)
                throw new InvalidOperationException(LocalizationService.Instance.Format("sing-box bridge زودتر خارج شد (exit code {0})", _singBoxProcess.ExitCode));

            var idx = FindInterfaceIndex(TunInterfaceName);
            if (idx > 0) return idx;
            await Task.Delay(500, ct);
        }

        return -1;
    }

    private static Process StartProcess(string fileName, string arguments, string stdoutPrefix, string stderrPrefix)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Logger.ProcessOutput(stdoutPrefix, e.Data, isError: false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Logger.ProcessOutput(stderrPrefix, e.Data, isError: true);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task KillProcessAsync()
    {
        await KillTrackedProcessAsync(_singBoxProcess);
        _singBoxProcess = null;

        await KillTrackedProcessAsync(_xrayProcess);
        _xrayProcess = null;

        KillByImageName("sing-box.exe");
        KillByImageName("xray.exe");
    }

    private static void KillByImageName(string imageName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /IM {imageName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    private static async Task KillTrackedProcessAsync(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cts.Token); } catch { }
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }

    private bool Fail(string message)
    {
        Status.State = ConnectionState.Error;
        Status.Message = message;
        Logger.Error(message);
        return false;
    }

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

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[key] = value;
        }

        return dict;
    }

    private static string? GetQueryValue(Dictionary<string, string> query, string key)
        => query.TryGetValue(key, out var value) ? value : null;

    private static string JsonString(JsonObject doc)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
