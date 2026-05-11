using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Lightweight in-memory DNS cache for IPv4 lookups used by routing and SOCKS.
/// Keeps short TTLs to avoid stale CDN mappings while preventing repeated
/// resolver calls during bursty traffic.
/// </summary>
internal static class DnsResolverCache
{
    private sealed class CacheEntry
    {
        public required IPAddress[] Addresses { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(20);

    public static async Task<IPAddress[]> ResolveIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = NormalizeKey(hostOrIp);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(hostOrIp, ct);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return v4;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            PutEntry(key, Array.Empty<IPAddress>(), NegativeTtl);
            return Array.Empty<IPAddress>();
        }
    }

    public static IPAddress[] ResolveIpv4(string hostOrIp, TimeSpan? ttl = null)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = NormalizeKey(hostOrIp);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;

        try
        {
            var addrs = Dns.GetHostAddresses(hostOrIp);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return v4;
        }
        catch
        {
            PutEntry(key, Array.Empty<IPAddress>(), NegativeTtl);
            return Array.Empty<IPAddress>();
        }
    }

    public static async Task<IPAddress?> ResolveFirstIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null)
    {
        var addrs = await ResolveIpv4Async(hostOrIp, ct, ttl);
        return addrs.Length > 0 ? addrs[0] : null;
    }

    public static IPAddress? ResolveFirstIpv4(string hostOrIp, TimeSpan? ttl = null)
    {
        var addrs = ResolveIpv4(hostOrIp, ttl);
        return addrs.Length > 0 ? addrs[0] : null;
    }

    private static bool TryParseIpv4(string value, out IPAddress ip)
    {
        if (IPAddress.TryParse(value?.Trim(), out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ip = parsed;
            return true;
        }

        ip = IPAddress.None;
        return false;
    }

    private static string NormalizeKey(string? hostOrIp)
        => (hostOrIp ?? string.Empty).Trim().ToLowerInvariant();

    private static bool TryGetValidEntry(string key, out IPAddress[] addresses)
    {
        if (Cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > DateTime.UtcNow)
            {
                addresses = entry.Addresses;
                return true;
            }

            Cache.TryRemove(key, out _);
        }

        addresses = Array.Empty<IPAddress>();
        return false;
    }

    private static void PutEntry(string key, IPAddress[] addresses, TimeSpan ttl)
    {
        Cache[key] = new CacheEntry
        {
            Addresses = addresses,
            ExpiresAtUtc = DateTime.UtcNow.Add(ttl)
        };
    }
}

