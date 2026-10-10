using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AppTunnel.Services;

/// <summary>How INCLUDE/EXCLUDE lists resolve domain names into route IPs.</summary>
internal enum DestinationDnsPolicy
{
    /// <summary>INCLUDE: only via tunnel-bound DoH (never unbound system DNS).</summary>
    TunnelRequired,
    /// <summary>EXCLUDE: system DNS first; tunnel-bound DoH only if direct fails.</summary>
    DirectThenTunnel
}

/// <summary>
/// Options for resolving hostnames while a VPN/split tunnel is active.
/// Binds DoH TCP to the tunnel local IP so lookups egress via the tunnel.
/// </summary>
internal sealed class TunnelDnsResolveOptions
{
    public IPAddress? BindLocalAddress { get; init; }
    /// <summary>VPN interface index for IP_UNICAST_IF. Avoids a capturing /32.</summary>
    public int VpnInterfaceIndex { get; init; }
    /// <summary>Skip filtered system DNS; use DoH through the tunnel first.</summary>
    public bool PreferDohFirst { get; init; }
    /// <summary>Do not fall back to system DNS after tunnel DoH (INCLUDE lists).</summary>
    public bool TunnelOnly { get; init; }
    public TimeSpan? Timeout { get; init; }
}

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
    private static readonly HttpClient DohHttp = CreateDohHttpClient(bindLocal: null);

    private static HttpClient CreateDohHttpClient(IPAddress? bindLocal, TimeSpan? timeout = null, int vpnInterfaceIndex = 0)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = bindLocal == null
                ? null
                : async (context, ct) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        Ipv4UnicastInterface.Bind(socket, bindLocal, vpnInterfaceIndex);
                        await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(8)
        };
    }
    internal static readonly IPAddress[] DohBootstrapResolvers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("1.0.0.1")
    ];

    public static Task<IPAddress[]> ResolveIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null,
        TunnelDnsResolveOptions? tunnel = null)
        => ResolveIpv4CoreAsync(hostOrIp, ct, ttl, tunnel);

    public static IPAddress[] ResolveIpv4(
        string hostOrIp,
        TimeSpan? ttl = null,
        TunnelDnsResolveOptions? tunnel = null)
    {
        if (tunnel == null)
            return ResolveIpv4CoreBlocking(hostOrIp, ttl, null);

        try
        {
            using var cts = new CancellationTokenSource(tunnel.Timeout ?? TimeSpan.FromSeconds(12));
            return ResolveIpv4CoreAsync(hostOrIp, cts.Token, ttl, tunnel)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// Resolve for INCLUDE/EXCLUDE routing lists. Writes cache once so EXCLUDE
    /// direct failure does not poison tunnel fallback with a negative entry.
    /// </summary>
    public static IPAddress[] ResolveIpv4ForRoutingList(
        string hostOrIp,
        DestinationDnsPolicy policy,
        TunnelDnsResolveOptions? tunnelOptions,
        TimeSpan? ttl = null)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = RoutingCacheKey(NormalizeKey(hostOrIp), policy);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;

        var stale = TryGetStaleEntry(key, out var staleCached) ? staleCached : Array.Empty<IPAddress>();

        try
        {
            var totalTimeout = policy == DestinationDnsPolicy.TunnelRequired
                ? tunnelOptions?.Timeout ?? TimeSpan.FromSeconds(12)
                : TimeSpan.FromSeconds(8) + (tunnelOptions?.Timeout ?? TimeSpan.FromSeconds(12));
            using var cts = new CancellationTokenSource(totalTimeout);
            var v4 = ResolveIpv4ForRoutingListCoreAsync(hostOrIp, policy, tunnelOptions, cts.Token)
                .GetAwaiter()
                .GetResult();

            if (v4.Length == 0 && stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
                return stale;
            }

            PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return v4;
        }
        catch (OperationCanceledException)
        {
            if (stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}' after timeout");
                return stale;
            }
            return Array.Empty<IPAddress>();
        }
    }

    private static async Task<IPAddress[]> ResolveIpv4CoreAsync(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl,
        TunnelDnsResolveOptions? tunnel)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = NormalizeKey(hostOrIp);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;
        var stale = TryGetStaleEntry(key, out var staleCached) ? staleCached : Array.Empty<IPAddress>();

        IPAddress[] v4;
        if (tunnel?.PreferDohFirst == true)
            v4 = await ResolveTunnelPathAsync(hostOrIp, ct, tunnel).ConfigureAwait(false);
        else
            v4 = await ResolveDefaultPathAsync(hostOrIp, ct, tunnel).ConfigureAwait(false);

        if (v4.Length == 0 && stale.Length > 0)
        {
            PutEntry(key, stale, TimeSpan.FromSeconds(45));
            Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
            return stale;
        }

        PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
        return v4;
    }

    private static IPAddress[] ResolveIpv4CoreBlocking(
        string hostOrIp,
        TimeSpan? ttl,
        TunnelDnsResolveOptions? tunnel)
        => ResolveIpv4CoreAsync(hostOrIp, CancellationToken.None, ttl, tunnel).GetAwaiter().GetResult();

    private static async Task<IPAddress[]> ResolveDefaultPathAsync(
        string hostOrIp,
        CancellationToken ct,
        TunnelDnsResolveOptions? tunnel)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(hostOrIp, ct).ConfigureAwait(false);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (v4.Length == 0)
                v4 = await ResolveIpv4ViaDohAsync(hostOrIp, ct, tunnel).ConfigureAwait(false);
            return v4;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await ResolveIpv4ViaDohAsync(hostOrIp, ct, tunnel).ConfigureAwait(false);
        }
    }

    private static async Task<IPAddress[]> ResolveIpv4ForRoutingListCoreAsync(
        string hostOrIp,
        DestinationDnsPolicy policy,
        TunnelDnsResolveOptions? tunnelOptions,
        CancellationToken ct)
    {
        return policy switch
        {
            DestinationDnsPolicy.TunnelRequired when tunnelOptions != null =>
                await ResolveTunnelPathAsync(hostOrIp, ct, tunnelOptions).ConfigureAwait(false),
            DestinationDnsPolicy.TunnelRequired =>
                Array.Empty<IPAddress>(),
            DestinationDnsPolicy.DirectThenTunnel =>
                await ResolveDirectThenTunnelAsync(hostOrIp, ct, tunnelOptions).ConfigureAwait(false),
            _ => Array.Empty<IPAddress>()
        };
    }

    private static async Task<IPAddress[]> ResolveDirectThenTunnelAsync(
        string hostOrIp,
        CancellationToken ct,
        TunnelDnsResolveOptions? tunnelOptions)
    {
        using (var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            directCts.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var direct = await ResolveDefaultPathAsync(hostOrIp, directCts.Token, tunnel: null)
                    .ConfigureAwait(false);
                if (direct.Length > 0)
                    return direct;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Direct path timed out — try tunnel fallback below.
            }
        }

        if (tunnelOptions == null)
            return Array.Empty<IPAddress>();

        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tunnelCts.CancelAfter(tunnelOptions.Timeout ?? TimeSpan.FromSeconds(12));
        var viaTunnel = await ResolveTunnelPathAsync(hostOrIp, tunnelCts.Token, tunnelOptions)
            .ConfigureAwait(false);
        if (viaTunnel.Length > 0)
            Logger.Info($"[DNS] '{hostOrIp}' resolved via tunnel fallback (direct path failed)");
        return viaTunnel;
    }

    private static async Task<IPAddress[]> ResolveTunnelPathAsync(
        string hostOrIp,
        CancellationToken ct,
        TunnelDnsResolveOptions tunnel)
    {
        var viaDoh = await ResolveIpv4ViaDohAsync(hostOrIp, ct, tunnel).ConfigureAwait(false);
        if (viaDoh.Length > 0)
            return viaDoh;

        if (tunnel.TunnelOnly)
            return Array.Empty<IPAddress>();

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(hostOrIp, ct).ConfigureAwait(false);
            return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static async Task<IPAddress[]> ResolveIpv4ViaDohAsync(
        string host,
        CancellationToken ct,
        TunnelDnsResolveOptions? tunnel = null)
    {
        var queryHost = NormalizeKey(host);
        if (string.IsNullOrWhiteSpace(queryHost))
            return Array.Empty<IPAddress>();

        using var boundClient = tunnel?.BindLocalAddress != null
            ? CreateDohHttpClient(tunnel.BindLocalAddress, tunnel.Timeout, tunnel.VpnInterfaceIndex)
            : null;

        var client = boundClient ?? DohHttp;
        var viaTunnel = boundClient != null;

        foreach (var resolver in DohBootstrapResolvers)
        {
            try
            {
                var url = $"https://{resolver}/dns-query?name={Uri.EscapeDataString(queryHost)}&type=A";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                    continue;

                var result = new List<IPAddress>();
                foreach (var answer in answers.EnumerateArray())
                {
                    if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                        continue;
                    if (!answer.TryGetProperty("data", out var data))
                        continue;
                    if (IPAddress.TryParse(data.GetString(), out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(ip);
                }

                if (result.Count > 0)
                {
                    var path = viaTunnel ? "tunnel-DoH" : "DoH";
                    Logger.Info($"[DNS-DOH] {queryHost} -> {string.Join(", ", result.Select(x => x.ToString()))} via {resolver} ({path})");
                    return result.ToArray();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning($"[DNS-DOH] {queryHost} via {resolver} failed: {ex.Message}");
            }
        }

        return Array.Empty<IPAddress>();
    }

    public static async Task<IPAddress?> ResolveFirstIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null,
        TunnelDnsResolveOptions? tunnel = null)
    {
        var addrs = await ResolveIpv4Async(hostOrIp, ct, ttl, tunnel);
        return addrs.Length > 0 ? addrs[0] : null;
    }

    public static IPAddress? ResolveFirstIpv4(
        string hostOrIp,
        TimeSpan? ttl = null,
        TunnelDnsResolveOptions? tunnel = null)
    {
        var addrs = ResolveIpv4(hostOrIp, ttl, tunnel);
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

    /// <summary>
    /// INCLUDE (tunnel-only) and EXCLUDE (direct-first) must not share cache entries —
    /// the same hostname can resolve to different IPs on each path.
    /// </summary>
    private static string RoutingCacheKey(string normalizedHost, DestinationDnsPolicy policy)
        => string.IsNullOrEmpty(normalizedHost)
            ? normalizedHost
            : policy == DestinationDnsPolicy.TunnelRequired
                ? normalizedHost + "\0tunnel"
                : normalizedHost;

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

    private static bool TryGetStaleEntry(string key, out IPAddress[] addresses)
    {
        if (Cache.TryGetValue(key, out var entry) &&
            entry.ExpiresAtUtc <= DateTime.UtcNow &&
            entry.Addresses.Length > 0)
        {
            addresses = entry.Addresses;
            return true;
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

