using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, long> _recentLeakByDst = new();

    /// <summary>
    /// Diagnostic SNIFF handle that passively observes all OUTBOUND packets exiting
    /// the VPN interface. Used to verify whether our rewritten packets actually leave
    /// via VPN (or whether Windows routes them elsewhere despite our IfIdx hint).
    /// </summary>
    private void VpnEgressSniffLoop(CancellationToken ct)
    {
        try
        {
            // SNIFF (0x1) + RECV_ONLY (0x4) = read-only, does not affect traffic
            const ulong FLAG_SNIFF_READONLY = 0x5;
            string filter = $"outbound and ifIdx == {_vpnInterfaceIndex} and (tcp or udp)";
            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 1000, FLAG_SNIFF_READONLY);
                _vpnSniffHandle = h;
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Logger.Warning($"[SNIFF] VPN egress sniff open failed: {Marshal.GetLastWin32Error()}. Diagnostic disabled.");
                return;
            }
            Logger.Info($"[SNIFF] VPN egress sniff opened (filter='{filter}')");

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();
            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);
                if (!ok) { if (ct.IsCancellationRequested || !_isRunning) break; continue; }
                Interlocked.Increment(ref _statVpnEgressSniffed);
                if (readLen >= 20 && _vpnLocalIpBytes != null)
                {
                    bool fromOurIp =
                        buffer[12] == _vpnLocalIpBytes[0] &&
                        buffer[13] == _vpnLocalIpBytes[1] &&
                        buffer[14] == _vpnLocalIpBytes[2] &&
                        buffer[15] == _vpnLocalIpBytes[3];
                    if (fromOurIp) Interlocked.Increment(ref _statVpnEgressFromOurIp);

                    // Count every outbound VPN byte toward the total usage,
                    // regardless of per-app attribution.
                    if (!fromOurIp)
                        continue;

                    Interlocked.Add(ref _totalVpnBytesSent, readLen);

                    // Attribute bytes to the target app owning this destination.
                    // First try per-flow ownership to avoid destination-IP collisions
                    // between different apps, then fallback to destination mapping.
                    uint dstNbo = BitConverter.ToUInt32(buffer, 16);
                    string? procName = null;
                    if (TryParseConnectionTuple(buffer, readLen, out var tuple) &&
                        _flowOwnerByTuple.TryGetValue((tuple.Protocol, tuple.LocalPort, dstNbo), out var flowOwner))
                    {
                        procName = flowOwner;
                    }
                    else if (_ipToProcess.TryGetValue(dstNbo, out var dstOwner))
                    {
                        procName = dstOwner;
                    }

                    if (!string.IsNullOrWhiteSpace(procName) &&
                        _trafficCounters.TryGetValue(procName, out var counter))
                    {
                        Interlocked.Add(ref counter.BytesSent, readLen);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[SNIFF] VPN egress loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sniff INBOUND packets arriving on the VPN interface and attribute their
    /// byte counts to the target application. The source IP of the packet is
    /// used as the key into the IP→process map built by FlowTrackingLoop.
    /// </summary>
    private void VpnIngressSniffLoop(CancellationToken ct)
    {
        try
        {
            const ulong FLAG_SNIFF_READONLY = 0x5;
            string filter = $"inbound and ifIdx == {_vpnInterfaceIndex} and (tcp or udp)";
            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 1002, FLAG_SNIFF_READONLY);
                _vpnIngressHandle = h;
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Logger.Warning($"[SNIFF] VPN ingress sniff open failed: {Marshal.GetLastWin32Error()}. Download counters disabled.");
                return;
            }
            Logger.Info($"[SNIFF] VPN ingress sniff opened (filter='{filter}')");

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();
            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);
                if (!ok) { if (ct.IsCancellationRequested || !_isRunning) break; continue; }
                if (readLen < 20) continue;

                // Count every inbound VPN byte toward the total usage.
                Interlocked.Add(ref _totalVpnBytesReceived, readLen);

                // Source IP of an inbound packet sits at offset 12..15 (NBO).
                // Prefer per-flow ownership key (proto + localPort + remoteIp)
                // and fallback to remote IP mapping for older/stale flows.
                uint srcNbo = BitConverter.ToUInt32(buffer, 12);
                string? procName = null;
                if (TryParseConnectionTuple(buffer, readLen, out var tupleIn) &&
                    _flowOwnerByTuple.TryGetValue((tupleIn.Protocol, tupleIn.RemotePort, srcNbo), out var flowOwner))
                {
                    procName = flowOwner;
                }
                else if (_ipToProcess.TryGetValue(srcNbo, out var dstOwner))
                {
                    procName = dstOwner;
                }

                if (!string.IsNullOrWhiteSpace(procName) &&
                    _trafficCounters.TryGetValue(procName, out var counter))
                {
                    Interlocked.Add(ref counter.BytesReceived, readLen);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[SNIFF] VPN ingress loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Active leak-guard handle:
    /// Captures outbound packets exiting NON-VPN interfaces with source IP equal to
    /// our VPN IP, then drops them before they leave the host. This prevents real
    /// split-tunnel leakage during route-transition races (especially after toggling
    /// full-route OFF while old sockets still retransmit).
    ///
    /// For destinations still allowed by current policy, we also restore/ensure the
    /// host route so subsequent retransmits naturally go through the VPN interface.
    /// </summary>
    private void PhysicalEgressSniffLoop(CancellationToken ct)
    {
        try
        {
            // Active capture (not SNIFF/RECV_ONLY): packets are dropped unless reinjected.
            const ulong FLAG_ACTIVE_DROP = 0;
            string filter = $"outbound and ifIdx != {_vpnInterfaceIndex} and ip.SrcAddr == {_vpnLocalIp}";
            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 1001, FLAG_ACTIVE_DROP);
                _physSniffHandle = h;
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Logger.Warning($"[LEAK-GUARD] Physical leak-guard open failed: {Marshal.GetLastWin32Error()}.");
                return;
            }
            Logger.Info($"[LEAK-GUARD] Active leak-guard opened (filter='{filter}')");

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();
            int leakLogCount = 0;
            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);
                if (!ok) { if (ct.IsCancellationRequested || !_isRunning) break; continue; }
                if (readLen >= 20)
                {
                    var dst = new IPAddress(new byte[] { buffer[16], buffer[17], buffer[18], buffer[19] });
                    uint dstNbo = BitConverter.ToUInt32(buffer, 16);
                    long now = Environment.TickCount64;
                    bool countLeak = true;
                    if (_recentLeakByDst.TryGetValue(dstNbo, out var lastTick) && (now - lastTick) < 30000)
                        countLeak = false;
                    _recentLeakByDst[dstNbo] = now;

                    // Auto-recover only when current policy still allows this destination.
                    // This avoids re-adding stale full-route destinations after switching
                    // back to split mode (which could keep non-target apps tunneled).
                    bool recovered = false;
                    if (IsRouteAllowedInCurrentMode(dstNbo))
                    {
                        EnsureHostRouteViaVpn(dstNbo, dst);
                        recovered = true;
                    }
                    else
                    {
                        _ipToProcess.TryRemove(dstNbo, out _);
                        _ipRefCount.TryRemove(dstNbo, out _);
                        _loggedMatchIps.TryRemove(dstNbo, out _);
                        _loggedExcludedIps.TryRemove(dstNbo, out _);
                        if (_pendingRouteRemoval.TryRemove(dstNbo, out var pending))
                        {
                            try { pending.Cancel(); } catch { }
                        }
                        TryRemoveHostRoute(dstNbo);
                    }

                    if (countLeak && leakLogCount < 5)
                    {
                        bool graceSuppressed = !recovered && IsPolicyTransitionGraceActive();
                        if (graceSuppressed)
                            Interlocked.Increment(ref _statLeakBlockedSuppressed);
                        else
                        {
                            Interlocked.Increment(ref _statLeakBlocked);
                            if (recovered)
                                Interlocked.Increment(ref _statLeakBlockedRecovered);
                        }

                        leakLogCount++;
                        if (recovered)
                            Logger.Warning($"[LEAK-BLOCKED] Packet with VPN srcIP exiting PHYSICAL ifIdx={addrBuf.IfIdx} → dst={dst} (proto={buffer[9]}) — blocked locally, route restored for retransmit via VPN");
                        else if (graceSuppressed)
                            Logger.Info($"[LEAK-BLOCKED-TRANSITION] Packet with VPN srcIP exiting PHYSICAL ifIdx={addrBuf.IfIdx} → dst={dst} (proto={buffer[9]}) — blocked during policy transition grace");
                        else
                            Logger.Warning($"[LEAK-BLOCKED] Packet with VPN srcIP exiting PHYSICAL ifIdx={addrBuf.IfIdx} → dst={dst} (proto={buffer[9]}) — blocked by split policy, route not restored");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[SNIFF] Physical egress loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Read-only counter for traffic that leaves or enters through the physical
    /// NIC without being the tunnel carrier connection. This gives the UI a real
    /// "outside tunnel" number instead of estimating it by subtracting TUN bytes
    /// from physical adapter counters.
    /// </summary>
    private void PhysicalDirectTrafficSniffLoop(CancellationToken ct)
    {
        if (_physicalInterfaceIndex <= 0)
        {
            Logger.Info("[DIRECT-SNIFF] No physical interface index; direct traffic counter disabled.");
            return;
        }

        try
        {
            const ulong FLAG_SNIFF_READONLY = 0x5;
            string filter = $"ifIdx == {_physicalInterfaceIndex} and ip and (tcp or udp) " +
                $"and ip.SrcAddr != {_vpnLocalIp} " +
                $"and ip.DstAddr != {_vpnLocalIp} " +
                $"and ip.SrcAddr != {_vpnServerIp} " +
                $"and ip.DstAddr != {_vpnServerIp}";

            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 1003, FLAG_SNIFF_READONLY);
                _directSniffHandle = h;
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Logger.Warning($"[DIRECT-SNIFF] Open failed: {Marshal.GetLastWin32Error()}. Direct traffic counter disabled.");
                return;
            }
            Logger.Info($"[DIRECT-SNIFF] Opened (filter='{filter}')");

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();
            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);
                if (!ok) { if (ct.IsCancellationRequested || !_isRunning) break; continue; }
                if (readLen < 20) continue;

                if (addrBuf.Outbound)
                    Interlocked.Add(ref _directBytesSent, readLen);
                else
                    Interlocked.Add(ref _directBytesReceived, readLen);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[DIRECT-SNIFF] Loop error: {ex.Message}");
        }
    }
}
