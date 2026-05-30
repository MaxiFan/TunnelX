using System.Text;
using System.Text.Json.Nodes;
using AppTunnel.Models;

namespace AppTunnel.Services;

public sealed class ImportedConfigDraft
{
    public required TunnelType TunnelType { get; init; }
    public required string ConfigText { get; init; }
    public required string SuggestedName { get; init; }
    public string? SkipReason { get; init; }
}

/// <summary>
/// Parses clipboard/subscription text into connection profiles (share links, JSON, OpenVPN, WireGuard).
/// </summary>
public static class ConfigImportService
{
    private static readonly string[] V2RaySchemes =
    [
        "vmess://", "vless://", "trojan://", "ss://",
        "socks5://", "socks://", "http://"
    ];

    public static IReadOnlyList<ImportedConfigDraft> ParseClipboard(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return Array.Empty<ImportedConfigDraft>();

        var text = rawText.Trim();
        if (TryDecodeSubscriptionBlob(text, out var decoded))
            text = decoded;

        var segments = SplitConfigSegments(text);
        var results = new List<ImportedConfigDraft>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            var draft = TryParseSegment(segment.Trim());
            if (draft == null)
                continue;

            if (!string.IsNullOrWhiteSpace(draft.SkipReason))
            {
                results.Add(draft);
                continue;
            }

            var uniqueName = MakeUniqueName(draft.SuggestedName, usedNames);
            usedNames.Add(uniqueName);
            results.Add(new ImportedConfigDraft
            {
                TunnelType = draft.TunnelType,
                ConfigText = draft.ConfigText,
                SuggestedName = uniqueName
            });
        }

