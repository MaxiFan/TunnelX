using System.Net;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    #region Excluded Destinations

    /// <summary>
    /// Replace the entire exclude list. Resolves domains to IPs.
    /// </summary>
    public void SetExcludedDestinations(IEnumerable<string> entries)
    {
        _excludedIps.Clear();
        _excludedEntries.Clear();
        foreach (var entry in entries)
            AddExcludedDestination(entry);
    }

    /// <summary>
    /// Add a single domain or IP to the exclude list.
    /// If the tunnel is already running and a host route for any of the resolved
    /// IPs was previously installed (e.g. the user browsed there before excluding
    /// it), the route is removed immediately so that subsequent connections bypass
    /// the VPN rather than continuing to use the stale route.
    /// Also removes stale routes left over from a previously crashed session
    /// (even if _isRunning is false at this point), since those routes are not
    /// tracked in _addedRoutes but can still silently redirect traffic to the VPN.
    /// </summary>
    public void AddExcludedDestination(string entry)
    {
        var originalEntry = entry.Trim();
        entry = NormalizeDestinationEntry(originalEntry);
        if (string.IsNullOrEmpty(entry)) return;
        if (_excludedEntries.ContainsKey(entry)) return;

        var ips = new HashSet<uint>();
        if (IPAddress.TryParse(entry, out var ip))
        {
            var nbo = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            ips.Add(nbo);
            _excludedIps[nbo] = true;
            PurgeRouteForExcludedIp(nbo, ip);
            Logger.Info($"[EXCLUDE] Added IP {entry}");
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
                        _excludedIps[nbo] = true;
                        PurgeRouteForExcludedIp(nbo, addr);
                    }
                }
                var normalizedSuffix = originalEntry.Equals(entry, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" (from '{originalEntry}')";
                Logger.Info($"[EXCLUDE] Added domain '{entry}'{normalizedSuffix} → {ips.Count} IPs");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[EXCLUDE] Could not resolve '{entry}': {ex.Message}");
            }
        }
        _excludedEntries[entry] = ips;
    }

    /// <summary>
    /// Removes any /32 host route for <paramref name="nbo"/> that may exist in
    /// the Windows routing table — regardless of whether TunnelX added it in this
    /// session (tracked via <see cref="_addedRoutes"/>) or it is a stale route
    /// left behind by a previous session that crashed without cleaning up.
    /// Also cancels any pending delayed-removal timer and clears the per-session
    /// flow-tracking state for the IP, so the egress/ingress sniff loops stop
    /// attributing bytes and the NAT table stops matching replies.
    /// </summary>
    private void PurgeRouteForExcludedIp(uint nbo, IPAddress ipForLog)
    {
        // Cancel any pending delayed removal (superseded by this explicit remove).
        if (_pendingRouteRemoval.TryRemove(nbo, out var pendingCts))
            try { pendingCts.Cancel(); } catch { }

        // Clear in-session tracking state.
        _addedRoutes.TryRemove(nbo, out _);
        _ipToProcess.TryRemove(nbo, out _);
        _ipRefCount.TryRemove(nbo, out _);

        // Force-delete from the Windows routing table using route.exe.
        // This covers:
        //   1. Routes that TunnelX added in the current session.
        //   2. Stale routes from a previous session that crashed before StopAsync
        //      could call RemoveAllHostRoutes().
        // We do NOT gate this on _isRunning — stale routes can be present even
        // before the tunnel starts, and we must clean them up proactively.
        ForceDeleteRouteFromWindows(ipForLog);
    }

    /// <summary>
    /// Unconditionally removes the /32 host route for <paramref name="ip"/> from
    /// the Windows routing table via route.exe delete. Does not touch _addedRoutes.
    /// </summary>
    internal void ForceDeleteRouteFromWindows(IPAddress ip)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = $"delete {ip} mask 255.255.255.255",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(1500);
        }
        catch { }
    }

    /// <summary>
    /// Remove a single domain or IP from the exclude list.
    /// </summary>
    public void RemoveExcludedDestination(string entry)
    {
        entry = NormalizeDestinationEntry(entry);
        if (_excludedEntries.TryRemove(entry, out var ips))
        {
            foreach (var nbo in ips)
                _excludedIps.TryRemove(nbo, out _);
            Logger.Info($"[EXCLUDE] Removed '{entry}'");
        }
    }

    private bool IsExcludedDestination(uint dstIpNbo)
        => _excludedIps.ContainsKey(dstIpNbo);

    #endregion
}
