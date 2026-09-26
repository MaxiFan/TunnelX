using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AppTunnel.Services;

public sealed record IpGeoInfo(string CountryCode, string CountryName);

public static class IpGeoLookupService
{
    public static string CountryCodeToFlagImageUrl(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
            return "";

        countryCode = countryCode.ToLowerInvariant();
        if (!char.IsAsciiLetter(countryCode[0]) || !char.IsAsciiLetter(countryCode[1]))
            return "";

        // 20px-high PNGs are light and still sharp enough for the small dashboard card.
        return $"https://flagcdn.com/h20/{countryCode}.png";
    }

    public static async Task<byte[]?> DownloadFlagPngViaTunnelAsync(
        int proxyPort,
        string? countryCode,
        CancellationToken ct = default)
    {
        if (proxyPort <= 0 || string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
            return null;

        var codeLower = countryCode.Trim().ToLowerInvariant();
        var codeUpper = codeLower.ToUpperInvariant();
        if (!char.IsAsciiLetter(codeLower[0]) || !char.IsAsciiLetter(codeLower[1]))
            return null;

        // Primary + fallbacks — all fetched through the local tunnel proxy.
        var sources = new (string host, int port, string path, bool tls)[]
        {
            ("flagcdn.com", 443, $"/h20/{codeLower}.png", true),
            ("flagcdn.com", 443, $"/w20/{codeLower}.png", true),
            ("flagsapi.com", 443, $"/{codeUpper}/flat/64.png", true),
        };

        foreach (var (host, port, path, tls) in sources)
        {
            try
            {
                var bytes = await TunnelProxyHttpService.GetBytesAsync(proxyPort, host, port, path, tls, ct);
                if (LooksLikePng(bytes))
                    return bytes;
            }
            catch
            {
                // try next CDN / size variant
            }
        }

        return null;
    }

    public static string CountryCodeToFlagEmoji(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
            return "";

        countryCode = countryCode.ToUpperInvariant();
        if (!char.IsAsciiLetter(countryCode[0]) || !char.IsAsciiLetter(countryCode[1]))
            return "";

        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + (countryCode[0] - 'A')),
            char.ConvertFromUtf32(0x1F1E6 + (countryCode[1] - 'A')));
    }

    /// <summary>
    /// True when <paramref name="flag"/> is a regional-indicator pair, not plain ASCII like "DE".
    /// </summary>
    public static bool IsRegionalIndicatorFlag(string? flag, string? countryCode)
    {
        if (string.IsNullOrEmpty(flag) || string.IsNullOrEmpty(countryCode) || countryCode.Length != 2)
            return false;

        if (string.Equals(flag, countryCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var elements = new System.Globalization.StringInfo(flag).LengthInTextElements;
        return elements == 1 && flag.Length >= 3;
    }

    /// <summary>
    /// Resolves country for an IP through the local tunnel proxy (mixed/SOCKS port).
    /// </summary>
    public static async Task<IpGeoInfo?> LookupViaTunnelAsync(
        int proxyPort,
        string ip,
        CancellationToken ct = default)
    {
        if (proxyPort <= 0 || !IPAddress.TryParse(ip.Trim(), out var address))
            return null;

        if (IPAddress.IsLoopback(address) || IsPrivateOrLinkLocal(address))
            return null;

        var effectiveLanguage = LocalizationService.Instance.EffectiveLanguage;
        var lang = effectiveLanguage.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ? "fa"
            : effectiveLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru"
            : "en";
        var encodedIp = Uri.EscapeDataString(ip.Trim());

        var endpoints = new (string host, int port, string path, bool tls)[]
        {
            ("ip-api.com", 80,
                $"/json/{encodedIp}?fields=status,country,countryCode,message&lang={lang}", false),
            ("ipwho.is", 443, $"/{encodedIp}", true),
            ("ipapi.co", 443, $"/{encodedIp}/json/", true),
        };

        foreach (var (host, port, path, tls) in endpoints)
        {
            try
            {
                var body = await TunnelProxyHttpService.GetAsync(proxyPort, host, port, path, tls, ct);
                if (!string.IsNullOrWhiteSpace(body) && TryParseGeo(body, out var info))
                    return info;
            }
            catch
            {
                // try next endpoint
            }
        }

        return null;
    }

    public static bool TryParseGeo(string body, out IpGeoInfo info)
    {
        info = new IpGeoInfo("", "");
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                return false;

            if (root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
                return false;

            var country = GetString(root, "country", "country_name");
            var code = GetString(root, "countryCode", "country_code");

            if (string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(code))
                return false;

            info = new IpGeoInfo(code.ToUpperInvariant(), country.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                var value = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    private static bool LooksLikePng(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 8)
            return false;

        return bytes[0] == 0x89 &&
               bytes[1] == 0x50 &&
               bytes[2] == 0x4E &&
               bytes[3] == 0x47 &&
               bytes[4] == 0x0D &&
               bytes[5] == 0x0A &&
               bytes[6] == 0x1A &&
               bytes[7] == 0x0A;
    }
}
