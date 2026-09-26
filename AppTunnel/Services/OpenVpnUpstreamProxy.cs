using System.Globalization;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>OpenVPN upstream proxy saved on a profile and written into the prepared config.</summary>
public readonly record struct OpenVpnUpstreamProxySettings(
    OpenVpnUpstreamProxyKind Kind,
    string Host,
    int Port,
    string Username,
    string Password)
{
    public bool IsEnabled => Kind != OpenVpnUpstreamProxyKind.None;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

    public static OpenVpnUpstreamProxySettings From(
        OpenVpnUpstreamProxyKind kind,
        string? host,
        int port,
        string? username,
        string? password) =>
        new(kind, host ?? "", port, username ?? "", password ?? "");
}

/// <summary>Proxy directive found inside a user .ovpn file.</summary>
public readonly record struct OpenVpnParsedUpstreamProxy(
    OpenVpnUpstreamProxyKind Kind,
    string Host,
    int Port,
    string Username,
    string Password,
    bool HasInlineCredentials,
    bool ReferencesExternalAuthFile);

/// <summary>
/// Builds OpenVPN <c>http-proxy</c> / <c>socks-proxy</c> lines and reads the same directives from .ovpn text.
/// HTTP and SOCKS upstream proxies only carry TCP OpenVPN connections.
/// </summary>
public static class OpenVpnUpstreamProxy
{
    public const int DefaultHttpPort = 8080;
    public const int DefaultSocksPort = 1080;

    public const string IntroTextKey =
        "اگر سرور OpenVPN مستقیم در دسترس نیست، نوع پراکسی را HTTP یا SOCKS5 بگذارید. فقط کانفیگ TCP (proto tcp) از پراکسی عبور می‌کند. نام کاربری و رمز پراکسی اختیاری است.";

    public const string HostRequiredKey = "آدرس پراکسی بالادستی OpenVPN را وارد کنید";
    public const string HostInvalidKey = "آدرس پراکسی بالادستی OpenVPN نامعتبر است";
    public const string PortInvalidKey = "پورت پراکسی بالادستی باید بین 1 تا 65535 باشد";
    public const string PortNumberKey = "پورت پراکسی بالادستی باید عدد باشد";
    public const string UsernameRequiredKey = "نام کاربری پراکسی را وارد کنید";
    public const string CredentialNewlineKey = "نام کاربری یا رمز پراکسی نباید خط جدید داشته باشد";
    public const string TcpRequiredKey =
        "پراکسی بالادستی OpenVPN فقط با TCP کار می‌کند. در فایل .ovpn پروتکل را tcp یا tcp-client کنید.";
    public const string ExternalAuthFileKey =
        "این فایل .ovpn برای پراکسی فایل احراز هویت جدا دارد؛ نام کاربری و رمز پراکسی را در TunnelX وارد کنید.";

    public static bool TryGetConnectError(
        OpenVpnUpstreamProxySettings settings,
        string? openVpnConfig,
        out string messageKey)
    {
        if (TryGetSettingsError(settings, out messageKey))
            return true;

        if (settings.IsEnabled && !HasTcpTransport(openVpnConfig))
        {
            messageKey = TcpRequiredKey;
            return true;
        }

        messageKey = "";
        return false;
    }

    public static bool TryGetSettingsError(OpenVpnUpstreamProxySettings settings, out string messageKey)
    {
        messageKey = "";
        if (!settings.IsEnabled)
            return false;

        var host = (settings.Host ?? "").Trim();
        if (host.Length == 0)
        {
            messageKey = HostRequiredKey;
            return true;
        }

        if (!IsValidHost(host))
        {
            messageKey = HostInvalidKey;
            return true;
        }

        if (settings.Port is < 1 or > 65535)
        {
            messageKey = PortInvalidKey;
            return true;
        }

        var username = settings.Username ?? "";
        var password = settings.Password ?? "";
        if (username.Contains('\n') || username.Contains('\r') ||
            password.Contains('\n') || password.Contains('\r'))
        {
            messageKey = CredentialNewlineKey;
            return true;
        }

        if (string.IsNullOrWhiteSpace(username) && password.Length > 0)
        {
            messageKey = UsernameRequiredKey;
            return true;
        }

        return false;
    }

