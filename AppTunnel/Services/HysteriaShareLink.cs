using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace AppTunnel.Services;

/// <summary>
/// Hysteria 1 and Hysteria 2 share links and sing-box JSON for the bundled sing-box 1.12 core.
/// Xray-core has no Hysteria outbound; these configs stay on sing-box.
/// </summary>
public static class HysteriaShareLink
{
    public const string InvalidLinkMessage = "لینک Hysteria نامعتبر است";
    public const string UdpOnlyMessage = "پروتکل Hysteria غیر از UDP در sing-box پشتیبانی نمی‌شود";
    public const string Hy2ObfsTypeMessage = "obfs هیستوریا ۲ فقط salamander است";
    public const string Hy2ObfsPasswordMessage = "رمز obfs هیستوریا ۲ وارد نشده است";
    public const string Hy1ObfsPasswordMessage = "رمز obfs هیستوریا ۱ وارد نشده است";

    public static bool IsShareLink(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return false;

        config = config.Trim();
        return config.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("hysteria://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHysteriaType(string? type) =>
        type != null &&
        (type.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));

    public static bool IsHysteria(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return false;

        config = config.Trim();
        if (IsShareLink(config))
            return true;

        if (!config.StartsWith('{'))
            return false;

        try
        {
            var root = JsonNode.Parse(config)?.AsObject();
            if (root == null)
                return false;

            if (IsHysteriaType(TryGetString(root, "type")))
                return true;

            if (root["outbounds"] is JsonArray outbounds)
            {
                foreach (var item in outbounds.OfType<JsonObject>())
                {
                    if (IsHysteriaType(TryGetString(item, "type")))
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Builds a sing-box hysteria/hysteria2 outbound from a bare outbound or from a document
    /// that has no inbounds yet. Documents that already declare inbounds are left untouched.
    /// </summary>
    public static bool TryCreateOutbound(string json, out JsonObject outbound, out string tag)
    {
        outbound = null!;
        tag = "";

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null || HasInbounds(root))
                return false;

            JsonObject? source = null;
            if (IsHysteriaType(TryGetString(root, "type")))
                source = root;
            else if (root["outbounds"] is JsonArray outbounds)
            {
                source = outbounds.OfType<JsonObject>()
                    .FirstOrDefault(item => IsHysteriaType(TryGetString(item, "type")));
            }

            if (source == null || string.IsNullOrWhiteSpace(TryGetString(source, "server")))
                return false;

            outbound = source.DeepClone().AsObject();
            var type = TryGetString(outbound, "type");
            outbound["type"] = type != null && type.Equals("hysteria", StringComparison.OrdinalIgnoreCase)
                ? "hysteria"
                : "hysteria2";
            EnsureTag(outbound, out tag);
            EnsureTlsDefaults(outbound);
            return true;
        }
        catch
        {
            outbound = null!;
            tag = "";
            return false;
        }
    }

    public static (JsonObject Outbound, string Tag) Parse(string uriText)
    {
        try
        {
            if (!TrySplit(uriText, out var scheme, out var uri, out var tag))
                throw new InvalidOperationException(InvalidLinkMessage);

            var query = ParseQuery(uri.Query);
            WarnIfPinIgnored(query);
            return scheme == "hysteria"
                ? ParseV1(uri, query, tag)
                : ParseV2(uri, query, tag);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(InvalidLinkMessage, ex);
        }
    }

    private static (JsonObject Outbound, string Tag) ParseV2(
        Uri uri,
        IReadOnlyDictionary<string, string> query,
        string tag)
    {
        var host = NormalizeHost(uri.Host);
        var port = uri.Port > 0 ? uri.Port : 443;
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new InvalidOperationException(InvalidLinkMessage);

        if (string.IsNullOrWhiteSpace(tag))
            tag = "hysteria2-out";

        var password = DecodeUser(uri);
        if (string.IsNullOrEmpty(password))
            password = FirstQuery(query, "auth", "password") ?? "";

        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = tag,
            ["server"] = host,
            ["server_port"] = port
        };

        if (!string.IsNullOrEmpty(password))
            outbound["password"] = password;

        ApplyOptionalMbps(outbound, query);
        ApplyObfsV2(outbound, query);
        ApplyHop(outbound, query);
        outbound["tls"] = BuildTls(host, query);
        return (outbound, tag);
    }

    private static (JsonObject Outbound, string Tag) ParseV1(
        Uri uri,
        IReadOnlyDictionary<string, string> query,
        string tag)
    {
        var protocol = FirstQuery(query, "protocol");
        if (!string.IsNullOrWhiteSpace(protocol) &&
            !protocol.Equals("udp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UdpOnlyMessage);

        var host = NormalizeHost(uri.Host);
        var port = uri.Port > 0 ? uri.Port : 443;
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new InvalidOperationException(InvalidLinkMessage);

        if (string.IsNullOrWhiteSpace(tag))
            tag = "hysteria-out";

        var auth = FirstQuery(query, "auth", "auth_str");
        if (string.IsNullOrEmpty(auth))
            auth = DecodeUser(uri);

        var outbound = new JsonObject
        {
            ["type"] = "hysteria",
            ["tag"] = tag,
            ["server"] = host,
            ["server_port"] = port
        };

        if (!string.IsNullOrEmpty(auth))
            outbound["auth_str"] = auth;

        var up = TryParseMbps(query, "upmbps", "up") ?? 100;
        var down = TryParseMbps(query, "downmbps", "down") ?? 100;
        if (up > 0)
            outbound["up_mbps"] = up;
        if (down > 0)
            outbound["down_mbps"] = down;

        ApplyObfsV1(outbound, query);
        ApplyHop(outbound, query);
        outbound["tls"] = BuildTls(host, query);
        return (outbound, tag);
    }

    private static void ApplyObfsV2(JsonObject outbound, IReadOnlyDictionary<string, string> query)
    {
        var obfsType = FirstQuery(query, "obfs");
        var obfsPassword = FirstQuery(query, "obfs-password", "obfsPassword", "obfs_password");
        if (string.IsNullOrWhiteSpace(obfsType) && string.IsNullOrWhiteSpace(obfsPassword))
            return;

        if (!string.IsNullOrWhiteSpace(obfsType) &&
            !obfsType.Equals("salamander", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Hy2ObfsTypeMessage);

        if (string.IsNullOrWhiteSpace(obfsPassword))
            throw new InvalidOperationException(Hy2ObfsPasswordMessage);

        outbound["obfs"] = new JsonObject
        {
            ["type"] = "salamander",
            ["password"] = obfsPassword
        };
    }

    private static void ApplyObfsV1(JsonObject outbound, IReadOnlyDictionary<string, string> query)
    {
        var obfsParam = FirstQuery(query, "obfsParam", "obfs-password", "obfs_password");
        var obfs = FirstQuery(query, "obfs");
        if (string.IsNullOrWhiteSpace(obfs) && string.IsNullOrWhiteSpace(obfsParam))
            return;

        var password = !string.IsNullOrWhiteSpace(obfsParam)
            ? obfsParam
            : obfs!.Equals("xplus", StringComparison.OrdinalIgnoreCase) ? "" : obfs;
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(Hy1ObfsPasswordMessage);

        outbound["obfs"] = password;
    }

    private static void ApplyOptionalMbps(JsonObject outbound, IReadOnlyDictionary<string, string> query)
    {
        var up = TryParseMbps(query, "upmbps", "up");
        var down = TryParseMbps(query, "downmbps", "down");
        if (up is > 0)
            outbound["up_mbps"] = up.Value;
        if (down is > 0)
            outbound["down_mbps"] = down.Value;
    }

    private static void ApplyHop(JsonObject outbound, IReadOnlyDictionary<string, string> query)
    {
        var ports = ParseServerPorts(FirstQuery(query, "mport", "ports", "server_ports"));
        if (ports != null)
            outbound["server_ports"] = ports;

        var hop = NormalizeDuration(FirstQuery(query, "hopInterval", "hop-interval", "hop_interval"));
        if (!string.IsNullOrWhiteSpace(hop))
            outbound["hop_interval"] = hop;
    }

    private static JsonObject BuildTls(string host, IReadOnlyDictionary<string, string> query)
    {
        var tls = new JsonObject { ["enabled"] = true };
        var sni = FirstQuery(query, "sni", "peer", "serverName", "server_name");
        if (string.IsNullOrWhiteSpace(sni) && !IPAddress.TryParse(host, out _))
            sni = host;
        if (!string.IsNullOrWhiteSpace(sni))
            tls["server_name"] = sni;

        if (IsEnabled(query, "insecure") || IsEnabled(query, "allowInsecure") || IsEnabled(query, "allow_insecure"))
            tls["insecure"] = true;

        var alpnRaw = FirstQuery(query, "alpn");
        var alpn = string.IsNullOrWhiteSpace(alpnRaw)
            ? ["h3"]
            : alpnRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (alpn.Length > 0)
        {
            var list = new JsonArray();
            foreach (var item in alpn)
                list.Add(item);
            tls["alpn"] = list;
        }

        return tls;
    }

    private static void EnsureTag(JsonObject outbound, out string tag)
    {
        tag = TryGetString(outbound, "tag") ?? "";
        if (!string.IsNullOrWhiteSpace(tag))
            return;

        tag = string.Equals(TryGetString(outbound, "type"), "hysteria", StringComparison.OrdinalIgnoreCase)
            ? "hysteria-out"
            : "hysteria2-out";
        outbound["tag"] = tag;
    }

    private static void EnsureTlsDefaults(JsonObject outbound)
    {
        if (outbound["tls"] is not JsonObject tls)
        {
            tls = new JsonObject();
            outbound["tls"] = tls;
        }

        if (tls["enabled"] == null)
            tls["enabled"] = true;

        if (string.IsNullOrWhiteSpace(TryGetString(tls, "server_name")))
        {
            var server = TryGetString(outbound, "server") ?? "";
            if (!string.IsNullOrWhiteSpace(server) && !IPAddress.TryParse(server, out _))
                tls["server_name"] = server;
        }

        if (tls["alpn"] == null)
            tls["alpn"] = new JsonArray { "h3" };
    }

    private static bool HasInbounds(JsonObject root) =>
        root["inbounds"] is JsonArray inbounds && inbounds.Count > 0;

    private static bool TrySplit(string text, out string scheme, out Uri uri, out string tag)
    {
        scheme = "";
        uri = null!;
        tag = "";
        text = text.Trim();

        var hash = text.IndexOf('#');
        var fragment = "";
        if (hash >= 0)
        {
            fragment = text[(hash + 1)..];
            text = text[..hash];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
            return false;

        scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme is not ("hysteria" or "hysteria2" or "hy2"))
            return false;

        uri = parsed;
        tag = string.IsNullOrWhiteSpace(fragment) ? "" : Uri.UnescapeDataString(fragment).Trim();
        return true;
    }

    private static string DecodeUser(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) ? "" : Uri.UnescapeDataString(uri.UserInfo);

    private static string NormalizeHost(string host)
    {
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            return host[1..^1];
        return host;
    }

    private static void WarnIfPinIgnored(IReadOnlyDictionary<string, string> query)
    {
        if (query.ContainsKey("pinSHA256"))
            Logger.Warning("[CONFIG] Hysteria pinSHA256 is ignored; bundled sing-box 1.12 has no certificate pin field");
    }

    private static JsonArray? ParseServerPorts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var items = new JsonArray();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = part.Contains('-', StringComparison.Ordinal) && !part.Contains(':', StringComparison.Ordinal)
                ? part.Replace('-', ':')
                : part;
            if (!string.IsNullOrWhiteSpace(normalized))
                items.Add(normalized);
        }

        return items.Count == 0 ? null : items;
    }

    private static string? NormalizeDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? raw + "s"
            : raw;
    }

    private static int? TryParseMbps(IReadOnlyDictionary<string, string> query, params string[] keys)
    {
        var raw = FirstQuery(query, keys);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var token = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var digits = new string(token.TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps) && mbps >= 0
            ? mbps
            : null;
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> query, string key)
    {
        var value = FirstQuery(query, key);
        return value != null && value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value != null && value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value != null && value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value != null && value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstQuery(IReadOnlyDictionary<string, string> query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return dict;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                continue;

            var key = Uri.UnescapeDataString(part[..eq]);
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[key] = val;
        }

        return dict;
    }

    private static string? TryGetString(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }
}
