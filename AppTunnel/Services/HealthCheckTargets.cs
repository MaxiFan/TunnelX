using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// One TCP health-check or ping target. <see cref="Display"/> round-trips through <see cref="HealthCheckTargets.TryParse"/>.
/// </summary>
public readonly record struct HealthCheckEndpoint(string Host, int Port)
{
    public string Display
    {
        get
        {
            var host = Host.Contains(':') ? $"[{Host}]" : Host;
            return Port == 443 ? host : $"{host}:{Port}";
        }
    }

    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// Effective connection health-check targets after applying custom entries and the public defaults.
/// </summary>
public sealed class HealthCheckPlan
{
    public required IReadOnlyList<HealthCheckEndpoint> Targets { get; init; }
    public required IReadOnlyList<HealthCheckEndpoint> CustomEndpoints { get; init; }
    public required bool IncludesDefaultPublicEndpoints { get; init; }
    public required bool FellBackToDefaults { get; init; }
    public required IReadOnlyList<string> InvalidEntries { get; init; }
    public required IReadOnlyList<string> IgnoredExtraEntries { get; init; }

    public bool HasCustomEndpoints => CustomEndpoints.Count > 0;

    /// <summary>
    /// Ping box value. Custom targets win when present; otherwise the historical www.google.com default.
    /// </summary>
    public string SuggestedPingTarget =>
        HasCustomEndpoints ? CustomEndpoints[0].Display : HealthCheckTargets.DefaultPingTarget;
}

/// <summary>
/// Parses user-configured health-check targets and decides which hosts the connection verify step probes.
/// </summary>
public static class HealthCheckTargets
{
    public const string DefaultPingTarget = "www.google.com";
    public const int MaxCustomEndpoints = 4;

    public static readonly HealthCheckEndpoint[] DefaultPublicEndpoints =
    [
        new("google.com", 443),
        new("cloudflare.com", 443)
    ];

    private static readonly char[] Separators = ['\r', '\n', ',', ';', '،'];

    /// <summary>
    /// Empty input keeps google.com and cloudflare.com. Custom entries are probed first.
    /// Turning defaults off with no valid custom entry falls back to the public endpoints.
    /// </summary>
    public static HealthCheckPlan Resolve(string? raw, bool includeDefaultPublicEndpoints)
    {
        var custom = new List<HealthCheckEndpoint>();
        var invalid = new List<string>();
        var ignored = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in Split(raw))
        {
            if (!TryParse(token, out var endpoint))
            {
                invalid.Add(token);
                continue;
            }

            var key = EndpointKey(endpoint);
            if (!seen.Add(key))
                continue;

            if (custom.Count >= MaxCustomEndpoints)
            {
                ignored.Add(token);
                continue;
            }

            custom.Add(endpoint);
        }

        var fellBack = !includeDefaultPublicEndpoints && custom.Count == 0;
        var includeDefaults = includeDefaultPublicEndpoints || fellBack;
        var targets = new List<HealthCheckEndpoint>(custom);
        if (includeDefaults)
        {
            foreach (var endpoint in DefaultPublicEndpoints)
            {
                if (seen.Add(EndpointKey(endpoint)))
                    targets.Add(endpoint);
            }
        }

        return new HealthCheckPlan
        {
            Targets = targets,
            CustomEndpoints = custom,
            IncludesDefaultPublicEndpoints = includeDefaults,
            FellBackToDefaults = fellBack,
            InvalidEntries = invalid,
            IgnoredExtraEntries = ignored
        };
    }

    /// <summary>
    /// Accepts a URL, hostname, IPv4/IPv6, or host:port. Bare names use port 443; http uses 80 unless a port is set.
    /// </summary>
    public static bool TryParse(string? raw, out HealthCheckEndpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim().Trim('"');
        if (raw.Length == 0)
            return false;

        if (raw.Contains("://", StringComparison.Ordinal))
            return TryParseUri(raw, out endpoint);

        if (raw.StartsWith('['))
            return TryParseBracketed(raw, out endpoint);

        if (IPAddress.TryParse(raw, out var ip) && IsSupportedIp(ip))
        {
            endpoint = new HealthCheckEndpoint(NormalizeIp(ip), 443);
            return true;
        }

        var colon = raw.LastIndexOf(':');
        if (colon > 0 && raw.IndexOf(':') == colon)
        {
            var hostPart = raw[..colon];
            var portPart = raw[(colon + 1)..];
            if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !IsPort(port))
                return false;
            if (!TryNormalizeHost(hostPart, out var host))
                return false;
            endpoint = new HealthCheckEndpoint(host, port);
            return true;
        }

        if (!TryNormalizeHost(raw, out var bareHost))
            return false;

        endpoint = new HealthCheckEndpoint(bareHost, 443);
        return true;
    }

    /// <summary>
    /// RFC1918, loopback, and link-local addresses. These must not get a temporary VPN host route.
    /// </summary>
    public static bool IsPrivateOrLinkLocal(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            169 => b[1] == 254,
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168,
            _ => false
        };
    }

    private static IEnumerable<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
                yield return part.Trim();
        }
    }

    private static bool TryParseUri(string raw, out HealthCheckEndpoint endpoint)
    {
        endpoint = default;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        if (!TryNormalizeHost(uri.Host, out var host))
            return false;

        int port;
        if (!uri.IsDefaultPort && uri.Port > 0)
            port = uri.Port;
        else if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            port = 80;
        else
            port = 443;

        if (!IsPort(port))
            return false;

        endpoint = new HealthCheckEndpoint(host, port);
        return true;
    }

    private static bool TryParseBracketed(string raw, out HealthCheckEndpoint endpoint)
    {
        endpoint = default;
        var end = raw.IndexOf(']');
        if (end <= 1)
            return false;

        var hostPart = raw[1..end];
        var rest = raw[(end + 1)..];
        var port = 443;
        if (rest.Length > 0)
        {
            if (!rest.StartsWith(':') ||
                !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                !IsPort(port))
                return false;
        }

        if (!IPAddress.TryParse(hostPart, out var ip) || !IsSupportedIp(ip))
            return false;

        endpoint = new HealthCheckEndpoint(NormalizeIp(ip), port);
        return true;
    }

    private static bool TryNormalizeHost(string host, out string normalized)
    {
        normalized = host.Trim().Trim('[', ']').Trim().TrimEnd('.');
        if (normalized.Length == 0 || normalized.Length > 253)
            return false;

        if (IPAddress.TryParse(normalized, out var ip) && IsSupportedIp(ip))
        {
            normalized = NormalizeIp(ip);
            return true;
        }

        if (!IsHostname(normalized))
            return false;

        try
        {
            var ascii = new IdnMapping().GetAscii(normalized);
            if (IsHostname(ascii))
            {
                normalized = ascii;
                return true;
            }
        }
        catch (ArgumentException)
        {
            // Internal names may contain underscores, which IDNA rejects.
        }

        return normalized.All(c => c <= 127);
    }

    private static bool IsHostname(string host)
    {
        if (host.Length == 0 || host.Length > 253)
            return false;

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                return false;
            if (label[0] == '-' || label[^1] == '-')
                return false;
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    return false;
            }
        }

        return true;
    }

    private static bool IsSupportedIp(IPAddress ip)
        => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;

    private static string NormalizeIp(IPAddress ip) => ip.ToString();

    private static bool IsPort(int port) => port is >= 1 and <= 65535;

    private static string EndpointKey(HealthCheckEndpoint endpoint)
        => $"{endpoint.Host}:{endpoint.Port}";
}
