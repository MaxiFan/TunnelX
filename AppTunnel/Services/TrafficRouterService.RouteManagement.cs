using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private bool _vpnServerPhysicalRouteAdded;
    private int _splitDefaultWatchTick;
    private int _unexpectedVpnDefaultRouteEvents;
    private volatile bool _hasVpnDefaultRoute;
    private bool _loggedInitialDefaultSnapshot;
    private bool _loggedSplitDefaultPresent;

    public bool IsFullRouteEnabled => _fullRouteEnabled;

    /// <summary>
    /// True when a 0.0.0.0/0 route is currently bound to the VPN interface.
    /// In split mode this is unexpected (silent full tunnel).
    /// </summary>
    public bool HasVpnDefaultRoute => _hasVpnDefaultRoute;

    public int UnexpectedVpnDefaultRouteEvents => _unexpectedVpnDefaultRouteEvents;

    public bool SetFullRouteEnabled(bool enabled)
    {
        if (!_isRunning && enabled) return false;
        if (enabled == _fullRouteEnabled) return true;

        if (enabled)
        {
            if (!AddVpnServerPhysicalRoute())
                Logger.Warning("[FULL-ROUTE] Could not pin VPN server to the physical gateway; enabling full-route may fail.");

            RemoveFullRouteDefault();
            var gateway = GetVpnRouteGateway();
            var added = TryRunRouteCommand($"add 0.0.0.0 mask 0.0.0.0 {gateway} IF {_vpnInterfaceIndex} METRIC 1", out var stderr);
            if (!added && gateway != "0.0.0.0")
            {
                Logger.Warning($"[FULL-ROUTE] Failed to add VPN default route via {gateway}; retrying on-link gateway. stderr={stderr.Trim()}");
                added = TryRunRouteCommand($"add 0.0.0.0 mask 0.0.0.0 0.0.0.0 IF {_vpnInterfaceIndex} METRIC 1", out stderr);
            }

            if (!added)
            {
                Logger.Warning($"[FULL-ROUTE] Failed to add VPN default route: {stderr}");
                RemoveVpnServerPhysicalRoute();
                return false;
            }

            _fullRouteEnabled = true;
            _hasVpnDefaultRoute = true;
            _loggedSplitDefaultPresent = true;
            InvalidateProcessCaches();
            RefreshExcludedDirectRoutes();
            Logger.Info($"[FULL-ROUTE] Enabled via VPN IF {_vpnInterfaceIndex}");
            return true;
        }

        RemoveExcludedDirectRoutes();
        RemoveFullRouteDefault();
        RemoveVpnServerPhysicalRoute();
        _fullRouteEnabled = false;
        _hasVpnDefaultRoute = false;
        _loggedSplitDefaultPresent = false;
        MarkPolicyTransitionGrace(TimeSpan.FromSeconds(25));
        CleanupRoutesForCurrentMode(dropStaleNat: true);
        InvalidateProcessCaches();
        Logger.Info("[FULL-ROUTE] Disabled; split routing restored");
        return true;
    }

    private bool AddVpnServerPhysicalRoute()
    {
        _vpnServerPhysicalRouteAdded = false;
        if (string.IsNullOrWhiteSpace(_vpnServerIp) || _vpnServerIp == "0.0.0.0")
            return false;
        if (string.IsNullOrWhiteSpace(_physicalGatewayIp) || _physicalInterfaceIndex <= 0)
            return false;

        TryRunRouteCommand($"delete {_vpnServerIp}", out _);
        var added = TryRunRouteCommand(
            $"add {_vpnServerIp} mask 255.255.255.255 {_physicalGatewayIp} IF {_physicalInterfaceIndex} METRIC 1",
            out _);
        _vpnServerPhysicalRouteAdded = added;
        return added;
    }

    private void RemoveVpnServerPhysicalRoute()
    {
        if (!_vpnServerPhysicalRouteAdded)
            return;

        if (!string.IsNullOrWhiteSpace(_vpnServerIp) && _vpnServerIp != "0.0.0.0")
            TryRunRouteCommand($"delete {_vpnServerIp}", out _);
        _vpnServerPhysicalRouteAdded = false;
    }

    private void RemoveFullRouteDefault()
    {
        TryRunRouteCommand($"delete 0.0.0.0 mask 0.0.0.0 IF {_vpnInterfaceIndex}", out _);
        RemoveDefaultRouteOnVpn();
    }

    private string GetVpnRouteGateway()
        => string.IsNullOrWhiteSpace(_vpnGatewayIp) ? "0.0.0.0" : _vpnGatewayIp;

    /// <summary>
    /// Delete 0.0.0.0/0 entries that are bound to the VPN interface only.
    /// Safe to call periodically: it does not spawn route.exe and cannot
    /// remove the Wi-Fi default gateway.
    /// </summary>
    private void RemoveVpnDefaultRouteByInterface()
    {
        if (_vpnInterfaceIndex <= 0)
            return;

        VpnDefaultRouteInspector.DeleteDefaultRoutesOnInterface(_vpnInterfaceIndex);

        foreach (int metric in new[] { 1, 6, 25, 26, 0 })
        {
            foreach (int proto in new[] { 3, 2, 5 }) // NETMGMT, LOCAL, ICMP/RIP
            {
                try
                {
                    var row = new MIB_IPFORWARDROW
                    {
                        dwForwardDest = 0,
                        dwForwardMask = 0,
                        dwForwardNextHop = 0,
                        dwForwardIfIndex = (uint)_vpnInterfaceIndex,
                        dwForwardType = 3,
                        dwForwardProto = (uint)proto,
                        dwForwardMetric1 = (uint)metric
                    };
                    IpHelperNative.DeleteIpForwardEntry(ref row);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Remove default routes (0.0.0.0/0) on the VPN interface so only
    /// explicitly added /32 host routes use the tunnel. Without this,
    /// some VPN servers push a default route via IPCP that makes the
    /// VPN act as full-route (all traffic tunnelled).
    /// Also ensures the VPN server itself remains reachable via the
    /// physical (intranet) gateway and that the VPN subnet route stays.
    /// </summary>
    private void RemoveDefaultRouteOnVpn()
    {
        try
        {
            var defaults = VpnDefaultRouteInspector.ReadDefaultRoutes();
            Logger.Info($"[ROUTE-DIAG] {VpnDefaultRouteInspector.FormatSummary(defaults, _vpnInterfaceIndex)}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ROUTE-DIAG] Failed to read IPv4 forward table: {ex.Message}");
        }

        VpnDefaultRouteInspector.DeleteDefaultRoutesOnInterface(_vpnInterfaceIndex);

        // Delete default route (0.0.0.0/0) on the VPN interface via route.exe.
        // We try multiple metric values because the server-pushed default route
        // may have a different metric than what we assume.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = $"delete 0.0.0.0 mask 0.0.0.0 IF {_vpnInterfaceIndex}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                Logger.Info($"[ROUTE] Remove default route on VPN IF {_vpnInterfaceIndex}: exit={proc.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ROUTE] Failed to remove default route on VPN: {ex.Message}");
        }

        // Interface-scoped. Does not match the physical NIC's default route.
        RemoveVpnDefaultRouteByInterface();

        try
        {
            var remaining = VpnDefaultRouteInspector.ReadDefaultRoutes();
            var vpnLeft = remaining.Count(r => r.IfIndex == (uint)_vpnInterfaceIndex);
            _hasVpnDefaultRoute = vpnLeft > 0;
            Logger.Info($"[ROUTE-AFTER] {VpnDefaultRouteInspector.FormatSummary(remaining, _vpnInterfaceIndex)}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ROUTE-AFTER] Failed to re-read IPv4 forward table: {ex.Message}");
        }
    }

    /// <summary>
    /// Split mode must not leave 0.0.0.0/0 on the VPN adapter. OpenVPN IPCP,
    /// WireGuard without Table=off, and TAP/Wintun can re-add it after bootstrap.
    /// </summary>
    private void EnforceSplitTunnelDefaultRoute(bool verboseLog)
    {
        if (_vpnInterfaceIndex <= 0)
            return;

        IReadOnlyList<VpnDefaultRouteInspector.Ipv4RouteRow> defaults;
        try
        {
            defaults = VpnDefaultRouteInspector.ReadDefaultRoutes();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ROUTE] Forward table read failed: {ex.Message}");
            return;
        }

        var vpnDefaults = VpnDefaultRouteInspector.OnInterface(defaults, _vpnInterfaceIndex);
        _hasVpnDefaultRoute = vpnDefaults.Count > 0;

        if (verboseLog || !_loggedInitialDefaultSnapshot)
        {
            _loggedInitialDefaultSnapshot = true;
            Logger.Info($"[ROUTE] {VpnDefaultRouteInspector.FormatSummary(defaults, _vpnInterfaceIndex)} fullRoute={_fullRouteEnabled}");
        }

        if (_fullRouteEnabled)
            return;

        if (vpnDefaults.Count == 0)
        {
            _loggedSplitDefaultPresent = false;
            return;
        }

        Logger.Warning($"[ROUTE] Unexpected 0.0.0.0/0 on VPN IF {_vpnInterfaceIndex} while split; stripping. {VpnDefaultRouteInspector.FormatSummary(vpnDefaults, _vpnInterfaceIndex)}");
        var deleted = VpnDefaultRouteInspector.DeleteDefaultRoutesOnInterface(_vpnInterfaceIndex);
        RemoveVpnDefaultRouteByInterface();

        var remaining = 0;
        try
        {
            remaining = VpnDefaultRouteInspector.OnInterface(VpnDefaultRouteInspector.ReadDefaultRoutes(), _vpnInterfaceIndex).Count;
        }
        catch { }

        if (remaining > 0)
        {
            Logger.Warning($"[ROUTE] IP helper left {remaining} VPN default(s); falling back to route.exe delete IF {_vpnInterfaceIndex}");
            TryRunRouteCommand($"delete 0.0.0.0 mask 0.0.0.0 IF {_vpnInterfaceIndex}", out var stderr);
            if (!string.IsNullOrWhiteSpace(stderr))
                Logger.Warning($"[ROUTE] route.exe delete stderr={stderr.Trim()}");
            try
            {
                remaining = VpnDefaultRouteInspector.OnInterface(VpnDefaultRouteInspector.ReadDefaultRoutes(), _vpnInterfaceIndex).Count;
            }
            catch { }
        }

        _hasVpnDefaultRoute = remaining > 0;
        var appearedAfterClean = _routeBootstrapReady && remaining > 0 && !_loggedSplitDefaultPresent;
        if (appearedAfterClean)
            Interlocked.Increment(ref _unexpectedVpnDefaultRouteEvents);
        _loggedSplitDefaultPresent = remaining > 0;

        Logger.Info($"[ROUTE] Split strip result helperDeleted={deleted} vpnDefaultRemaining={remaining} unexpectedEvents={_unexpectedVpnDefaultRouteEvents}");
    }

    /// <summary>
    /// Adds a host route (dstIP/32) via the VPN interface so the Windows routing
    /// layer actually sends our reinjected packets out the VPN adapter.
    /// Without this, with split-tunneling enabled the default route (Wi-Fi/ethernet)
    /// wins and the kernel silently drops the packet due to source-address mismatch
    /// (strong host model).
    /// </summary>
    private bool EnsureHostRouteViaVpn(uint dstIpNbo, IPAddress dstIpForLog)
    {
        // Exclude = direct outside tunnel (never install a VPN /32 for excluded IPs).
        if (IsExcludedDestination(dstIpNbo))
            return false;

        // Per-app mode: NCSI destinations stay on the physical default route.
        // Full-route still reaches them through the VPN default route.
        if (!_fullRouteEnabled && WindowsConnectivityGuard.IsProbeIpv4(dstIpNbo))
            return false;

        // Skip private/multicast/broadcast ranges that should not be routed via VPN.
        byte b0 = (byte)(dstIpNbo & 0xFF);
        if (b0 == 127 || b0 >= 224) return false;

        if (_addedRoutes.ContainsKey(dstIpNbo)) return false;
        if (!_addedRoutes.TryAdd(dstIpNbo, true)) return false;

        try
        {
            // Use route.exe directly — CreateIpForwardEntry consistently returns
            // errno=160 (ERROR_BAD_ARGUMENTS) on modern Windows PPP interfaces.
            bool ok = TryAddRouteViaCommandLine(dstIpForLog);
            if (ok)
            {
                long added = Interlocked.Increment(ref _statRoutesAdded);
                if (added <= 5)
                    Logger.Info($"[ROUTE+] Added host route {dstIpForLog}/32 via route.exe (#{added})");
                return true;
            }
            else
            {
                Interlocked.Increment(ref _statRoutesFailed);
                _addedRoutes.TryRemove(dstIpNbo, out _);
                if (Interlocked.Read(ref _statRoutesFailed) <= 5)
                    Logger.Warning($"[ROUTE!] route.exe failed for {dstIpForLog}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _addedRoutes.TryRemove(dstIpNbo, out _);
            Logger.Warning($"[ROUTE!] Exception adding host route {dstIpForLog}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Called by the SOCKS5 proxy to install a /32 host route for the
    /// destination IP it is about to connect to. Without this route,
    /// the outbound socket (bound to VPN IP) can't reach the remote host
    /// because the routing table has no path for it via the VPN interface.
    /// </summary>
    private void EnsureHostRouteForSocks5(IPAddress remoteIp)
    {
        var ipBytes = remoteIp.GetAddressBytes();
        uint nbo = BitConverter.ToUInt32(ipBytes, 0);
        if (IsExcludedDestination(nbo))
        {
            Logger.Info($"[SOCKS5-EXCLUDED] Route for {remoteIp} skipped (destination excluded)");
            return;
        }
        EnsureHostRouteViaVpn(nbo, remoteIp);
    }

    /// <summary>
    /// Public method to install a /32 host route for an arbitrary IP through VPN.
    /// Used by the ping feature to route ping packets via the tunnel.
    /// </summary>
    public void EnsureRouteViaVpn(string ipAddress)
    {
        if (!_isRunning) return;
        if (!IPAddress.TryParse(ipAddress, out var ip)) return;
        var ipBytes = ip.GetAddressBytes();
        uint nbo = BitConverter.ToUInt32(ipBytes, 0);
        EnsureHostRouteViaVpn(nbo, ip);
    }

    private int _routeGcLogCount = 0;

    /// <summary>
    /// Schedule removal of the /32 host route after a grace period.
    /// If a new target-app flow for the same IP appears before the
    /// timer fires, the removal is cancelled (see FLOW_ESTABLISHED).
    /// </summary>
    /// <summary>
    /// Drop a /32 off the packet thread. Used for NCSI, exclude, and blocked
    /// senders so the hot path can reinject immediately.
    /// </summary>
    private void ScheduleImmediateHostRouteRemoval(uint dstIpNbo)
    {
        if (!_immediateRouteRemovalQueued.TryAdd(dstIpNbo, 1))
            return;

        if (_pendingRouteRemoval.TryRemove(dstIpNbo, out var pending))
        {
            try { pending.Cancel(); } catch { }
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!_isRunning)
                    return;

                _ipToProcess.TryRemove(dstIpNbo, out _);
                _ipRefCount.TryRemove(dstIpNbo, out _);
                _loggedMatchIps.TryRemove(dstIpNbo, out _);
                _loggedExcludedIps.TryRemove(dstIpNbo, out _);
                foreach (var flowKey in _flowOwnerByTuple.Keys.Where(k => k.remoteIp == dstIpNbo).ToList())
                    _flowOwnerByTuple.TryRemove(flowKey, out _);
                TryRemoveHostRoute(dstIpNbo);
            }
            finally
            {
                _immediateRouteRemovalQueued.TryRemove(dstIpNbo, out _);
            }
        });
    }

    /// <summary>
    /// Arm the grace-period remover once. Repeated calls must not push the
    /// deadline out, or background traffic to a stale CDN IP would keep the
    /// /32 forever.
    /// </summary>
    private void ScheduleDelayedRouteRemovalIfIdle(uint dstIpNbo)
    {
        if (_pendingRouteRemoval.ContainsKey(dstIpNbo) || _immediateRouteRemovalQueued.ContainsKey(dstIpNbo))
            return;
        ScheduleDelayedRouteRemoval(dstIpNbo);
    }

    private void ScheduleDelayedRouteRemoval(uint dstIpNbo)
    {
        var cts = new CancellationTokenSource();
        // If there's already a pending removal, cancel it and replace.
        if (_pendingRouteRemoval.TryGetValue(dstIpNbo, out var prev))
            try { prev.Cancel(); } catch { }
        _pendingRouteRemoval[dstIpNbo] = cts;

        _ = Task.Delay(TimeSpan.FromSeconds(RouteRemovalGraceSeconds), cts.Token)
            .ContinueWith(t =>
            {
                if (!_pendingRouteRemoval.TryRemove(dstIpNbo, out var removed)) return;

                if (IsIncludedDestination(dstIpNbo) || IsPinnedWireGuardInfrastructureRoute(dstIpNbo))
                {
                    if (IsIncludedDestination(dstIpNbo))
                        _ipToProcess[dstIpNbo] = "[INCLUDE]";
                    return;
                }

                // Defer removal if NAT still has a recently-active entry for
                // this destination. Otherwise route GC races against TCP
                // retransmits / lingering target-app connections, causing
                // a transient LEAK (packet exits physical NIC with VPN srcIP
                // before the LEAK handler restores the route).
                // Keep routes longer than the user-visible GC grace if NAT still
                // has a live mapping. WireGuard retransmits can arrive after the
                // FLOW_DELETED event for short-lived sockets; removing the route
                // too eagerly sends VPN-source packets toward the physical NIC
                // where leak-guard has to drop them.
                var recentCutoff = DateTime.UtcNow.AddSeconds(-Math.Max(RouteRemovalGraceSeconds, 180));
                bool natActive = false;
                foreach (var kv in _natTable)
                {
                    var k = kv.Key;
                    if (k.dstIp == dstIpNbo && kv.Value.LastSeen >= recentCutoff)
                    {
                        natActive = true;
                        break;
                    }
                }
                if (natActive)
                {
                    // Reschedule another grace period; do not remove route yet.
                    ScheduleDelayedRouteRemoval(dstIpNbo);
                    return;
                }

                _ipToProcess.TryRemove(dstIpNbo, out var removedOwner);
                foreach (var flowKey in _flowOwnerByTuple.Keys.Where(k => k.remoteIp == dstIpNbo))
                    _flowOwnerByTuple.TryRemove(flowKey, out _);
                TryRemoveHostRoute(dstIpNbo);
                if (Interlocked.Increment(ref _routeGcLogCount) <= 5)
                {
                    var ipBytes = BitConverter.GetBytes(dstIpNbo);
                    Logger.Info($"[ROUTE-GC] Removed delayed route for {new IPAddress(ipBytes)}");
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private bool IsPinnedWireGuardInfrastructureRoute(uint dstIpNbo)
    {
        if (!_vpnServerIsUdpOnly)
            return false;

        if (dstIpNbo == _dnsRedirectIpNbo)
            return true;

        foreach (var resolver in DnsResolverCache.DohBootstrapResolvers)
        {
            if (BitConverter.ToUInt32(resolver.GetAddressBytes(), 0) == dstIpNbo)
                return true;
        }

        return false;
    }

    private void TryRemoveHostRoute(uint dstIpNbo)
    {
        if (!_addedRoutes.TryRemove(dstIpNbo, out _)) return;

        bool removed = false;
        try
        {
            var row = new MIB_IPFORWARDROW
            {
                dwForwardDest = dstIpNbo,
                dwForwardMask = 0xFFFFFFFF,
                dwForwardNextHop = 0,
                dwForwardIfIndex = (uint)_vpnInterfaceIndex,
                dwForwardType = 3,
                dwForwardProto = 3,
                dwForwardMetric1 = 1,
                dwForwardMetric2 = 0,
                dwForwardMetric3 = 0,
                dwForwardMetric4 = 0,
                dwForwardMetric5 = 0
            };
            if (IpHelperNative.DeleteIpForwardEntry(ref row) == 0)
                removed = true;
        }
        catch { }

        if (!removed)
        {
            try
            {
                var ipBytes = BitConverter.GetBytes(dstIpNbo);
                var dstIp = new IPAddress(ipBytes);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "route.exe",
                    Arguments = $"delete {dstIp}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null && proc.WaitForExit(1500) && proc.ExitCode == 0)
                    removed = true;
            }
            catch { }
        }
    }

    /// <summary>
    /// Fallback: add a host route using the route.exe command line tool.
    /// This works on modern Windows versions where CreateIpForwardEntry
    /// rejects arguments with ERROR_BAD_ARGUMENTS.
    /// </summary>
    private bool TryAddRouteViaCommandLine(IPAddress dstIp)
    {
        var gateway = GetVpnRouteGateway();
        var ok = TryRunRouteCommand($"add {dstIp} mask 255.255.255.255 {gateway} IF {_vpnInterfaceIndex} METRIC 1", out var stderr);
        if (!ok && Interlocked.Read(ref _statRoutesFailed) <= 10)
            Logger.Warning($"[ROUTE!] route.exe add {dstIp} via {gateway} stderr='{stderr.Trim()}'");
        return ok;
    }

    private static bool TryRunRouteCommand(string arguments, out string stderr)
    {
        stderr = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Remove all host routes we added during this session.
    /// </summary>
    private void RemoveAllHostRoutes()
    {
        int removed = 0;
        foreach (var kv in _addedRoutes)
        {
            var row = new MIB_IPFORWARDROW
            {
                dwForwardDest = kv.Key,
                dwForwardMask = 0xFFFFFFFF,
                dwForwardNextHop = 0,
                dwForwardIfIndex = (uint)_vpnInterfaceIndex,
                dwForwardType = 3,
                dwForwardProto = 3,
                dwForwardMetric1 = 1,
                dwForwardMetric2 = 0,
                dwForwardMetric3 = 0,
                dwForwardMetric4 = 0,
                dwForwardMetric5 = 0
            };
            try
            {
                if (IpHelperNative.DeleteIpForwardEntry(ref row) == 0)
                {
                    removed++;
                    continue;
                }
            }
            catch { }
            // Fallback: use route.exe to delete.
            try
            {
                var ipBytes = BitConverter.GetBytes(kv.Key);
                var dstIp = new IPAddress(ipBytes);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "route.exe",
                    Arguments = $"delete {dstIp}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null && proc.WaitForExit(2000) && proc.ExitCode == 0)
                    removed++;
            }
            catch { }
        }
        _addedRoutes.Clear();
        _ipToProcess.Clear();
        _ipRefCount.Clear();
        _flowOwnerByTuple.Clear();
        _loggedMatchIps.Clear();
        _loggedExcludedIps.Clear();
        Logger.Info($"[ROUTE-] Removed {removed} host routes on stop");
    }

    /// <summary>
    /// Removes stale route ownership entries that are no longer valid for the
    /// active routing mode.
    /// </summary>
    private void CleanupRoutesForCurrentMode(bool dropStaleNat = false)
    {
        var removedRouteIps = new HashSet<uint>();

        // Reconcile against all installed host routes (not just mapped ones).
        foreach (var ipNbo in _addedRoutes.Keys)
        {
            bool keep = IsRouteAllowedInCurrentMode(ipNbo);

            if (keep)
                continue;

            _ipToProcess.TryRemove(ipNbo, out _);
            _ipRefCount.TryRemove(ipNbo, out _);
            foreach (var flowKey in _flowOwnerByTuple.Keys.Where(k => k.remoteIp == ipNbo))
                _flowOwnerByTuple.TryRemove(flowKey, out _);
            _loggedMatchIps.TryRemove(ipNbo, out _);
            _loggedExcludedIps.TryRemove(ipNbo, out _);
            if (_pendingRouteRemoval.TryRemove(ipNbo, out var pending))
            {
                try { pending.Cancel(); } catch { }
            }
            TryRemoveHostRoute(ipNbo);
            _recentLeakByDst.TryRemove(ipNbo, out _);
            removedRouteIps.Add(ipNbo);
        }

        if (!dropStaleNat)
            return;

        int natRemoved = 0;
        foreach (var kv in _natTable)
        {
            bool remove = removedRouteIps.Contains(kv.Key.dstIp);
            if (!remove)
            {
                remove = !IsRouteAllowedInCurrentMode(kv.Key.dstIp);
            }

            if (remove && _natTable.TryRemove(kv.Key, out _))
                natRemoved++;
        }

        if (natRemoved > 0 || removedRouteIps.Count > 0)
        {
            Logger.Info($"[FULL-ROUTE] Split reconcile: removedRoutes={removedRouteIps.Count}, removedNat={natRemoved}");
        }
    }

    private bool IsRouteAllowedInCurrentMode(uint dstIpNbo)
    {
        if (IsExcludedDestination(dstIpNbo))
            return false;

        if (_fullRouteEnabled)
            return true;

        if (IsIncludedDestination(dstIpNbo))
            return true;

        if (!_ipToProcess.TryGetValue(dstIpNbo, out var owner) || string.IsNullOrWhiteSpace(owner))
            return false;

        if (string.Equals(owner, "[INCLUDE]", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(owner, "[FULL-ROUTE]", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsExecutableTargeted(owner);
    }
}