        return results;
    }

    public static ConnectionProfile CreateProfile(ImportedConfigDraft draft) => draft.TunnelType switch
    {
        TunnelType.V2Ray => new ConnectionProfile
        {
            Name = draft.SuggestedName,
            TunnelType = TunnelType.V2Ray,
            V2RayConfig = draft.ConfigText
        },
        TunnelType.OpenVpn => new ConnectionProfile
        {
            Name = draft.SuggestedName,
            TunnelType = TunnelType.OpenVpn,
            OpenVpnConfig = draft.ConfigText
        },
        TunnelType.WireGuard => new ConnectionProfile
        {
            Name = draft.SuggestedName,
            TunnelType = TunnelType.WireGuard,
            WireGuardConfig = draft.ConfigText
        },
        TunnelType.SocksProxy => CreateSocksProfile(draft),
        _ => new ConnectionProfile
        {
            Name = draft.SuggestedName,
            TunnelType = draft.TunnelType,
            V2RayConfig = draft.ConfigText
        }
    };

    public static bool IsDuplicateConfig(ConnectionProfile profile, IEnumerable<ConnectionProfile> existing)
    {
        var key = NormalizeConfigKey(profile);
        if (string.IsNullOrEmpty(key))
            return false;

        return existing.Any(p => string.Equals(NormalizeConfigKey(p), key, StringComparison.Ordinal));
    }

    private static string NormalizeConfigKey(ConnectionProfile profile) => profile.TunnelType switch
    {
        TunnelType.V2Ray => profile.V2RayConfig.Trim(),
        TunnelType.OpenVpn => profile.OpenVpnConfig.Trim(),
        TunnelType.WireGuard => profile.WireGuardConfig.Trim(),
        TunnelType.SocksProxy => $"{profile.ProxyProtocol}|{profile.ProxyServerAddress}|{profile.ProxyPort}|{profile.ProxyUsername}",
        TunnelType.L2tpIpsec => $"{profile.ServerAddress}|{profile.Username}",
        _ => profile.Name.Trim()
    };

    private static ConnectionProfile CreateSocksProfile(ImportedConfigDraft draft)
    {
        var uri = new Uri(draft.ConfigText.Split('#')[0]);
        var profile = new ConnectionProfile
        {
            Name = draft.SuggestedName,
            TunnelType = TunnelType.SocksProxy,
            ProxyServerAddress = uri.Host,
            ProxyPort = uri.Port > 0 ? uri.Port : 1080,
            ProxyProtocol = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? ProxyProtocol.Http
                : ProxyProtocol.Socks5
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            profile.ProxyUsername = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
                profile.ProxyPassword = Uri.UnescapeDataString(parts[1]);
        }

        return profile;
    }

    private static ImportedConfigDraft? TryParseSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return null;

        if (segment.StartsWith('{'))
        {
            if (!IsV2RayJson(segment))
            {
                return new ImportedConfigDraft
                {
                    TunnelType = TunnelType.V2Ray,
                    ConfigText = segment,
                    SuggestedName = "",
                    SkipReason = LocalizationService.Instance.T("فرمت JSON شناخته نشد")
                };
            }

            return new ImportedConfigDraft
            {
                TunnelType = TunnelType.V2Ray,
                ConfigText = segment,
                SuggestedName = SuggestJsonProfileName(segment)
            };
        }

        foreach (var scheme in V2RaySchemes)
        {
            if (!segment.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                continue;

            if (scheme is "socks5://" or "socks://" or "http://")
            {
                return new ImportedConfigDraft
                {
                    TunnelType = TunnelType.SocksProxy,
                    ConfigText = segment,
                    SuggestedName = SuggestUriProfileName(segment, "Proxy")
                };
            }

            return new ImportedConfigDraft
            {
                TunnelType = TunnelType.V2Ray,
                ConfigText = segment,
                SuggestedName = SuggestUriProfileName(segment, scheme.TrimEnd('/').Split("://")[0].ToUpperInvariant())
            };
        }

        if (segment.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportedConfigDraft
            {
                TunnelType = TunnelType.WireGuard,
                ConfigText = segment,
                SuggestedName = SuggestWireGuardProfileName(segment)
            };
        }

        if (LooksLikeOpenVpnConfig(segment))
        {
            return new ImportedConfigDraft
            {
                TunnelType = TunnelType.OpenVpn,
                ConfigText = segment,
                SuggestedName = SuggestOpenVpnProfileName(segment)
            };
        }

        return new ImportedConfigDraft
        {
            TunnelType = TunnelType.V2Ray,
            ConfigText = segment,
            SuggestedName = "",
            SkipReason = LocalizationService.Instance.T("فرمت کانفیگ شناخته نشد")
        };
    }

    private static bool LooksLikeOpenVpnConfig(string text) =>
        text.Contains("remote ", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("-----BEGIN OpenVPN Static key V1-----", StringComparison.Ordinal) ||
        text.Contains("client\n", StringComparison.OrdinalIgnoreCase);

    private static bool IsV2RayJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            return root?["outbounds"] is JsonArray;
        }
        catch
        {
            return false;
        }
    }

    private static string SuggestJsonProfileName(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["outbounds"] is JsonArray outbounds)
            {
                foreach (var item in outbounds.OfType<JsonObject>())
                {
                    var tag = item["tag"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(tag))
                        return SanitizeName(tag);

                    var server = item["server"]?.GetValue<string>() ??
                                 item["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>();
                    var port = item["server_port"]?.GetValue<int>() ??
                               item["settings"]?["vnext"]?[0]?["port"]?.GetValue<int>();
                    if (!string.IsNullOrWhiteSpace(server))
                        return SanitizeName(port is > 0 ? $"{server}:{port}" : server);
                }
            }
        }
        catch
        {
            // ignored
        }

        return LocalizationService.Instance.T("کانفیگ JSON");
    }

    private static string SuggestUriProfileName(string uriText, string fallbackPrefix)
    {
        var hashIdx = uriText.IndexOf('#');
        if (hashIdx >= 0 && hashIdx < uriText.Length - 1)
        {
            var remark = Uri.UnescapeDataString(uriText[(hashIdx + 1)..]).Trim();
            if (!string.IsNullOrWhiteSpace(remark))
                return SanitizeName(remark);
        }

        if (uriText.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b64 = uriText["vmess://".Length..].Split('#')[0];
                b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                var node = JsonNode.Parse(json)?.AsObject();
                var ps = node?["ps"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(ps))
                    return SanitizeName(ps);

                var add = node?["add"]?.GetValue<string>();
                var port = node?["port"]?.ToString();
                if (!string.IsNullOrWhiteSpace(add))
                    return SanitizeName(string.IsNullOrWhiteSpace(port) ? add : $"{add}:{port}");
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            var uri = new Uri(uriText.Split('#')[0]);
            if (!string.IsNullOrWhiteSpace(uri.Host))
                return SanitizeName(uri.Port > 0 ? $"{uri.Host}:{uri.Port}" : uri.Host);
        }
        catch
        {
            // ignored
        }

        return $"{fallbackPrefix}-{DateTime.Now:HHmmss}";
    }

    private static string SuggestWireGuardProfileName(string config)
    {
        if (WireGuardConfigParser.TryParse(config, out var parsed, out _))
        {
            if (!string.IsNullOrWhiteSpace(parsed.EndpointHost))
                return SanitizeName($"{parsed.EndpointHost}:{parsed.EndpointPort}");
        }

        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                return SanitizeName(trimmed.TrimStart('#').Trim());
        }

        return LocalizationService.Instance.T("WireGuard");
    }

    private static string SuggestOpenVpnProfileName(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return SanitizeName(parts.Length >= 3 ? $"{parts[1]}:{parts[2]}" : parts[1]);
        }

        return LocalizationService.Instance.T("OpenVPN");
    }

    private static string SanitizeName(string name)
    {
        name = name.Trim();
        if (name.Length > 48)
            name = name[..48];
        return string.IsNullOrWhiteSpace(name)
            ? LocalizationService.Instance.T("پروفایل جدید")
            : name;
    }

    private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
    {
        baseName = string.IsNullOrWhiteSpace(baseName)
            ? LocalizationService.Instance.T("پروفایل جدید")
            : SanitizeName(baseName);

        if (!usedNames.Contains(baseName))
            return baseName;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!usedNames.Contains(candidate))
                return candidate;
        }

        return $"{baseName}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private static bool TryDecodeSubscriptionBlob(string text, out string decoded)
    {
        decoded = "";
        if (text.Contains("://", StringComparison.Ordinal))
            return false;

        try
        {
            var normalized = text.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return decoded.Contains("://", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> SplitConfigSegments(string text)
    {
        var results = new List<string>();
        var lines = text.Split('\n');
        var buffer = new StringBuilder();
        var inJson = false;
        var braceDepth = 0;
        var inWireGuard = false;

        void Flush()
        {
            var chunk = buffer.ToString().Trim();
            buffer.Clear();
            if (!string.IsNullOrWhiteSpace(chunk))
                results.Add(chunk);
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (inJson || inWireGuard)
                    buffer.AppendLine(line);
                else
                    Flush();
                continue;
            }

            if (!inJson && !inWireGuard && StartsWithKnownScheme(trimmed))
            {
                Flush();
                buffer.AppendLine(trimmed);
                continue;
            }

            if (!inJson && trimmed.StartsWith('{'))
            {
                Flush();
                inJson = true;
                braceDepth = 0;
            }

            if (inJson)
            {
                buffer.AppendLine(line);
                braceDepth += trimmed.Count(c => c == '{');
                braceDepth -= trimmed.Count(c => c == '}');
                if (braceDepth <= 0)
                {
                    inJson = false;
                    Flush();
                }
                continue;
            }

            if (!inWireGuard && trimmed.StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                inWireGuard = true;
            }

            if (inWireGuard && trimmed.StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase) && buffer.Length > 0)
            {
                Flush();
            }

            if (inWireGuard)
            {
                buffer.AppendLine(line);
                continue;
            }

            if (LooksLikeOpenVpnConfig(trimmed) && buffer.Length == 0)
            {
                buffer.AppendLine(line);
                continue;
            }

            if (buffer.Length > 0 && LooksLikeOpenVpnConfig(buffer.ToString()) && trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
            {
                buffer.AppendLine(line);
                continue;
            }

            if (buffer.Length > 0 && LooksLikeOpenVpnConfig(buffer.ToString()) &&
                !trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith('#') &&
                !trimmed.StartsWith("setenv", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("auth-", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("cipher", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("verb", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                Flush();
            }

            buffer.AppendLine(line);
        }

        Flush();
        return results;
    }

    private static bool StartsWithKnownScheme(string line)
    {
        if (line.StartsWith('{'))
            return true;

        foreach (var scheme in V2RaySchemes)
        {
            if (line.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return line.StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase);
    }
}
