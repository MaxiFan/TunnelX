using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private IntPtr _networkOutHandle = IntPtr.Zero;
    private IntPtr _networkInHandle = IntPtr.Zero;
    private Task? _networkOutTask;
    private Task? _networkInTask;

    /// <summary>
    /// Intercepts outbound packets from the physical NIC that are destined for
    /// VPN-routed addresses. Rewrites the source IP to the VPN local IP and
    /// re-injects on the VPN interface.
    ///
    /// Without this, sockets that called connect() before the host route was
    /// installed remain bound to the physical IP. The Windows strong-host model
    /// silently drops their packets when the routing table directs them to the
    /// VPN interface (source IP mismatch), causing connection timeouts.
    /// </summary>
    private void NetworkOutboundLoop(CancellationToken ct)
    {
        try
        {
            // Capture outbound IPv4 TCP/UDP not already from VPN IP.
            // Packets sourced from VPN IP are correctly routed natively.
            string filter = $"outbound and ip and (tcp or udp) " +
                $"and ip.SrcAddr != {_vpnLocalIp} " +
                $"and ip.DstAddr != {_vpnServerIp} " +
                $"and ip.DstAddr != 127.0.0.1";

            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 0, 0);
                _networkOutHandle = h;
            }

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Error($"[NET-OUT] WinDivert open failed: {err} ({GetWinDivertErrorMessage(err)})");
                return;
            }

            Logger.Info($"[NET-OUT] Packet intercept handle opened (filter='{filter}')");
            WinDivertNative.WinDivertSetParam(h, 0, 16384);
            WinDivertNative.WinDivertSetParam(h, 1, 2000);
            WinDivertNative.WinDivertSetParam(h, 2, 33554432);

            var buffer = new byte[65535];
            var addr = new WinDivertAddress();
            var connCache = new ConnectionProcessCache();
            var lastRefresh = DateTime.MinValue;
            int rewriteLogCount = 0;
            int dnsRedirectLogCount = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                if (readLen < 20 || _vpnLocalIpBytes == null)
                {
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                uint dstNbo = BitConverter.ToUInt32(buffer, 16);

                // ── Fast path: destination already has a VPN host route ──
                if (_addedRoutes.ContainsKey(dstNbo))
                {
                    bool isIncluded = IsIncludedDestination(dstNbo);
                    bool shouldRoute = _fullRouteEnabled || isIncluded;
                    string? packetProc = null;
                    bool isBlockedProc = false;

                    if (TryParseConnectionTuple(buffer, readLen, out var probeTuple))
                    {
                        if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                        }

                        packetProc = connCache.GetProcessName(probeTuple);
                        if (packetProc == null)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                            packetProc = connCache.GetProcessName(probeTuple);
                        }

                        isBlockedProc = !_fullRouteEnabled && IsExecutableBlocked(packetProc);
                        if (!shouldRoute && !isBlockedProc && IsExecutableTargeted(packetProc))
                        {
                            shouldRoute = true;
                            _ipToProcess[dstNbo] = packetProc;
                        }
                    }

                    // If this destination is no longer valid for current policy,
                    // remove stale state and pass through unchanged.
                    if (IsExcludedDestination(dstNbo) || !shouldRoute || isBlockedProc)
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
                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                        continue;
                    }

                    if (packetProc != null)
                        _ipToProcess[dstNbo] = isIncluded ? "[INCLUDE]" : packetProc;

                    // Save original source IP before overwriting
                    var origSrc = new byte[4];
                    Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);
                    var origIfIdx = addr.IfIdx;

                    // Rewrite source IP to VPN
                    Buffer.BlockCopy(_vpnLocalIpBytes, 0, buffer, 12, 4);
                    addr.IfIdx = (uint)_vpnInterfaceIndex;
                    addr.SubIfIdx = 0;
                    ApplyGameModePacketTuning(buffer, readLen);
                    WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);

                    // Record NAT + attribute traffic (if parseable)
                    if (TryParseConnectionTuple(buffer, readLen, out var tuple))
                    {
                        var processName = _ipToProcess.TryGetValue(dstNbo, out var pName) ? pName : "unknown";
                        _natTable[(tuple.Protocol, tuple.LocalPort, dstNbo)] = new NatEntry
                        {
                            OriginalSrcIp = origSrc,
                            PhysicalIfIdx = origIfIdx,
                            ProcessName = processName,
                            LastSeen = DateTime.UtcNow
                        };
                    }

                    Interlocked.Increment(ref _statNetOutRewritten);
                    if (!WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr))
                    {
                        long f = Interlocked.Increment(ref _statNetOutSendFailed);
                        if (f <= 5)
                        {
                            int err = Marshal.GetLastWin32Error();
                            Logger.Warning($"[NET-OUT] Fast-path send failed #{f}: err={err} dst={new IPAddress(BitConverter.GetBytes(dstNbo))}");
                        }
                    }
                    continue;
                }

                // ── Slow path: no route yet — check connection table for PID ──
                if (TryParseConnectionTuple(buffer, readLen, out var tuple2))
                {
                    // Skip private/multicast/broadcast ranges
                    byte[] dstBytes = BitConverter.GetBytes(dstNbo);
                    byte b0 = dstBytes[0], b1 = dstBytes[1];
                    bool isPrivate = b0 == 0 || b0 == 127 || b0 >= 224 ||
                        b0 == 10 || (b0 == 172 && b1 >= 16 && b1 <= 31) ||
                        (b0 == 192 && b1 == 168) || (b0 == 169 && b1 == 254);

                    // ── DNS redirect ────────────────────────────────────────────────────
                    // Chrome's plain-UDP/TCP DNS (port 53) goes to the local router
                    // (192.168.32.x — a private IP). Private IPs are excluded from
                    // normal rewriting, so DNS bypasses the VPN and lands on the ISP's
                    // DNS resolver. In filtered networks (e.g. Iran) that resolver returns
                    // NXDOMAIN or fake IPs for sites like facebook.com, preventing any
                    // connection even though our tunnel is up.
                    //
                    // Fix: intercept port-53 traffic from target apps aimed at private
                    // or excluded DNS servers and silently redirect it to the optimized
                    // public DNS target which transits the VPN. On the inbound path the source IP
                    // is spoofed back to the original server so the OS/Chrome accepts the
                    // response (it came from the "expected" address).
                    if (tuple2.RemotePort == 53 && (isPrivate || IsExcludedDestination(dstNbo)))
                    {
                        if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                        }
                        var dnsProc = connCache.GetProcessName(tuple2);
                        if (dnsProc == null)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                            dnsProc = connCache.GetProcessName(tuple2);
                        }

                        if (IsExecutableTargeted(dnsProc))
                        {
                            uint publicDnsNbo = _dnsRedirectIpNbo;

                            // Save original DNS server IP so we can spoof it back on the response
                            var dnsOrigDst = new byte[4];
                            Buffer.BlockCopy(buffer, 16, dnsOrigDst, 0, 4);

                            // Save physical src IP and interface for reverse NAT
                            var origSrc = new byte[4];
                            Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);
                            uint origIfIdx = addr.IfIdx;

                            // Rewrite: dst → optimized DNS, src → VPN IP, send via TUN
                            Buffer.BlockCopy(_dnsRedirectIpBytes, 0, buffer, 16, 4);
                            Buffer.BlockCopy(_vpnLocalIpBytes, 0, buffer, 12, 4);
                            addr.IfIdx = (uint)_vpnInterfaceIndex;
                            addr.SubIfIdx = 0;
                            ApplyGameModePacketTuning(buffer, readLen);
                            WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);

                            // Ensure optimized DNS target has a host route via VPN
                            var dnsRouteIp = _dnsRedirectIp;
                            _ = Task.Run(() => EnsureHostRouteViaVpn(publicDnsNbo, dnsRouteIp));
                            _ipToProcess[publicDnsNbo] = dnsProc;

                            _natTable[(tuple2.Protocol, tuple2.LocalPort, publicDnsNbo)] = new NatEntry
                            {
                                OriginalSrcIp = origSrc,
                                PhysicalIfIdx = origIfIdx,
                                ProcessName = dnsProc,
                                LastSeen = DateTime.UtcNow,
                                IsDnsRedirect = true,
                                DnsOrigDstIp = dnsOrigDst
                            };

                            if (dnsRedirectLogCount < 5)
                            {
                                dnsRedirectLogCount++;
                                Logger.Info($"[DNS-REDIRECT] {dnsProc} → {_dnsRedirectIp}:53 (was: {tuple2.RemoteIp}:{tuple2.RemotePort})");
                            }
                            Interlocked.Increment(ref _redirectCount);

                            WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                            continue;
                        }
                    }

                    if (!isPrivate && !IsExcludedDestination(dstNbo))
                    {
                        // Check if destination is explicitly included (forced through VPN)
                        bool isIncluded = IsIncludedDestination(dstNbo);
                        bool shouldRoute = _fullRouteEnabled || isIncluded;
                        string? procName = null;

                        if (!shouldRoute)
                        {
                            if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                            {
                                connCache.Refresh();
                                lastRefresh = DateTime.UtcNow;
                            }

                            procName = connCache.GetProcessName(tuple2);
                            // Force refresh if not found — socket might be brand new
                            if (procName == null)
                            {
                                connCache.Refresh();
                                lastRefresh = DateTime.UtcNow;
                                procName = connCache.GetProcessName(tuple2);
                            }

                            // Check if source app is in target tunnel apps
                            if (IsExecutableTargeted(procName))
                            {
                                shouldRoute = true;
                            }
                        }

                        if (shouldRoute)
                        {
                            bool shouldInstallHostRoute = !_fullRouteEnabled || isIncluded ||
                                IsExecutableTargeted(procName);

                            if (shouldInstallHostRoute)
                            {
                                // Install route asynchronously (don't block packet loop)
                                var remoteIp = tuple2.RemoteIp;
                                _ = Task.Run(() => EnsureHostRouteViaVpn(dstNbo, remoteIp));
                            }

                            // Update IP→process mapping for sniff-loop attribution
                            if (isIncluded)
                            {
                                _ipToProcess[dstNbo] = "[INCLUDE]";
                            }
                            else if (!string.IsNullOrWhiteSpace(procName))
                            {
                                _ipToProcess[dstNbo] = procName;
                            }
                            else if (_fullRouteEnabled)
                            {
                                _ipToProcess[dstNbo] = "[FULL-ROUTE]";
                            }

                            // Save original source IP
                            var origSrc = new byte[4];
                            Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);
                            var origIfIdx = addr.IfIdx;

                            // Rewrite source IP to VPN
                            Buffer.BlockCopy(_vpnLocalIpBytes, 0, buffer, 12, 4);
                            addr.IfIdx = (uint)_vpnInterfaceIndex;
                            addr.SubIfIdx = 0;
                            ApplyGameModePacketTuning(buffer, readLen);
                            WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);

                            procName = procName ?? (isIncluded ? "[INCLUDE]" : "[FULL-ROUTE]");
                            _natTable[(tuple2.Protocol, tuple2.LocalPort, dstNbo)] = new NatEntry
                            {
                                OriginalSrcIp = origSrc,
                                PhysicalIfIdx = origIfIdx,
                                ProcessName = procName,
                                LastSeen = DateTime.UtcNow
                            };

                            if (rewriteLogCount < 10)
                            {
                                rewriteLogCount++;
                                Logger.Info($"[NET-OUT] Rewrite #{rewriteLogCount}: {procName} → {tuple2.RemoteIp}:{tuple2.RemotePort} (first packet, route installing)");
                            }

                            Interlocked.Increment(ref _statNetOutRewritten);
                        }
                    }
                }

                Interlocked.Increment(ref _statNetOutPassthrough);
                WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Error($"[NET-OUT] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures inbound packets addressed to the VPN local IP and performs
    /// reverse NAT: rewrites the destination back to the original physical IP
    /// so the application's socket (bound to physical IP) receives the reply.
    /// </summary>
    private void NetworkInboundLoop(CancellationToken ct)
    {
        try
        {
            string filter = $"inbound and ip and (tcp or udp) and ip.DstAddr == {_vpnLocalIp}";

            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 0, 0);
                _networkInHandle = h;
            }

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Warning($"[NET-IN] WinDivert open failed: {err} ({GetWinDivertErrorMessage(err)})");
                return;
            }

            Logger.Info($"[NET-IN] Reverse-NAT handle opened (filter='{filter}')");
            WinDivertNative.WinDivertSetParam(h, 0, 16384);
            WinDivertNative.WinDivertSetParam(h, 1, 2000);
            WinDivertNative.WinDivertSetParam(h, 2, 33554432);

            var buffer = new byte[65535];
            var addr = new WinDivertAddress();
            int natLogCount = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                if (readLen >= 20 && TryParseConnectionTuple(buffer, readLen, out var tuple))
                {
                    // For inbound: tuple.RemotePort = destination port = our client port.
                    // tuple.LocalIp = the remote server IP (source of the inbound packet).
                    // Key = (proto, clientPort, serverIp) — must match the outbound key
                    // (proto, srcPort, dstIp) since dstIp==serverIp and srcPort==clientPort.
                    uint serverNbo = BitConverter.ToUInt32(buffer, 12); // src IP of inbound = server
                    var key = (tuple.Protocol, tuple.RemotePort, serverNbo);
                    if (_natTable.TryGetValue(key, out var nat))
                    {
                        // Rewrite destination back to original physical IP
                        Buffer.BlockCopy(nat.OriginalSrcIp, 0, buffer, 16, 4);
                        addr.IfIdx = nat.PhysicalIfIdx;
                        addr.SubIfIdx = 0;

                        // For DNS-redirected queries: spoof source back to the original
                        // DNS server so Chrome/Windows accepts the response as coming from
                        // the configured resolver (e.g. 192.168.32.1), not 8.8.8.8.
                        if (nat.IsDnsRedirect && nat.DnsOrigDstIp != null)
                            Buffer.BlockCopy(nat.DnsOrigDstIp, 0, buffer, 12, 4);

                        WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);
                        nat.LastSeen = DateTime.UtcNow;

                        Interlocked.Increment(ref _statNetInRewritten);

                        if (natLogCount < 5)
                        {
                            natLogCount++;
                            Logger.Info($"[NET-IN] Reverse-NAT #{natLogCount}: from {tuple.LocalIp}:{tuple.LocalPort} → port {tuple.RemotePort} (target={nat.ProcessName})");
                        }
                    }
                }

                WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[NET-IN] Error: {ex.Message}");
        }
    }

    private void ApplyGameModePacketTuning(byte[] buffer, uint readLen)
    {
        if (!EnableGameMode || readLen < 20)
            return;

        // Low-latency DSCP (EF) on IPv4. ECN bits are preserved.
        buffer[1] = (byte)((buffer[1] & 0x03) | 0xB8);

        // Clamp TCP MSS on SYN packets to avoid fragmentation stalls on TUN paths.
        if (buffer[9] != 6)
            return;

        int ipHeaderLen = (buffer[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || readLen < ipHeaderLen + 20)
            return;

        int tcpOffset = ipHeaderLen;
        byte flags = buffer[tcpOffset + 13];
        bool syn = (flags & 0x02) != 0;
        if (!syn)
            return;

        int tcpHeaderLen = ((buffer[tcpOffset + 12] >> 4) & 0x0F) * 4;
        if (tcpHeaderLen <= 20 || readLen < tcpOffset + tcpHeaderLen)
            return;

        const ushort targetMss = 1360;
        int opt = tcpOffset + 20;
        int end = tcpOffset + tcpHeaderLen;
        while (opt < end)
        {
            byte kind = buffer[opt];
            if (kind == 0) break;
            if (kind == 1)
            {
                opt++;
                continue;
            }
            if (opt + 1 >= end) break;
            int len = buffer[opt + 1];
            if (len < 2 || opt + len > end) break;

            if (kind == 2 && len == 4)
            {
                ushort current = (ushort)((buffer[opt + 2] << 8) | buffer[opt + 3]);
                if (current > targetMss)
                {
                    buffer[opt + 2] = (byte)(targetMss >> 8);
                    buffer[opt + 3] = (byte)(targetMss & 0xFF);
                }
                return;
            }

            opt += len;
        }
    }
}
