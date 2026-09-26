using System.Text;
using System.Text.Json.Nodes;

namespace AppTunnel.Services;

/// <summary>
/// Parses V2Ray share links (vmess/vless/trojan/ss/hysteria/…) for server host/port used in status and routing.
/// </summary>
internal static class V2RayEndpointHelper
{
    public static string ExtractServerHost(string userConfig)
    {
        return TryExtract(userConfig, out var host, out _) ? host : "";
    }

    public static int ExtractServerPort(string userConfig)
    {
        return TryExtract(userConfig, out _, out var port) ? port : 0;
    }

    public static bool TryExtract(string userConfig, out string host, out int port)
    {
        host = "";
        port = 443;

        try
        {
            userConfig = userConfig.Trim();
            if (string.IsNullOrEmpty(userConfig))
                return false;

            if (userConfig.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var node = TryParseVmessJson(userConfig);
                if (node == null)
                    return false;

                host = node["add"]?.GetValue<string>() ?? "";
                if (int.TryParse(node["port"]?.ToString(), out var p) && p is > 0 and <= 65535)
                    port = p;
                return !string.IsNullOrWhiteSpace(host);
            }

            if (userConfig.StartsWith("{"))
            {
                var root = JsonNode.Parse(userConfig)?.AsObject();
                if (root?["outbounds"] is JsonArray outbounds)
                {
                    foreach (var item in outbounds.OfType<JsonObject>())
                    {
                        var server = item["server"]?.GetValue<string>() ??
                                     item["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(server))
                            continue;

                        host = server;
                        var parsedPort = item["server_port"]?.GetValue<int>() ??
                                         item["settings"]?["vnext"]?[0]?["port"]?.GetValue<int>();
                        if (parsedPort is > 0 and <= 65535)
                            port = parsedPort.Value;
                        return true;
                    }
                }

                if (HysteriaShareLink.IsHysteriaType(root?["type"]?.GetValue<string>()) &&
                    !string.IsNullOrWhiteSpace(root?["server"]?.GetValue<string>()))
                {
                    host = root!["server"]!.GetValue<string>();
                    var barePort = root["server_port"]?.GetValue<int>();
                    if (barePort is > 0 and <= 65535)
                        port = barePort.Value;
                    return true;
                }

                return false;
            }

            var uri = new Uri(userConfig.Split('#')[0]);
            host = uri.Host;
            if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
                host = host[1..^1];
            if (uri.Port > 0)
                port = uri.Port;
            else if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                port = 3128;
            else if (uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) ||
                     uri.Scheme.Equals("socks", StringComparison.OrdinalIgnoreCase))
                port = 1080;

            return !string.IsNullOrWhiteSpace(host);
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject? TryParseVmessJson(string uri)
    {
        var b64 = uri["vmess://".Length..].Split('#')[0];
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        return JsonNode.Parse(json)?.AsObject();
    }
}
