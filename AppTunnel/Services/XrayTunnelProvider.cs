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
    private const int MixedProxyPort = 2080;
    private const int XraySocksPort = 2081;

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
        _workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TunnelX",
            "xray");

        _xrayConfigPath = Path.Combine(_workDir, "xray-config.json");
        _singBoxConfigPath = Path.Combine(_workDir, "tun-bridge.json");

        var xraySideBySide = Path.Combine(AppContext.BaseDirectory, "xray.exe");
        _xrayExe = File.Exists(xraySideBySide)
            ? xraySideBySide
            : Path.Combine(_workDir, "xray.exe");

        var singBoxSideBySide = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        _singBoxExe = File.Exists(singBoxSideBySide)
            ? singBoxSideBySide
            : Path.Combine(_workDir, "sing-box.exe");
    }

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        Status.State = ConnectionState.Connecting;
        Status.Message = "در حال آماده سازی Xray...";

        try
        {
            Directory.CreateDirectory(_workDir);
            await EnsureEmbeddedExeExtractedAsync("xray.exe", _xrayExe, ct);
            await EnsureEmbeddedExeExtractedAsync("sing-box.exe", _singBoxExe, ct);

            if (!File.Exists(_xrayExe))
                return Fail($"فایل xray.exe پیدا نشد: {_xrayExe}");
            if (!File.Exists(_singBoxExe))
                return Fail($"فایل sing-box.exe پیدا نشد: {_singBoxExe}");

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

            var xrayJson = BuildXraySocksConfig(outbound);
            var singBoxJson = BuildTunBridgeConfig(tunMtu);

            await File.WriteAllTextAsync(_xrayConfigPath, xrayJson, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(_singBoxConfigPath, singBoxJson, new UTF8Encoding(false), ct);

            _xrayProcess = StartProcess(
                _xrayExe,
                $"run -c \"{_xrayConfigPath}\"",
                "[xray]",
                "[xray stderr]");

            Logger.Info($"xray started (PID {_xrayProcess.Id})");

            _singBoxProcess = StartProcess(
                _singBoxExe,
                $"run -c \"{_singBoxConfigPath}\"",
                "[sing-box bridge]",
                "[sing-box bridge stderr]");

            Logger.Info($"sing-box TUN bridge started (PID {_singBoxProcess.Id})");
            Status.Message = "در حال انتظار برای interface TunnelX-V2Ray...";

            var interfaceIndex = await WaitForTunInterfaceAsync(ct);
            if (interfaceIndex <= 0)
            {
                await KillProcessAsync();
                return Fail("interface TunnelX-V2Ray ظاهر نشد (timeout 10s)");
            }

            _vpnInterfaceIndex = interfaceIndex;
            Status.State = ConnectionState.Connected;
            Status.ConnectedSince = DateTime.Now;
            Status.VpnLocalIp = VpnLocalIp;
            Status.VpnServerIp = ExtractServerHost(config.V2RayConfig);
            Status.VpnInterfaceIndex = interfaceIndex;
            Status.SingBoxMixedPort = MixedProxyPort;
            Status.Message = "Xray connected";

            Logger.Info($"Xray tunnel up via sing-box TUN bridge — interface index {interfaceIndex}, server={Status.VpnServerIp}");
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
            Logger.Error("XrayTunnelProvider.ConnectAsync failed", ex);
            Status.State = ConnectionState.Error;
            Status.Message = $"خطا: {ex.Message}";
            await KillProcessAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        Status.State = ConnectionState.Disconnecting;
        Status.Message = "در حال قطع اتصال Xray...";

        await KillProcessAsync();

        TryDelete(_xrayConfigPath);
        TryDelete(_singBoxConfigPath);

        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Disconnected;
        Status.ConnectedSince = null;
        Status.VpnLocalIp = string.Empty;
        Status.VpnInterfaceIndex = -1;
        Status.SingBoxMixedPort = 0;
        Status.Message = "قطع شد";
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

        if (userConfig.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return ParseVlessXhttpUri(userConfig);

        throw new InvalidOperationException("xhttp فعلا فقط برای vless:// یا JSON دارای outbound xhttp پشتیبانی می‌شود");
    }

    private static string BuildXraySocksConfig(JsonObject outbound)
    {
        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = XraySocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["udp"] = true,
                        ["auth"] = "noauth"
                    }
                }
            },
            ["outbounds"] = new JsonArray { outbound }
        };

        return JsonString(doc);
    }

    private static void ApplyDnsOptimizationToOutbound(JsonObject outbound)
    {
        try
        {
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

    private static string BuildTunBridgeConfig(int tunMtu)
    {
        var doc = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
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
                    ["stack"] = "system"
                },
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = MixedProxyPort
                }
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "xray-socks",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = XraySocksPort,
                    ["version"] = "5"
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inbound"] = new JsonArray { "tun-in" },
                        ["outbound"] = "xray-socks"
                    },
                    new JsonObject
                    {
                        ["inbound"] = new JsonArray { "mixed-in" },
                        ["outbound"] = "xray-socks"
                    }
                }
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
                transport["headers"]?.DeepClone())
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
                null)
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

    private static JsonObject BuildXhttpSettings(string path, string host, string? mode, JsonNode? headers)
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

        return settings;
    }

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
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_xrayProcess?.HasExited == true)
                throw new InvalidOperationException($"xray زودتر خارج شد (exit code {_xrayProcess.ExitCode})");
            if (_singBoxProcess?.HasExited == true)
                throw new InvalidOperationException($"sing-box bridge زودتر خارج شد (exit code {_singBoxProcess.ExitCode})");

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

    private static async Task EnsureEmbeddedExeExtractedAsync(string resourceName, string targetPath, CancellationToken ct)
    {
        if (!targetPath.StartsWith(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunnelX"),
                StringComparison.OrdinalIgnoreCase))
            return;

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Logger.Warning($"[{resourceName}] Embedded resource not found.");
            return;
        }

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == stream.Length)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await stream.CopyToAsync(fs, 81920, ct);
    }

    private bool Fail(string message)
    {
        Status.State = ConnectionState.Error;
        Status.Message = message;
        Logger.Error(message);
        return false;
    }

    private static string ExtractServerHost(string userConfig)
    {
        try
        {
            userConfig = userConfig.Trim();
            if (!userConfig.StartsWith("{"))
                return new Uri(userConfig.Split('#')[0]).Host;

            var root = JsonNode.Parse(userConfig)?.AsObject();
            if (root?["outbounds"] is JsonArray outbounds)
            {
                foreach (var item in outbounds.OfType<JsonObject>())
                {
                    var server = item["server"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(server)) return server;

                    var address = item["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(address)) return address;
                }
            }
        }
        catch { }

        return "";
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