    /// <summary>
    /// OpenVPN config lines. <paramref name="quotedAuthFilePath"/> is the already-quoted auth file path
    /// used when <see cref="OpenVpnUpstreamProxySettings.HasCredentials"/> is true.
    /// </summary>
    public static IReadOnlyList<string> BuildConfigLines(
        OpenVpnUpstreamProxySettings settings,
        string? quotedAuthFilePath)
    {
        if (!settings.IsEnabled)
            return Array.Empty<string>();

        var host = settings.Host.Trim();
        var port = settings.Port.ToString(CultureInfo.InvariantCulture);
        var useAuth = settings.HasCredentials && !string.IsNullOrWhiteSpace(quotedAuthFilePath);
        var lines = new List<string>(2);

        if (settings.Kind == OpenVpnUpstreamProxyKind.Http)
        {
            lines.Add(useAuth
                ? $"http-proxy {host} {port} {quotedAuthFilePath} basic"
                : $"http-proxy {host} {port}");
            lines.Add("http-proxy-retry");
            return lines;
        }

        if (settings.Kind == OpenVpnUpstreamProxyKind.Socks)
        {
            lines.Add(useAuth
                ? $"socks-proxy {host} {port} {quotedAuthFilePath}"
                : $"socks-proxy {host} {port}");
            lines.Add("socks-proxy-retry");
        }

        return lines;
    }

    public static string BuildAuthFileBody(string? username, string? password) =>
        string.Concat((username ?? "").Trim(), "\n", password ?? "");

    public static bool TryParse(string? config, out OpenVpnParsedUpstreamProxy parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(config))
            return false;

        string? directive = null;
        string inlineUser = "";
        string inlinePass = "";
        var sawInline = false;
        var inlineDepth = 0;
        var inProxyUserPass = false;
        var proxyUserPassLines = new List<string>();

        foreach (var rawLine in config.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (inProxyUserPass)
            {
                if (trimmed.StartsWith("</http-proxy-user-pass>", StringComparison.OrdinalIgnoreCase))
                {
                    sawInline = true;
                    inlineUser = proxyUserPassLines.Count > 0 ? proxyUserPassLines[0] : "";
                    inlinePass = proxyUserPassLines.Count > 1 ? proxyUserPassLines[1] : "";
                    proxyUserPassLines.Clear();
                    inProxyUserPass = false;
                }
                else if (trimmed.Length > 0 && trimmed[0] != '#' && trimmed[0] != ';')
                {
                    proxyUserPassLines.Add(trimmed);
                }

                continue;
            }

            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                continue;

            if (TryReadTag(trimmed, out var tag, out var closing))
            {
                if (tag == "connection")
                    continue;

                if (tag == "http-proxy-user-pass" && !closing)
                {
                    inProxyUserPass = true;
                    proxyUserPassLines.Clear();
                    continue;
                }

                if (closing)
                {
                    if (inlineDepth > 0)
                        inlineDepth--;
                }
                else
                    inlineDepth++;

                continue;
            }

            if (inlineDepth > 0)
                continue;

            if (directive == null && IsProxyServerDirective(trimmed))
                directive = trimmed;
        }

        if (directive == null || !TryParseDirective(directive, out var kind, out var host, out var port, out var referencesAuthFile))
            return false;

