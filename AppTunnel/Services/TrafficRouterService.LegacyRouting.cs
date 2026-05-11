using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    /// <summary>
    /// Main packet interception loop using WinDivert.
    /// Captures outbound packets, checks process ownership, redirects if needed.
    /// </summary>
    private void OutboundRoutingLoop(CancellationToken ct)
    {
        try
        {
            // CRITICAL: Exclude VPN server IP from interception to avoid breaking the tunnel
            // Also exclude loopback and local network traffic
            string filter = $"outbound and (tcp or udp) and ip.DstAddr != {_vpnServerIp} and ip.DstAddr != 127.0.0.1";
            
            Logger.Info($"WinDivert filter: {filter}");

            lock (_handleLock)
            {
                _outboundHandle = WinDivertNative.WinDivertOpen(
                    filter,
                    WinDivertLayer.Network,
                    0, 0);
            }

            if (_outboundHandle == IntPtr.Zero || _outboundHandle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"WinDivert outbound open failed: {error} ({GetWinDivertErrorMessage(error)})");
                return;
            }

            Logger.Info("WinDivert outbound handle opened successfully");

            // Tune queue params to prevent dropping/slowing other system traffic.
            // WINDIVERT_PARAM_QUEUE_LENGTH=0, QUEUE_TIME=1, QUEUE_SIZE=2
            WinDivertNative.WinDivertSetParam(_outboundHandle, 0, 16384);   // packets
            WinDivertNative.WinDivertSetParam(_outboundHandle, 1, 2000);    // ms
            WinDivertNative.WinDivertSetParam(_outboundHandle, 2, 33554432); // bytes (32MB)

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();

            var connectionCache = new ConnectionProcessCache();
            var lastCacheRefresh = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;

                bool success = WinDivertNative.WinDivertRecv(
                    _outboundHandle, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);

                if (!success)
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                Interlocked.Increment(ref _statTotalCaptured);

                if ((DateTime.UtcNow - lastCacheRefresh).TotalMilliseconds > 500)
                {
                    connectionCache.Refresh();
                    lastCacheRefresh = DateTime.UtcNow;
                }

                bool shouldRedirect = false;
                string? matchedProcess = null;

                if (TryParseConnectionTuple(buffer, readLen, out var tuple))
                {
                    var processName = connectionCache.GetProcessName(tuple);
                    if (IsExecutableTargeted(processName))
                    {
                        shouldRedirect = true;
                        matchedProcess = processName;

                        if (_trafficCounters.TryGetValue(processName, out var counter))
                        {
                            Interlocked.Add(ref counter.BytesSent, readLen);
                        }
                    }
                }

                // In PassthroughMode, do NOT rewrite — just reinject unchanged.
                if (shouldRedirect && !PassthroughMode && _vpnInterfaceIndex >= 0 && _vpnLocalIpBytes != null && readLen >= 20)
                {
                    // Save original source IP (before rewrite) so inbound can reverse NAT.
                    var origSrc = new byte[4];
                    Buffer.BlockCopy(buffer, 12, origSrc, 0, 4);
                    var origIfIdx = addrBuf.IfIdx;

                    // CRITICAL: ensure Windows actually routes this destination via VPN.
                    // Without a host route, the default route (Wi-Fi) wins and the
                    // kernel drops our reinjected packet because srcIP=192.168.19.10
                    // doesn't belong to the egress interface (strong host model).
                    uint dstIpNbo = BitConverter.ToUInt32(buffer, 16);
                    EnsureHostRouteViaVpn(dstIpNbo, tuple.RemoteIp);

                    _natTable[(tuple.Protocol, tuple.LocalPort, dstIpNbo)] = new NatEntry
                    {
                        OriginalSrcIp = origSrc,
                        PhysicalIfIdx = origIfIdx,
                        ProcessName = matchedProcess!,
                        LastSeen = DateTime.UtcNow
                    };

                    Buffer.BlockCopy(_vpnLocalIpBytes, 0, buffer, 12, 4);

                    addrBuf.IfIdx = (uint)_vpnInterfaceIndex;
                    addrBuf.SubIfIdx = 0;

                    WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addrBuf, 0);

                    Interlocked.Increment(ref _statRedirected);

                    if (_redirectCount < 10)
                    {
                        _redirectCount++;
                        Logger.Info($"OUT REDIRECT #{_redirectCount}: {matchedProcess} {tuple.LocalIp}:{tuple.LocalPort} → {tuple.RemoteIp}:{tuple.RemotePort} (proto={tuple.Protocol}) origIf={origIfIdx} vpnIf={_vpnInterfaceIndex} srcIP rewritten to {_vpnLocalIp}");
                    }
                }
                else
                {
                    Interlocked.Increment(ref _statPassthrough);
                }

                bool sent = WinDivertNative.WinDivertSend(
                    _outboundHandle, buffer, readLen, IntPtr.Zero, ref addrBuf);

                if (!sent)
                {
                    long f = Interlocked.Increment(ref _statSendFailed);
                    if (f <= 5)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Logger.Warning($"OUT WinDivertSend failed #{f}: err={err} ({GetWinDivertErrorMessage(err)}), ifIdx={addrBuf.IfIdx}, len={readLen}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning)
                Logger.Error($"Outbound routing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Inbound loop that does REVERSE NAT for target app traffic returning via VPN.
    /// Packets arrive with dst = VPN local IP; we rewrite dst back to the original
    /// physical IP so the app's socket (bound to physical IP) receives the reply.
    /// </summary>
    private void InboundTrackingLoop(CancellationToken ct)
    {
        try
        {
            // Only capture inbound packets addressed to the VPN local IP.
            // Everything else (normal inbound traffic on physical NIC) is not touched.
            string filter = $"inbound and (tcp or udp) and ip.DstAddr == {_vpnLocalIp}";

            lock (_handleLock)
            {
                _inboundHandle = WinDivertNative.WinDivertOpen(
                    filter,
                    WinDivertLayer.Network,
                    0,
                    0); // Full modify mode (not sniff)
            }

            if (_inboundHandle == IntPtr.Zero || _inboundHandle == new IntPtr(-1))
            {
                Logger.Warning($"WinDivert inbound open failed: {Marshal.GetLastWin32Error()}. Reverse NAT disabled.");
                return;
            }

            Logger.Info($"WinDivert inbound (reverse NAT) filter: {filter}");

            // Increase queue capacity to keep up with bursty traffic.
            WinDivertNative.WinDivertSetParam(_inboundHandle, 0, 16384);
            WinDivertNative.WinDivertSetParam(_inboundHandle, 1, 2000);
            WinDivertNative.WinDivertSetParam(_inboundHandle, 2, 33554432);

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool success = WinDivertNative.WinDivertRecv(
                    _inboundHandle, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);

                if (!success)
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                Interlocked.Increment(ref _statInboundCaptured);

                if (TryParseConnectionTuple(buffer, readLen, out var tuple))
                {
                    // For inbound packets:
                    //   tuple.LocalIp/LocalPort = srcIp/srcPort (the SERVER)
                    //   tuple.RemoteIp/RemotePort = dstIp/dstPort (US — the client)
                    // NAT table is keyed by (proto, clientPort) which was the OUTBOUND srcPort.
                    // That equals this packet's DESTINATION port = tuple.RemotePort.
                    uint srvNbo = BitConverter.ToUInt32(buffer, 12); // src IP of inbound = server
                    var key = (tuple.Protocol, tuple.RemotePort, srvNbo);
                    if (!PassthroughMode && _natTable.TryGetValue(key, out var nat))
                    {
                        // Rewrite dst IP (bytes 16..19) back to the original physical IP
                        Buffer.BlockCopy(nat.OriginalSrcIp, 0, buffer, 16, 4);
                        addrBuf.IfIdx = nat.PhysicalIfIdx;
                        addrBuf.SubIfIdx = 0;
                        WinDivertNative.WinDivertHelperCalcChecksums(buffer, readLen, ref addrBuf, 0);
                        nat.LastSeen = DateTime.UtcNow;
                        if (_trafficCounters.TryGetValue(nat.ProcessName, out var counter))
                            Interlocked.Add(ref counter.BytesReceived, readLen);
                        Interlocked.Increment(ref _statInboundNatMatched);

                        if (_inboundRewriteCount < 10)
                        {
                            _inboundRewriteCount++;
                            Logger.Info($"IN REVERSE NAT #{_inboundRewriteCount}: proto={tuple.Protocol} from {tuple.LocalIp}:{tuple.LocalPort} → dst port {tuple.RemotePort} rewritten to physical (target={nat.ProcessName}, iface={nat.PhysicalIfIdx})");
                        }
                    }
                    else if (_inboundRewriteCount < 10)
                    {
                        // Diagnostic: log unmatched inbound VPN packets
                        if (Interlocked.Read(ref _statInboundCaptured) <= 10)
                            Logger.Info($"IN UNMATCHED: proto={tuple.Protocol} {tuple.LocalIp}:{tuple.LocalPort} → {tuple.RemoteIp}:{tuple.RemotePort} (lookup key proto={tuple.Protocol},port={tuple.RemotePort})");
                    }
                }

                bool sent = WinDivertNative.WinDivertSend(_inboundHandle, buffer, readLen, IntPtr.Zero, ref addrBuf);
                if (!sent)
                {
                    long f = Interlocked.Increment(ref _statInboundSendFailed);
                    if (f <= 5)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Logger.Warning($"IN WinDivertSend failed #{f}: err={err} ({GetWinDivertErrorMessage(err)}), ifIdx={addrBuf.IfIdx}, len={readLen}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning)
                Logger.Warning($"Inbound NAT error: {ex.Message}");
        }
    }

    private static bool TryParseConnectionTuple(byte[] packet, uint length, out ConnectionTuple tuple)
    {
        tuple = default;
        if (length < 20) return false;

        byte version = (byte)((packet[0] >> 4) & 0xF);
        if (version != 4) return false;

        int headerLength = (packet[0] & 0xF) * 4;
        byte protocol = packet[9];

        var srcIp = new IPAddress(new ReadOnlySpan<byte>(packet, 12, 4));
        var dstIp = new IPAddress(new ReadOnlySpan<byte>(packet, 16, 4));

        if (protocol == 6 && length >= headerLength + 4) // TCP
        {
            ushort srcPort = (ushort)((packet[headerLength] << 8) | packet[headerLength + 1]);
            ushort dstPort = (ushort)((packet[headerLength + 2] << 8) | packet[headerLength + 3]);
            tuple = new ConnectionTuple(protocol, srcIp, srcPort, dstIp, dstPort);
            return true;
        }
        else if (protocol == 17 && length >= headerLength + 4) // UDP
        {
            ushort srcPort = (ushort)((packet[headerLength] << 8) | packet[headerLength + 1]);
            ushort dstPort = (ushort)((packet[headerLength + 2] << 8) | packet[headerLength + 3]);
            tuple = new ConnectionTuple(protocol, srcIp, srcPort, dstIp, dstPort);
            return true;
        }

        return false;
    }
}
