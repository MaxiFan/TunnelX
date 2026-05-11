using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    /// <summary>
    /// Intercepts outbound IPv6 packets at the NETWORK layer and drops those
    /// belonging to target applications. This forces Chrome/Edge/etc. to fall
    /// back to IPv4 (Happy Eyeballs), which then gets tunneled via host routes.
    /// The FLOW layer is SNIFF-only and cannot block connections, so we must
    /// block at the packet level instead.
    /// </summary>
    private void IPv6BlockLoop(CancellationToken ct)
    {
        try
        {
            // Capture outbound IPv6 TCP/UDP packets (not SNIFF — we need to be
            // able to drop them by not re-injecting).
            string filter = "outbound and ipv6 and (tcp or udp)";
            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 0, 0);
                _ipv6BlockHandle = h;
            }
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Warning($"[IPv6-BLOCK] WinDivert open failed: {err} ({GetWinDivertErrorMessage(err)}). IPv6 blocking disabled.");
                return;
            }
            Logger.Info($"[IPv6-BLOCK] Handle opened (filter='{filter}')");

            WinDivertNative.WinDivertSetParam(h, 0, 8192);
            WinDivertNative.WinDivertSetParam(h, 1, 2000);
            WinDivertNative.WinDivertSetParam(h, 2, 16777216);

            var buffer = new byte[65535];
            var addrBuf = new WinDivertAddress();
            var ipv6ConnCache = new ConnectionProcessCacheV6();
            var lastRefresh = DateTime.MinValue;
            int logCount = 0;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, buffer, (uint)buffer.Length, ref readLen, ref addrBuf);
                if (!ok)
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                // Refresh IPv6 connection table periodically
                if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 500)
                {
                    ipv6ConnCache.Refresh();
                    lastRefresh = DateTime.UtcNow;
                }

                // Try to find the owning process of this IPv6 packet
                bool shouldDrop = false;
                byte nextHeader = 0;
                int payloadOffset = 40;
                if (readLen >= 40) // minimum IPv6 header
                {
                    nextHeader = buffer[6];
                    if ((nextHeader == 6 || nextHeader == 17) && readLen >= payloadOffset + 4)
                    {
                        ushort srcPort = (ushort)((buffer[payloadOffset] << 8) | buffer[payloadOffset + 1]);
                        var procName = ipv6ConnCache.GetProcessName(srcPort, nextHeader);
                        if (procName != null)
                        {
                            var targetOwner = IsExecutableTargeted(procName) ? procName : null;
                            if (targetOwner != null)
                            {
                                shouldDrop = true;
                                Interlocked.Increment(ref _statFlowIPv6Blocked);
                                if (logCount < 3)
                                {
                                    logCount++;
                                    Logger.Info($"[IPv6-BLOCK] Dropped {procName} proto={nextHeader} srcPort={srcPort}");
                                }
                            }
                        }
                    }
                }

                if (!shouldDrop)
                {
                    // Re-inject non-target packets so they proceed normally
                    WinDivertNative.WinDivertSend(h, buffer, readLen, IntPtr.Zero, ref addrBuf);
                }
                else if (nextHeader == 6 && readLen >= payloadOffset + 20)
                {
                    // TCP IPv6 block: inject a TCP RST back to the application so
                    // Chrome's Happy Eyeballs immediately falls back to IPv4 instead
                    // of waiting for a multi-second TCP timeout.
                    InjectIPv6TcpRst(h, buffer, payloadOffset, ref addrBuf);
                }
                // UDP IPv6: silently dropped (no meaningful RST for UDP)
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Warning($"[IPv6-BLOCK] Loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Injects an inbound TCP RST+ACK so the application's TCP stack
    /// immediately signals ECONNREFUSED instead of waiting for SYN timeout.
    /// The RST is built from the dropped outbound SYN: src/dst and ports are
    /// swapped, seq=original_ack (0 for pure SYN), ack=original_seq+1.
    /// </summary>
    private static void InjectIPv6TcpRst(IntPtr h, byte[] pkt, int payloadOff, ref WinDivertAddress origAddr)
    {
        try
        {
            // IPv6(40) + TCP(20) = 60 bytes
            const int RstLen = 60;
            var rst = new byte[RstLen];

            // IPv6 header ─────────────────────────────────────────────
            rst[0] = 0x60;               // version=6, traffic class=0
            // bytes 1-3: traffic class(low) + flow label = 0
            rst[4] = 0;  rst[5] = 20;   // payload length = 20 (TCP header)
            rst[6] = 6;                  // next header = TCP
            rst[7] = 64;                 // hop limit
            // src = original dst (remote server)
            Buffer.BlockCopy(pkt, 24, rst, 8, 16);
            // dst = original src (local application)
            Buffer.BlockCopy(pkt, 8, rst, 24, 16);

            // TCP header at offset 40 ──────────────────────────────────
            // src port = original dst port
            rst[40] = pkt[payloadOff + 2];
            rst[41] = pkt[payloadOff + 3];
            // dst port = original src port
            rst[42] = pkt[payloadOff + 0];
            rst[43] = pkt[payloadOff + 1];

            // seq = original ack (if ACK flag was set) else 0
            byte flags = pkt[payloadOff + 13];
            if ((flags & 0x10) != 0)
            {
                rst[44] = pkt[payloadOff + 8];
                rst[45] = pkt[payloadOff + 9];
                rst[46] = pkt[payloadOff + 10];
                rst[47] = pkt[payloadOff + 11];
            }

            // ack = original seq + 1
            uint origSeq = ((uint)pkt[payloadOff + 4] << 24) | ((uint)pkt[payloadOff + 5] << 16)
                         | ((uint)pkt[payloadOff + 6] << 8)  |  pkt[payloadOff + 7];
            uint ack = origSeq + 1;
            rst[48] = (byte)(ack >> 24);
            rst[49] = (byte)(ack >> 16);
            rst[50] = (byte)(ack >> 8);
            rst[51] = (byte) ack;

            rst[52] = 0x50;  // data offset = 5 (no options)
            rst[53] = 0x14;  // RST | ACK
            // window, checksum, urgent = 0

            // Inject as INBOUND on the same interface
            var rstAddr = origAddr;
            rstAddr.LayerEventFlags &= ~(1UL << 17); // clear Outbound bit → inbound
            WinDivertNative.WinDivertHelperCalcChecksums(rst, RstLen, ref rstAddr, 0);
            WinDivertNative.WinDivertSend(h, rst, RstLen, IntPtr.Zero, ref rstAddr);
        }
        catch
        {
            // Best-effort; if RST injection fails the app will time out normally
        }
    }
}