        parsed = new OpenVpnParsedUpstreamProxy(
            kind,
            host,
            port,
            sawInline ? inlineUser : "",
            sawInline ? inlinePass : "",
            sawInline,
            referencesAuthFile);
        return true;
    }

    public static string DetectGlobalProto(string? config)
    {
        Scan(config, out var globalProto, out _);
        return globalProto;
    }

    public static bool HasTcpTransport(string? config)
    {
        Scan(config, out _, out var endpoints);
        return endpoints.Any(IsTcpProto);
    }

    public static bool RemoteLineUsesTcp(string line, string? inheritedProto)
    {
        if (!TryReadRemote(line, out _, out _, out var specified))
            return false;

        var effective = string.IsNullOrWhiteSpace(specified) ? inheritedProto : specified;
        return IsTcpProto(effective);
    }

    /// <summary>
    /// Drops non-TCP remotes and embedded proxy directives from one <c>&lt;connection&gt;</c> block.
    /// Returns false when the block has no TCP remote left.
    /// </summary>
    public static bool TryKeepTcpConnectionBlock(
        IReadOnlyList<string> block,
        string? globalProto,
        out List<string> filtered)
    {
        string? blockProto = null;
        var skippingUserPass = false;
        foreach (var line in block)
        {
            var trimmed = line.Trim();
            if (skippingUserPass)
            {
                if (trimmed.StartsWith("</http-proxy-user-pass>", StringComparison.OrdinalIgnoreCase))
                    skippingUserPass = false;
                continue;
            }

            if (trimmed.StartsWith("<http-proxy-user-pass>", StringComparison.OrdinalIgnoreCase))
            {
                skippingUserPass = true;
                continue;
            }

            if (TryReadProto(line, out var proto))
                blockProto = proto;
        }

        var inherited = string.IsNullOrWhiteSpace(blockProto) ? globalProto : blockProto;
        filtered = new List<string>(block.Count);
        skippingUserPass = false;
        var keptRemote = false;
        foreach (var line in block)
        {
            var trimmed = line.Trim();
            if (skippingUserPass)
            {
                if (trimmed.StartsWith("</http-proxy-user-pass>", StringComparison.OrdinalIgnoreCase))
                    skippingUserPass = false;
                continue;
            }

            if (trimmed.StartsWith("<http-proxy-user-pass>", StringComparison.OrdinalIgnoreCase))
            {
                skippingUserPass = true;
                continue;
            }

            if (IsUpstreamProxyDirectiveLine(trimmed))
                continue;

            if (trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
            {
                if (!RemoteLineUsesTcp(line, inherited))
                    continue;
                keptRemote = true;
            }

            filtered.Add(line);
        }

        return keptRemote;
    }

    public static bool IsTcpProto(string? proto) =>
        !string.IsNullOrWhiteSpace(proto) &&
        proto.Contains("tcp", StringComparison.OrdinalIgnoreCase);

    private static void Scan(string? config, out string globalProto, out List<string> endpointProtos)
    {
        globalProto = "";
        endpointProtos = new List<string>();
        if (string.IsNullOrWhiteSpace(config))
            return;

        var inConnection = false;
        var inlineDepth = 0;
        var blockLines = new List<string>();

        foreach (var rawLine in config.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                continue;

            if (TryReadTag(trimmed, out var tag, out var closing))
            {
                if (tag == "connection")
                {
                    if (closing && inConnection)
                    {
                        AddConnectionEndpoints(blockLines, globalProto, endpointProtos);
                        blockLines.Clear();
                        inConnection = false;
                    }
                    else if (!closing)
                    {
                        inConnection = true;
                        blockLines.Clear();
                    }

                    continue;
                }

                if (inConnection)
                {
                    blockLines.Add(rawLine);
                    continue;
                }

                if (closing)
                {
                    if (inlineDepth > 0)
                        inlineDepth--;
                }
                else
                    inlineDepth++;

                continue;
            }

            if (inConnection)
            {
                blockLines.Add(rawLine);
                continue;
            }

            if (inlineDepth > 0)
                continue;

            if (TryReadProto(trimmed, out var proto))
                globalProto = proto;
            else if (TryReadRemote(trimmed, out _, out _, out var remoteProto))
            {
                var effective = string.IsNullOrWhiteSpace(remoteProto) ? globalProto : remoteProto;
                endpointProtos.Add(effective);
            }
        }
    }

    private static void AddConnectionEndpoints(
        IReadOnlyList<string> block,
        string globalProto,
        List<string> endpointProtos)
    {
        string? blockProto = null;
        var inlineDepth = 0;
        var active = new List<string>();
        foreach (var line in block)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                continue;

            if (TryReadTag(trimmed, out _, out var closing))
            {
                if (closing)
                {
                    if (inlineDepth > 0)
                        inlineDepth--;
                }
                else
                    inlineDepth++;

                continue;
            }

            if (inlineDepth > 0)
                continue;

            active.Add(trimmed);
            if (TryReadProto(trimmed, out var proto))
                blockProto = proto;
        }

        var inherited = string.IsNullOrWhiteSpace(blockProto) ? globalProto : blockProto;
        foreach (var line in active)
        {
            if (!TryReadRemote(line, out _, out _, out var remoteProto))
                continue;

            var effective = string.IsNullOrWhiteSpace(remoteProto) ? inherited : remoteProto;
            endpointProtos.Add(effective ?? "");
        }
    }

    private static bool TryParseDirective(
        string directive,
        out OpenVpnUpstreamProxyKind kind,
        out string host,
        out int port,
        out bool referencesAuthFile)
    {
        kind = OpenVpnUpstreamProxyKind.None;
        host = "";
        port = 0;
        referencesAuthFile = false;

        var tokens = Tokenize(directive);
        if (tokens.Count < 2)
            return false;

        var socks = tokens[0].Equals("socks-proxy", StringComparison.OrdinalIgnoreCase);
        host = tokens[1].Trim();
        if (!IsValidHost(host))
            return false;

        if (socks)
        {
            kind = OpenVpnUpstreamProxyKind.Socks;
            if (tokens.Count >= 3 && int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var socksPort))
            {
                port = socksPort;
                referencesAuthFile = tokens.Count >= 4 && IsExternalAuthToken(tokens[3]);
            }
            else
            {
                port = DefaultSocksPort;
                referencesAuthFile = tokens.Count >= 3 && IsExternalAuthToken(tokens[2]);
            }
        }
        else
        {
            kind = OpenVpnUpstreamProxyKind.Http;
            if (tokens.Count < 3 || !int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                return false;

            referencesAuthFile = tokens.Count >= 4 && IsExternalAuthToken(tokens[3]);
        }

        return port is >= 1 and <= 65535;
    }

    private static bool IsProxyServerDirective(string trimmed) =>
        (StartsWithDirective(trimmed, "socks-proxy") && !StartsWithDirective(trimmed, "socks-proxy-retry")) ||
        (StartsWithDirective(trimmed, "http-proxy") &&
         !StartsWithDirective(trimmed, "http-proxy-option") &&
         !StartsWithDirective(trimmed, "http-proxy-retry") &&
         !StartsWithDirective(trimmed, "http-proxy-user-pass"));

    private static bool IsUpstreamProxyDirectiveLine(string trimmed) =>
        trimmed.StartsWith("socks-proxy", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("http-proxy", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternalAuthToken(string token)
    {
        var value = token.Trim().Trim('"');
        if (value.Length == 0)
            return false;

        return !value.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("auto-nct", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("stdin", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("basic", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("ntlm", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("ntlm2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253)
            return false;

        foreach (var c in host)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '#' or ';' or '<' or '>' or '\\')
                return false;
        }

        return true;
    }

    private static bool TryReadProto(string line, out string proto)
    {
        proto = "";
        var trimmed = line.Trim();
        if (!StartsWithDirective(trimmed, "proto"))
            return false;

        var tokens = Tokenize(trimmed);
        if (tokens.Count < 2)
            return false;

        proto = tokens[1];
        return true;
    }

    private static bool TryReadRemote(string line, out string host, out string port, out string proto)
    {
        host = "";
        port = "";
        proto = "";
        var trimmed = line.Trim();
        if (!StartsWithDirective(trimmed, "remote"))
            return false;

        var tokens = Tokenize(trimmed);
        if (tokens.Count < 2)
            return false;

        host = tokens[1];
        if (tokens.Count >= 3)
            port = tokens[2];
        if (tokens.Count >= 4)
            proto = tokens[3];
        return true;
    }

    private static bool TryReadTag(string trimmed, out string tag, out bool closing)
    {
        tag = "";
        closing = false;
        if (!trimmed.StartsWith("<", StringComparison.Ordinal))
            return false;

        closing = trimmed.StartsWith("</", StringComparison.Ordinal);
        var start = closing ? 2 : 1;
        var end = trimmed.IndexOf('>', start);
        if (end < 0)
            return false;

        tag = trimmed[start..end].Trim().ToLowerInvariant();
        return tag.Length > 0 && tag.IndexOf(' ') < 0;
    }

    private static bool StartsWithDirective(string trimmed, string name)
    {
        if (!trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmed.Length == name.Length || char.IsWhiteSpace(trimmed[name.Length]);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= line.Length || line[i] == '#')
                break;

            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    tokens.Add(line[(i + 1)..]);
                    break;
                }

                tokens.Add(line[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '#')
                i++;
            tokens.Add(line[start..i]);
        }

        return tokens;
    }
}
