using System.Net;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    #region Included Destinations

    /// <summary>
    /// Replace the entire include list. Resolves domains to IPs.
    /// </summary>
    public void SetIncludedDestinations(IEnumerable<string> entries)
    {
        _includedIps.Clear();
        _includedEntries.Clear();
        foreach (var entry in entries)
            AddIncludedDestination(entry);
    }

    /// <summary>
    /// Add a single domain or IP to the include list.
    /// Included destinations will be forced through the VPN tunnel regardless of
    /// whether the source application is in the target tunnel apps list.
    /// </summary>
    public void AddIncludedDestination(string entry)
    {
        var originalEntry = entry.Trim();
        entry = NormalizeDestinationEntry(originalEntry);
        if (string.IsNullOrEmpty(entry)) return;
        if (_includedEntries.ContainsKey(entry)) return;

        var ips = new HashSet<uint>();
        if (IPAddress.TryParse(entry, out var ip))
        {
            var nbo = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            ips.Add(nbo);
            _includedIps[nbo] = true;
            Logger.Info($"[INCLUDE] Added IP {entry}");
        }
        else
        {
            // Domain → resolve
            try
            {
                var addresses = DnsResolverCache.ResolveIpv4(entry);
                foreach (var addr in addresses)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var nbo = BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
                        ips.Add(nbo);
                        _includedIps[nbo] = true;
                    }
                }
                var normalizedSuffix = originalEntry.Equals(entry, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" (from '{originalEntry}')";
                Logger.Info($"[INCLUDE] Added domain '{entry}'{normalizedSuffix} → {ips.Count} IPs");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[INCLUDE] Could not resolve '{entry}': {ex.Message}");
            }
        }
        _includedEntries[entry] = ips;
    }

    /// <summary>
    /// Remove a single domain or IP from the include list.
    /// </summary>
    public void RemoveIncludedDestination(string entry)
    {
        entry = NormalizeDestinationEntry(entry);
        if (_includedEntries.TryRemove(entry, out var ips))
        {
            foreach (var nbo in ips)
                _includedIps.TryRemove(nbo, out _);
            Logger.Info($"[INCLUDE] Removed '{entry}'");
        }
    }

    private bool IsIncludedDestination(uint dstIpNbo)
        => _includedIps.ContainsKey(dstIpNbo);

    #endregion
}
