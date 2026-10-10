using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private IntPtr _networkOutHandle = IntPtr.Zero;
    private IntPtr _networkInHandle = IntPtr.Zero;
    private IntPtr _loopbackReturnHandle = IntPtr.Zero;
    private Task? _networkOutTask;
    private Task? _networkInTask;
    private Task? _loopbackReturnTask;
    private SplitAppForwarder? _splitForwarder;
    private int _splitRelayLogCount;
    private int _splitRouteStripLogCount;

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
            // Source 127.0.0.1 is the per-app forwarder. Capturing those
            // replies steals the SYN-ACK before the return path restores the
            // server address the selected app is waiting on.
            string filter = $"outbound and ip and (tcp or udp) " +
                $"and ip.SrcAddr != {_vpnLocalIp} " +
                $"and ip.SrcAddr != 127.0.0.1 " +
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
            int packetErrorLogs = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                try
                {
                if (readLen < 20 || _vpnLocalIpBytes == null)
                {
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                uint dstNbo = BitConverter.ToUInt32(buffer, 16);
                LearnDnsRuleFromOutboundPacket(buffer, readLen);

                // ── Fast path: destination already has a VPN host route ──
                if (_addedRoutes.ContainsKey(dstNbo))
                {
                    bool isIncluded = IsIncludedDestination(dstNbo);
                    string? packetProc = null;

                    if (TryParseConnectionTuple(buffer, readLen, out var probeTuple))
                    {
                        if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                        }

                        var packetPid = connCache.GetOwningPid(probeTuple);
                        packetProc = packetPid > 0 ? ResolveTargetOwner(packetPid) : null;
                        packetProc ??= connCache.GetProcessName(probeTuple);
                    }

                    _ipRefCount.TryGetValue(dstNbo, out var ipRefs);
                    var decision = SplitRouteFastPath.Decide(
                        fullRoute: _fullRouteEnabled,
                        probeIp: WindowsConnectivityGuard.IsProbeIpv4(dstNbo),
                        excluded: IsExcludedDestination(dstNbo),
                        included: isIncluded,
                        blockedProc: !_fullRouteEnabled && IsExecutableBlocked(packetProc),
                        processIsTarget: !string.IsNullOrWhiteSpace(packetProc) && IsExecutableTargeted(packetProc),
                        ipRefCount: ipRefs,
                        forwarderReady: _splitForwarder?.IsRunning == true);

                    // Outbound IfIdx is ignored by WinDivert, so a leftover VPN
                    // /32 would send this packet at the VPN adapter. Strip it
                    // with one IP-helper delete (not route.exe per packet)
                    // before reinjecting, or the strong-host model drops it.
                    if (decision.RemoveRouteNow && _addedRoutes.ContainsKey(dstNbo))
                        StripCapturingHostRoute(dstNbo);

                    if (decision.RelaySelectedApp &&
                        TryParseConnectionTuple(buffer, readLen, out var relayTuple) &&
                        TryRelaySelectedAppPacket(h, buffer, readLen, ref addr, relayTuple, packetProc))
                    {
                        continue;
                    }

                    if (!decision.SendViaVpn)
                    {
                        if (decision.ScheduleDelayedRemovalIfIdle)
                            ScheduleDelayedRouteRemovalIfIdle(dstNbo);

                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                        continue;
                    }

                    if (_pendingRouteRemoval.TryRemove(dstNbo, out var pendingKeep))
                    {
                        try { pendingKeep.Cancel(); } catch { }
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
                    // Fix: intercept port-53 from target apps aimed at private or auto-detected
                    // system DNS (exclude = direct for other apps) and redirect to the tunnel
                    // resolver. On the inbound path the source IP
                    // is spoofed back to the original server so the OS/Chrome accepts the
                    // response (it came from the "expected" address).
                    bool wireGuardDnsCandidate = _vpnServerIsUdpOnly && tuple2.RemotePort == 53;
                    bool classicDnsCandidate = tuple2.RemotePort == 53 && (isPrivate || IsExcludedDestination(dstNbo));
                    bool connectivityProbeQuery = IsSplitModeConnectivityProbeQuery(buffer, readLen);
                    bool connectivityProbeIp = !_fullRouteEnabled && WindowsConnectivityGuard.IsProbeIpv4(dstNbo);
                    if (_splitForwarder?.IsRunning == true && !connectivityProbeQuery && !connectivityProbeIp)
                    {
                        var slowProc = LookupPacketOwner(connCache, tuple2, buffer, readLen, ref lastRefresh);
                        if (tuple2.RemotePort == 53 && string.IsNullOrWhiteSpace(slowProc))
                            slowProc = ResolveDnsProcessOwner(connCache, tuple2);

                        bool slowTarget = !string.IsNullOrWhiteSpace(slowProc) && IsExecutableTargeted(slowProc);
                        if (slowTarget &&
                            !_fullRouteEnabled &&
                            !IsIncludedDestination(dstNbo) &&
                            !IsExecutableBlocked(slowProc) &&
                            TryRelaySelectedAppPacket(h, buffer, readLen, ref addr, tuple2, slowProc))
                        {
                            continue;
                        }
                    }

                    // Legacy DNS rewrite publishes a /32 for the public resolver.
                    // That route also captures unselected apps. The forwarder
                    // above sends only the selected app's DNS via the VPN.
                    if (_splitForwarder?.IsRunning != true &&
                        !connectivityProbeQuery && !connectivityProbeIp &&
                        EnableDnsRedirect && (wireGuardDnsCandidate || classicDnsCandidate))
                    {
                        if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                        {
                            connCache.Refresh();
                            lastRefresh = DateTime.UtcNow;
                        }
                        var dnsProc = ResolveDnsProcessOwner(connCache, tuple2);

                        if (!string.IsNullOrWhiteSpace(dnsProc) && IsExecutableTargeted(dnsProc))
                        {
                            LearnTargetDnsQueryFromOutboundPacket(buffer, readLen, dnsProc);
                            uint publicDnsNbo = _dnsRedirectIpNbo;

                            // Save original DNS server IP so we can spoof it back on the response
                            var dnsOrigDst = new byte[4];
                            Buffer.BlockCopy(buffer, 16, dnsOrigDst, 0, 4);

                            // Save physical src IP and interface for reverse NAT
                            var origSrc = new byte[4];
                            Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);
                            uint origIfIdx = addr.IfIdx;

                            // Rewrite destination to the public resolver and egress via VPN.
                            // WireGuard split-tunnel removes the VPN default route; without a
                            // /32 to the resolver and VPN source IP, DNS would leak to the ISP
                            // (blocked in Iran). Telegram works with hardcoded IPs; browsers need DNS.
                            Buffer.BlockCopy(_dnsRedirectIpBytes, 0, buffer, 16, 4);
                            var dnsRouteIp = _dnsRedirectIp;
                            Buffer.BlockCopy(_vpnLocalIpBytes!, 0, buffer, 12, 4);
                            addr.IfIdx = (uint)_vpnInterfaceIndex;
                            QueueHostRouteViaVpn(publicDnsNbo, dnsRouteIp);
                            _ipToProcess[publicDnsNbo] = dnsProc;

                            addr.SubIfIdx = 0;
                            ApplyGameModePacketTuning(buffer, readLen);
                            WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);

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

                    if (!connectivityProbeIp && !isPrivate && !IsExcludedDestination(dstNbo))
                    {
                        // Check if destination is explicitly included (forced through VPN)
                        bool isIncluded = IsIncludedDestination(dstNbo);
                        bool shouldRoute = _fullRouteEnabled || isIncluded;
                        string? procName = null;

                        if (!shouldRoute)
                        {
                            procName = LookupPacketOwner(connCache, tuple2, buffer, readLen, ref lastRefresh);

                            // Check if source app is in target tunnel apps
                            if (!string.IsNullOrWhiteSpace(procName) && IsExecutableTargeted(procName))
                            {
                                shouldRoute = true;
                            }
                        }

                        if (shouldRoute &&
                            _splitForwarder?.IsRunning == true &&
                            !_fullRouteEnabled &&
                            !isIncluded)
                        {
                            if (TryRelaySelectedAppPacket(h, buffer, readLen, ref addr, tuple2, procName))
                                continue;

                            // A failed relay must not fall back to a /32.
                            // That route would take unselected apps offline.
                            shouldRoute = false;
                        }

                        if (shouldRoute)
                        {
                            if (_vpnServerIsUdpOnly &&
                                tuple2.Protocol == 17 &&
                                tuple2.RemotePort == 443 &&
                                !string.IsNullOrWhiteSpace(procName) &&
                                ShouldForceTcpFallbackForWireGuard(procName))
                            {
                                var dropped = Interlocked.Increment(ref _statWgQuicDropped);
                                if (dropped <= 8)
                                    Logger.Info($"[WG-QUIC-DROP] {procName} UDP/443 → {tuple2.RemoteIp}:443 dropped to force TCP fallback");
                                continue;
                            }

                            // Per-app /32s are only the legacy path (forwarder down).
                            // Full-route already has a default route; include rules
                            // still need a /32 while split mode is selected.
                            bool shouldInstallHostRoute = isIncluded ||
                                (_splitForwarder?.IsRunning != true && !_fullRouteEnabled) ||
                                (_fullRouteEnabled && IsExecutableTargeted(procName));

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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad packet must not kill the loop. The handle stays
                    // open and would blackhole every outbound TCP/UDP packet,
                    // which Windows reports as Wi-Fi with no internet.
                    if (packetErrorLogs < 5)
                    {
                        packetErrorLogs++;
                        Logger.Warning($"[NET-OUT] Packet handling error; capture loop kept alive: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Error($"[NET-OUT] Error: {ex.Message}");
        }
        finally
        {
            FailOpenCaptureHandle(ref _networkOutHandle, "NET-OUT");
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
            string filter = $"inbound and ip and ((tcp or udp) and ip.DstAddr == {_vpnLocalIp} or udp.SrcPort == 53)";

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
            int packetErrorLogs = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                try
                {
                if (readLen >= 20)
                    ApplyDnsRuleFromInboundPacket(buffer, readLen);

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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // This handle also diverts every inbound DNS response.
                    // If the loop dies, Windows loses DNS and marks Wi-Fi
                    // "Connected, no internet" until the VPN is disconnected.
                    if (packetErrorLogs < 5)
                    {
                        packetErrorLogs++;
                        Logger.Warning($"[NET-IN] Packet handling error; capture loop kept alive: {ex.Message}");
                    }
                    try { WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[NET-IN] Error: {ex.Message}");
        }
        finally
        {
            FailOpenCaptureHandle(ref _networkInHandle, "NET-IN");
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

    private static bool IsSafeIpv4TransportPacket(byte[] packet, uint length)
    {
        if (length < 20 || packet.Length < 20)
            return false;

        int packetLen = (int)Math.Min(length, (uint)packet.Length);
        if ((packet[0] >> 4) != 4)
            return false;

        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || packetLen < ipHeaderLen)
            return false;

        int totalLen = (packet[2] << 8) | packet[3];
        if (totalLen < ipHeaderLen || totalLen > packetLen)
            return false;

        ushort frag = (ushort)((packet[6] << 8) | packet[7]);
        if ((frag & 0x3FFF) != 0)
            return false;

        byte protocol = packet[9];
        if (protocol == 6)
        {
            if (totalLen < ipHeaderLen + 20)
                return false;

            int tcpOffset = ipHeaderLen;
            int tcpHeaderLen = ((packet[tcpOffset + 12] >> 4) & 0x0F) * 4;
            return tcpHeaderLen >= 20 && totalLen >= ipHeaderLen + tcpHeaderLen;
        }

        if (protocol == 17)
        {
            if (totalLen < ipHeaderLen + 8)
                return false;

            int udpLen = (packet[ipHeaderLen + 4] << 8) | packet[ipHeaderLen + 5];
            return udpLen >= 8 && udpLen <= totalLen - ipHeaderLen;
        }

        return false;
    }

    /// <summary>
    /// WireGuard uses a point-to-point adapter: after a host route is installed,
    /// packets can leave directly on the VPN ifIdx while still carrying the
    /// physical source IP. The generic NET-OUT loop may never see them. Rewrite
    /// source IP on the VPN interface before WireGuard drops them (strong host).
    /// </summary>
    private void WireGuardVpnOutboundLoop(CancellationToken ct)
    {
        try
        {
            // Observe all packets already leaving on the WireGuard interface. Some
            // Windows/WireGuard paths need this pass-through handle to see packets
            // after NET-OUT reinjects them onto the adapter. Healthy packets take a
            // fast path below; only wrong-source packets do owner/NAT work.
            string filter = $"outbound and ip and ifIdx == {_vpnInterfaceIndex} and (tcp or udp) " +
                            $"and ip.DstAddr != {_vpnServerIp} and ip.DstAddr != 127.0.0.1";

            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 0, 0);
                _wgVpnOutHandle = h;
            }

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Warning($"[WG-OUT] WinDivert open failed: {err} ({GetWinDivertErrorMessage(err)})");
                return;
            }

            Logger.Info($"[WG-OUT] VPN-interface rewrite handle opened (filter='{filter}')");

            WinDivertNative.WinDivertSetParam(h, 0, 16384);
            WinDivertNative.WinDivertSetParam(h, 1, 2000);
            WinDivertNative.WinDivertSetParam(h, 2, 33554432);

            var buffer = new byte[65535];
            var addr = new WinDivertAddress();
            var connCache = new ConnectionProcessCache();
            var lastRefresh = DateTime.MinValue;
            int logCount = 0;
            int wgDnsRedirectLogCount = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                Interlocked.Increment(ref _statWgVpnOutSeen);

                if (readLen < 20 || _vpnLocalIpBytes is not { Length: 4 } vpnLocalIpBytes)
                {
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                bool srcIsVpn = buffer[12] == vpnLocalIpBytes[0] &&
                                buffer[13] == vpnLocalIpBytes[1] &&
                                buffer[14] == vpnLocalIpBytes[2] &&
                                buffer[15] == vpnLocalIpBytes[3];

                if (!TryParseConnectionTuple(buffer, readLen, out var tuple))
                {
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                uint dstNbo = BitConverter.ToUInt32(buffer, 16);

                if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
                {
                    connCache.Refresh();
                    lastRefresh = DateTime.UtcNow;
                }

                var pid = connCache.GetOwningPid(tuple);
                string? targetOwner = pid > 0 ? ResolveTargetOwner(pid) : null;
                if (targetOwner == null)
                {
                    var procHint = connCache.GetProcessName(tuple);
                    if (!string.IsNullOrWhiteSpace(procHint) && IsExecutableTargeted(procHint))
                        targetOwner = procHint;
                }
                if (targetOwner == null &&
                    _flowOwnerByTuple.TryGetValue((tuple.Protocol, tuple.LocalPort, dstNbo), out var flowOwner) &&
                    IsExecutableTargeted(flowOwner))
                {
                    targetOwner = flowOwner;
                }
                if (targetOwner == null &&
                    _ipToProcess.TryGetValue(dstNbo, out var ipOwner) &&
                    IsExecutableTargeted(ipOwner))
                {
                    targetOwner = ipOwner;
                }

                // DNS on WG ifIdx never hits NET-OUT. Packets may use physical OR VPN
                // source on this ifIdx (strong-host); do not require srcIsVpn.
                if (EnableDnsRedirect &&
                    _vpnServerIsUdpOnly &&
                    tuple.Protocol == 17 &&
                    tuple.RemotePort == 53)
                {
                    byte[] dstBytes = BitConverter.GetBytes(dstNbo);
                    var dnsProc = targetOwner ?? connCache.GetProcessName(tuple);
                    if (string.IsNullOrWhiteSpace(dnsProc) &&
                        _ipToProcess.TryGetValue(dstNbo, out var routeOwner))
                        dnsProc = routeOwner;

                    // Exclude = outside tunnel for non-target apps. Include/user exclude
                    // still applies to normal traffic; target-app DNS is redirected below.
                    bool isTargetDns = IsExecutableTargeted(dnsProc) ||
                                         (!string.IsNullOrWhiteSpace(targetOwner) && IsExecutableTargeted(targetOwner));
                    if (!isTargetDns || IsSplitModeConnectivityProbeQuery(buffer, readLen))
                    {
                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                        continue;
                    }

                    if (dstBytes[0] >= 224 || dstNbo == _dnsRedirectIpNbo)
                    {
                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(dnsProc))
                        dnsProc = "dns";

                    {
                        LearnTargetDnsQueryFromOutboundPacket(buffer, readLen, dnsProc);
                        var dnsOrigDst = new byte[4];
                        Buffer.BlockCopy(buffer, 16, dnsOrigDst, 0, 4);
                        var wgDnsOrigSrc = new byte[4];
                        Buffer.BlockCopy(buffer, 12, wgDnsOrigSrc, 0, 4);
                        uint origIfIdx = addr.IfIdx;
                        uint dnsReturnIfIdx = srcIsVpn || _physicalInterfaceIndex <= 0
                            ? origIfIdx
                            : (uint)_physicalInterfaceIndex;

                        Buffer.BlockCopy(_dnsRedirectIpBytes, 0, buffer, 16, 4);
                        Buffer.BlockCopy(vpnLocalIpBytes, 0, buffer, 12, 4);
                        addr.IfIdx = (uint)_vpnInterfaceIndex;
                        addr.SubIfIdx = 0;
                        QueueHostRouteViaVpn(_dnsRedirectIpNbo, _dnsRedirectIp);
                        if (!string.IsNullOrWhiteSpace(dnsProc))
                            _ipToProcess[_dnsRedirectIpNbo] = dnsProc;

                        ApplyGameModePacketTuning(buffer, readLen);
                        WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);
                        _natTable[(tuple.Protocol, tuple.LocalPort, _dnsRedirectIpNbo)] = new NatEntry
                        {
                            OriginalSrcIp = wgDnsOrigSrc,
                            PhysicalIfIdx = dnsReturnIfIdx,
                            ProcessName = dnsProc ?? "dns",
                            LastSeen = DateTime.UtcNow,
                            IsDnsRedirect = true,
                            DnsOrigDstIp = dnsOrigDst
                        };

                        Interlocked.Increment(ref _redirectCount);
                        Interlocked.Increment(ref _statWgDnsRedirect);
                        if (wgDnsRedirectLogCount < 5)
                        {
                            wgDnsRedirectLogCount++;
                            Logger.Info($"[WG-DNS-REDIRECT] {dnsProc ?? "dns"} → {_dnsRedirectIp}:53 (was: {tuple.RemoteIp}:53)");
                        }

                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                        continue;
                    }
                }

                bool shouldRoute = _addedRoutes.ContainsKey(dstNbo) || _fullRouteEnabled ||
                                   IsIncludedDestination(dstNbo) || targetOwner != null;
                string? procName = targetOwner;

                if (_vpnServerIsUdpOnly &&
                    tuple.Protocol == 17 &&
                    tuple.RemotePort == 443 &&
                    !string.IsNullOrWhiteSpace(targetOwner) &&
                    ShouldForceTcpFallbackForWireGuard(targetOwner))
                {
                    var dropped = Interlocked.Increment(ref _statWgQuicDropped);
                    if (dropped <= 8)
                        Logger.Info($"[WG-QUIC-DROP] {targetOwner} UDP/443 → {tuple.RemoteIp}:443 dropped to force TCP fallback");
                    continue;
                }

                if (srcIsVpn)
                {
                    // This is the normal successful path after NET-OUT or Windows
                    // routing selected the WireGuard source IP. Do not rewrite it
                    // again, but keep route ownership fresh and allow QUIC/DNS policy
                    // above to run first.
                    if (!IsExcludedDestination(dstNbo) && shouldRoute)
                    {
                        var routeOwner = targetOwner ?? procName;
                        if (!string.IsNullOrWhiteSpace(routeOwner))
                            _ipToProcess[dstNbo] = routeOwner;
                        if (!_addedRoutes.ContainsKey(dstNbo))
                            QueueHostRouteViaVpn(dstNbo, tuple.RemoteIp);
                    }

                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                if (!shouldRoute || IsExcludedDestination(dstNbo))
                {
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    continue;
                }

                var origSrc = new byte[4];
                Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);

                Buffer.BlockCopy(vpnLocalIpBytes, 0, buffer, 12, 4);
                addr.IfIdx = (uint)_vpnInterfaceIndex;
                addr.SubIfIdx = 0;
                ApplyGameModePacketTuning(buffer, readLen);
                WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);

                var ownerName = procName ??
                                (_ipToProcess.TryGetValue(dstNbo, out var mappedOwner) ? mappedOwner : "unknown");
                var returnIfIdx = _physicalInterfaceIndex > 0
                    ? (uint)_physicalInterfaceIndex
                    : addr.IfIdx;
                _natTable[(tuple.Protocol, tuple.LocalPort, dstNbo)] = new NatEntry
                {
                    OriginalSrcIp = origSrc,
                    PhysicalIfIdx = returnIfIdx,
                    ProcessName = ownerName,
                    LastSeen = DateTime.UtcNow
                };

                Interlocked.Increment(ref _statWgVpnOutRewritten);
                if (logCount < 5)
                {
                    logCount++;
                    Logger.Info($"[WG-OUT] Rewrite #{logCount}: {ownerName} → {tuple.RemoteIp}:{tuple.RemotePort} (returnIf={returnIfIdx})");
                }

                if (!WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (Interlocked.Increment(ref _statNetOutSendFailed) <= 5)
                        Logger.Warning($"[WG-OUT] send failed: err={err} dst={tuple.RemoteIp}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[WG-OUT] Loop error: {ex.Message}");
        }
    }

    private void StripCapturingHostRoute(uint dstIpNbo)
    {
        if (_splitRouteStripLogCount < 8)
        {
            _splitRouteStripLogCount++;
            Logger.Info($"[SPLIT-DIRECT] Removing VPN /32 {new IPAddress(BitConverter.GetBytes(dstIpNbo))} so unselected apps stay on the direct path");
        }

        TryRemoveHostRoute(dstIpNbo);
    }

    /// <summary>
    /// The connection table is refreshed every 300ms. A TCP SYN is often
    /// diverted before that row exists, so the handshake leaves on the
    /// physical NIC and only later ACK/data segments are redirected.
    /// A listener never accepts those segments. Refresh once on a SYN miss.
    /// </summary>
    private string? LookupPacketOwner(
        ConnectionProcessCache connCache,
        ConnectionTuple tuple,
        byte[] buffer,
        uint readLen,
        ref DateTime lastRefresh)
    {
        int ihl = (buffer[0] & 0x0F) * 4;
        bool syn = tuple.Protocol == 6 && readLen >= ihl + 14 && (buffer[ihl + 13] & 2) != 0;

        if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 300)
        {
            connCache.Refresh();
            lastRefresh = DateTime.UtcNow;
        }

        int pid = connCache.GetOwningPid(tuple);
        if (pid <= 0 && syn)
        {
            connCache.Refresh();
            lastRefresh = DateTime.UtcNow;
            pid = connCache.GetOwningPid(tuple);
        }

        var name = pid > 0 ? ResolveTargetOwner(pid) : null;
        name ??= connCache.GetProcessName(tuple);
        return name;
    }

    /// <summary>
    /// Sends one selected-app packet to the local forwarder. The forwarder
    /// dials the real destination from the VPN interface. No /32 is installed,
    /// so other processes keep the physical default route.
    /// </summary>
    private bool TryRelaySelectedAppPacket(
        IntPtr handle,
        byte[] buffer,
        uint readLen,
        ref WinDivertAddress addr,
        ConnectionTuple tuple,
        string? procName)
    {
        var forwarder = _splitForwarder;
        if (forwarder == null || !forwarder.IsRunning || _vpnLocalIpBytes == null)
            return false;

        if (tuple.RemotePort == 53 && IsSplitModeConnectivityProbeQuery(buffer, readLen))
            return false;

        uint destNbo = BitConverter.ToUInt32(buffer, 16);
        byte b0 = buffer[16];
        byte b1 = buffer[17];
        bool isPrivate = b0 == 0 || b0 == 127 || b0 >= 224 ||
            b0 == 10 || (b0 == 172 && b1 >= 16 && b1 <= 31) ||
            (b0 == 192 && b1 == 168) || (b0 == 169 && b1 == 254);
        if (isPrivate && !(EnableDnsRedirect && tuple.RemotePort == 53))
            return false;

        if (IsExcludedDestination(destNbo) && tuple.RemotePort != 53)
            return false;

        if (_vpnServerIsUdpOnly &&
            tuple.Protocol == 17 &&
            tuple.RemotePort == 443 &&
            !string.IsNullOrWhiteSpace(procName) &&
            ShouldForceTcpFallbackForWireGuard(procName))
        {
            var dropped = Interlocked.Increment(ref _statWgQuicDropped);
            if (dropped <= 8)
                Logger.Info($"[WG-QUIC-DROP] {procName} UDP/443 → {tuple.RemoteIp}:443 dropped to force TCP fallback");
            return true;
        }

        uint clientNbo = BitConverter.ToUInt32(buffer, 12);
        uint forwardNbo = destNbo;
        ushort forwardPort = tuple.RemotePort;
        if (EnableDnsRedirect && tuple.RemotePort == 53)
        {
            if (!string.IsNullOrWhiteSpace(procName))
                LearnTargetDnsQueryFromOutboundPacket(buffer, readLen, procName);
            forwardNbo = _dnsRedirectIpNbo;
            forwardPort = 53;
        }

        ushort relayPort = tuple.Protocol == 6 ? forwarder.TcpPort : forwarder.UdpPort;
        if (relayPort == 0)
            return false;

        forwarder.Register(
            tuple.Protocol,
            clientNbo,
            tuple.LocalPort,
            destNbo,
            tuple.RemotePort,
            forwardNbo,
            forwardPort,
            addr.IfIdx,
            procName);

        if (!Ipv4PacketRewrite.TryRewriteDestination(buffer, (int)readLen, Ipv4PacketRewrite.LoopbackBytes, relayPort))
            return false;
        // A SYN to 127.0.0.1 whose source is the LAN address is dropped by the
        // stack: WinDivertSend succeeds, the same ports are retransmitted, and
        // the listener never accepts. Both ends must be loopback.
        if (!Ipv4PacketRewrite.TryRewriteSource(buffer, (int)readLen, Ipv4PacketRewrite.LoopbackBytes, tuple.LocalPort))
            return false;

        // Checksum-valid bits still describe the pre-rewrite packet, so the
        // helper would skip them and the stack would drop the inject. Loopback
        // + impostor are what actually deliver a rewritten external packet to
        // the socket listening on 127.0.0.1.
        SplitLoopbackInject.PrepareOutboundRedirect(ref addr);
        WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);
        addr.Outbound = true;
        addr.Loopback = true;
        addr.Impostor = true;
        if (_splitRelayLogCount < 8)
        {
            _splitRelayLogCount++;
            var forwardIp = new IPAddress(BitConverter.GetBytes(forwardNbo));
            var via = forwarder.UsesMixedProxy ? "mixed proxy" : $"VPN if {_vpnInterfaceIndex}";
            Logger.Info($"[SPLIT-DIRECT] {procName ?? "app"} {tuple.RemoteIp}:{tuple.RemotePort} → {via} via 127.0.0.1:{relayPort} (dial {forwardIp}:{forwardPort}, loopback inject, no /32)");
        }

        Interlocked.Increment(ref _statNetOutRewritten);
        Interlocked.Increment(ref _redirectCount);
        if (!WinDivertNative.WinDivertSend(handle, buffer, readLen, IntPtr.Zero, ref addr))
        {
            long failed = Interlocked.Increment(ref _statNetOutSendFailed);
            if (failed <= 5)
                Logger.Warning($"[SPLIT-DIRECT] Relay inject failed: {Marshal.GetLastWin32Error()}");
        }

        return true;
    }

    /// <summary>
    /// Selected-app sockets still think they are talking to the real server.
    /// The forwarder's loopback replies are rewritten so the source is that server.
    /// </summary>
    private void SplitLoopbackReturnLoop(CancellationToken ct)
    {
        var forwarder = _splitForwarder;
        if (forwarder == null || !forwarder.IsRunning)
            return;

        try
        {
            // The listener's SYN-ACK leaves as outbound from 127.0.0.1:<forwarder>
            // before any inbound reflection. An inbound-only filter never sees
            // it, so the app retransmits and Accept never returns.
            string filter =
                $"(inbound or outbound) and not impostor and ip.SrcAddr == 127.0.0.1 and (tcp.SrcPort == {forwarder.TcpPort} or udp.SrcPort == {forwarder.UdpPort})";
            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, -50, 0);
                _loopbackReturnHandle = h;
            }

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Warning($"[SPLIT-DIRECT] Return path open failed: {err} ({GetWinDivertErrorMessage(err)})");
                return;
            }

            Logger.Info($"[SPLIT-DIRECT] Return path opened (filter='{filter}')");
            var buffer = new byte[65535];
            var addr = new WinDivertAddress();
            int packetErrorLogs = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                if (!WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addr))
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                try
                {
                    if (readLen >= 20 && TryParseConnectionTuple(buffer, readLen, out var tuple))
                    {
                        uint clientNbo = BitConverter.ToUInt32(buffer, 16);
                        if (forwarder.Sessions.TryGet(tuple.Protocol, clientNbo, tuple.RemotePort, out var session))
                        {
                            if (Ipv4PacketRewrite.TryRestoreSelectedAppReply(
                                    buffer,
                                    (int)readLen,
                                    session.OriginalClientIp,
                                    session.OriginalDestIp,
                                    session.OriginalDestPort))
                            {
                                SplitLoopbackInject.PrepareReturn(ref addr, session.OriginalIfIdx);
                                WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addr, 0);
                                addr.Impostor = true;
                                addr.Outbound = false;
                                if (session.OriginalIfIdx > 0)
                                {
                                    addr.Loopback = false;
                                    addr.IfIdx = session.OriginalIfIdx;
                                }
                                else
                                {
                                    addr.Loopback = true;
                                }

                                var restored = Interlocked.Increment(ref _statSplitReturnRewritten);
                                if (restored <= 8)
                                {
                                    var client = new IPAddress(BitConverter.GetBytes(session.OriginalClientIp));
                                    var server = new IPAddress(BitConverter.GetBytes(session.OriginalDestIp));
                                    Logger.Info($"[SPLIT-FWD] return proto={tuple.Protocol} restored {server}:{session.OriginalDestPort} -> {client}:{tuple.RemotePort} if {session.OriginalIfIdx}");
                                }
                            }
                        }
                        else
                        {
                            var missed = Interlocked.Increment(ref _statSplitReturnMiss);
                            if (missed <= 8)
                                Logger.Warning($"[SPLIT-FWD] return proto={tuple.Protocol} {tuple.LocalIp}:{tuple.LocalPort} -> {tuple.RemoteIp}:{tuple.RemotePort} has no session");
                        }
                    }

                    addr.Impostor = true;
                    if (!WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (Interlocked.Increment(ref _statNetOutSendFailed) <= 5)
                            Logger.Warning($"[SPLIT-FWD] return inject failed: {err} ({GetWinDivertErrorMessage(err)})");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (packetErrorLogs < 5)
                    {
                        packetErrorLogs++;
                        Logger.Warning($"[SPLIT-DIRECT] Return path error: {ex.Message}");
                    }
                    try
                    {
                        addr.Impostor = true;
                        WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addr);
                    }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[SPLIT-DIRECT] Return path stopped: {ex.Message}");
        }
        finally
        {
            FailOpenCaptureHandle(ref _loopbackReturnHandle, "SPLIT-DIRECT");
        }
    }

    private static bool ShouldForceTcpFallbackForWireGuard(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;

        return executableName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("cursor.exe", StringComparison.OrdinalIgnoreCase) ||
               executableName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
    }
}
