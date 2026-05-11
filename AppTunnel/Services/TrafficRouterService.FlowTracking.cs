using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    // ======== Flow-layer tracking (primary mechanism) ========

    private readonly ConcurrentDictionary<int, string> _pidNameCache = new();
    // Per-destination-IP → target-app mapping, filled when a flow for a target
    // app is observed. Used by the VPN egress/ingress sniff loops to attribute
    // traffic bytes to the right application.
    private readonly ConcurrentDictionary<uint, string> _ipToProcess = new();
    // Per-flow ownership map keyed by (protocol, local port, remote IP).
    // This avoids attribution collisions when multiple apps connect to the
    // same destination IP at the same time.
    private readonly ConcurrentDictionary<(byte proto, ushort localPort, uint remoteIp), string> _flowOwnerByTuple = new();
    // Reference count per destination IP (target-app flows only). When it
    // drops to zero, the corresponding /32 host route is removed so that
    // other (non-target) applications no longer inherit the VPN path for
    // that IP. This is essential for true per-app isolation.
    private readonly ConcurrentDictionary<uint, int> _ipRefCount = new();
    // Pending delayed route removals: when refcount drops to 0 we schedule
    // removal after a grace period so short-lived target-app reconnects
    // don't lose the route, but non-target apps stop piggy-backing eventually.
    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _pendingRouteRemoval = new();
    // pid → parent pid cache (walked up the tree to detect child processes of
    // target apps, e.g. msedgewebview2.exe hosted inside WhatsApp.Root.exe).
    private readonly ConcurrentDictionary<int, int> _pidParentCache = new();
    // pid → resolved target-app name (if any ancestor matches a target),
    // or "" (empty) if no ancestor matches. Cached to avoid re-walking.
    private readonly ConcurrentDictionary<int, string> _pidTargetOwnerCache = new();
    private long _statFlowEstablished;
    private long _statFlowTargetMatched;
    private long _statFlowDeleted;
    private long _statFlowExcluded;
    private long _statFlowIPv6Blocked;
    private int _flowLogCount = 0;
    private int _flowMatchLogCount = 0;
    // Track which IPs we already logged, to suppress duplicate FLOW-MATCH / FLOW-EXCLUDED lines
    private readonly ConcurrentDictionary<uint, bool> _loggedMatchIps = new();
    private readonly ConcurrentDictionary<uint, bool> _loggedExcludedIps = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Returns the parent PID of a given process, or 0 if it cannot be
    /// determined (process exited, access denied, etc.). Cached per pid.
    /// </summary>
    private int GetParentPid(int pid)
    {
        if (pid <= 0) return 0;
        if (_pidParentCache.TryGetValue(pid, out var cached)) return cached;
        int parent = 0;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
                int status = NtQueryInformationProcess(h, 0, ref pbi, size, out _);
                if (status == 0)
                    parent = pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            finally { CloseHandle(h); }
        }
        _pidParentCache[pid] = parent;
        return parent;
    }

    /// <summary>
    /// Walks up the parent-process chain (up to 8 hops) looking for any
    /// ancestor whose executable name is in <see cref="_targetExecutables"/>.
    /// If found, returns that ancestor's name (so the flow is attributed to
    /// e.g. "WhatsApp.Root.exe" even when the actual socket is owned by
    /// "msedgewebview2.exe"). Otherwise returns null.
    /// </summary>
    private string? ResolveTargetOwner(int pid)
    {
        if (pid <= 0) return null;
        if (_pidTargetOwnerCache.TryGetValue(pid, out var cachedOwner))
        {
            if (string.IsNullOrEmpty(cachedOwner))
                return null;

            // Policy may have changed after this PID was cached.
            if (IsExecutableTargeted(cachedOwner))
                return cachedOwner;

            _pidTargetOwnerCache.TryRemove(pid, out _);
        }

        int current = pid;
        for (int hop = 0; hop < 8 && current > 4; hop++)
        {
            var name = GetProcessNameByPid(current);
            if (IsExecutableTargeted(name))
            {
                _pidTargetOwnerCache[pid] = name;
                return name;
            }
            int parent = GetParentPid(current);
            if (parent == 0 || parent == current) break;
            current = parent;
        }
        _pidTargetOwnerCache[pid] = string.Empty;
        return null;
    }

    /// <summary>
    /// Resolves a process ID to its executable file name (e.g. "chrome.exe"),
    /// caching the result. Returns null if the PID has already exited or is inaccessible.
    /// </summary>
    private string? GetProcessNameByPid(int pid)
    {
        if (pid <= 0) return null;
        if (_pidNameCache.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var p = Process.GetProcessById(pid);
            // Always prefer MainModule filename (e.g. "chrome.exe") over ProcessName ("chrome")
            string name;
            try
            {
                name = p.MainModule?.ModuleName ?? (p.ProcessName + ".exe");
            }
            catch
            {
                name = p.ProcessName + ".exe";
            }
            _pidNameCache[pid] = name;
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Listens on the WinDivert FLOW layer for connection-established events and,
    /// for every flow that belongs to a target application, installs a /32 host
    /// route to the remote endpoint via the VPN interface. After that, Windows
    /// itself routes the connection through the VPN — no per-packet rewriting
    /// needed.
    /// </summary>
    private void FlowTrackingLoop(CancellationToken ct)
    {
        try
        {
            // FLOW layer requires SNIFF (0x1) + RECV_ONLY (0x4) = 0x5
            // FLOW layer is observe-only; it cannot block connections.
            const ulong FLAG_SNIFF_RECV_ONLY = 0x5;
            // Flow-layer filter fields are different from network layer:
            //   - Use 'remoteAddr' / 'localAddr' / 'remotePort' / 'protocol'
            //   - 'ip.DstAddr', 'tcp', 'udp' are NOT valid here
            //   - protocol 6=TCP, 17=UDP
            string filter =
                $"not loopback and (protocol == 6 or protocol == 17) and remoteAddr != {_vpnServerIp}";

            IntPtr h;
            lock (_handleLock)
            {
                h = WinDivertNative.WinDivertOpen(
                    filter, WinDivertLayer.Flow, 0, FLAG_SNIFF_RECV_ONLY);
                _outboundHandle = h; // reuse existing field for cleanup
            }

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Error($"WinDivert FLOW open failed: {err} ({GetWinDivertErrorMessage(err)})");
                return;
            }

            Logger.Info($"WinDivert FLOW handle opened (filter='{filter}')");

            // Raise queue capacity — flow events are small but can be bursty.
            WinDivertNative.WinDivertSetParam(h, 0, 16384);
            WinDivertNative.WinDivertSetParam(h, 1, 2000);
            WinDivertNative.WinDivertSetParam(h, 2, 33554432);

            // Flow layer does NOT return packet data; we pass an empty buffer.
            var emptyBuf = Array.Empty<byte>();
            var addr = new WinDivertAddress();
            var lastCacheClear = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                uint readLen = 0;
                bool ok = WinDivertNative.WinDivertRecv(h, emptyBuf, 0, ref readLen, ref addr);
                if (!ok)
                {
                    if (ct.IsCancellationRequested || !_isRunning) break;
                    continue;
                }

                // Periodic PID cache cleanup to handle PID reuse.
                // Chrome/VS Code constantly spawn/destroy child processes;
                // stale cached "no match" entries cause new children with
                // recycled PIDs to bypass the tunnel.
                if ((DateTime.UtcNow - lastCacheClear).TotalSeconds > 30)
                {
                    _pidTargetOwnerCache.Clear();
                    _pidNameCache.Clear();
                    _pidParentCache.Clear();
                    lastCacheClear = DateTime.UtcNow;
                }

                byte ev = addr.Event;
                // Event 1 = FLOW_ESTABLISHED, Event 2 = FLOW_DELETED
                if (ev == 2)
                {
                    Interlocked.Increment(ref _statFlowDeleted);

                    // Decrement the per-IP refcount. When it reaches 0,
                    // schedule a delayed route removal so non-target apps
                    // stop piggy-backing. The grace period prevents breaking
                    // target-app reconnections (Chrome opens short-lived flows).
                    if (!addr.IsIPv6)
                    {
                        uint delHost = addr.Flow_RemoteAddr0;
                        uint delNbo = (uint)System.Net.IPAddress.HostToNetworkOrder((int)delHost);
                        _flowOwnerByTuple.TryRemove((addr.Flow_Protocol, addr.Flow_LocalPort, delNbo), out _);
                        if (_ipRefCount.TryGetValue(delNbo, out _))
                        {
                            int newCnt = _ipRefCount.AddOrUpdate(delNbo, 0, (_, v) => v - 1);
                            if (newCnt <= 0)
                            {
                                _ipRefCount.TryRemove(delNbo, out _);
                                ScheduleDelayedRouteRemoval(delNbo);
                            }
                        }
                    }
                    continue;
                }
                if (ev != 1) continue;

                Interlocked.Increment(ref _statFlowEstablished);

                int pid = (int)addr.Flow_ProcessId;
                var processName = GetProcessNameByPid(pid);
                // Resolve the "target owner": if this pid OR any ancestor is a
                // target app, returns that target's name. This catches child
                // processes such as msedgewebview2.exe spawned by WhatsApp.Root.exe
                // or Telegram helper processes, whose sockets would otherwise be
                // invisible to us.
                var targetOwner = ResolveTargetOwner(pid);

                // IPv6 handling: we don't add IPv6 host routes yet, but we DO
                // want to log target-app IPv6 flows so we can diagnose cases
                // where Telegram/chrome silently use IPv6 and bypass our IPv4
                // host routes.
                if (addr.IsIPv6)
                {
                    if (targetOwner != null)
                    {
                        Interlocked.Increment(ref _statFlowIPv6Blocked);
                        if (_flowLogCount < 5)
                        {
                            Interlocked.Increment(ref _flowLogCount);
                            Logger.Info($"[FLOW-IPv6] {targetOwner} (via {processName ?? "?"}) pid={pid} proto={addr.Flow_Protocol} remotePort={addr.Flow_RemotePort}  (IPv6 NOT ROUTED — will be blocked at network layer)");
                        }
                    }
                    continue;
                }

                // WinDivert 2.x stores IPv4 addresses in RemoteAddr[0] as a single
                // UINT32 in HOST byte order. RemoteAddr[1..3] are zero for IPv4.
                // Convert to NETWORK byte order for IpHelper and to produce the
                // usual dotted-quad string.
                uint remoteHost = addr.Flow_RemoteAddr0;
                uint remoteNbo = (uint)System.Net.IPAddress.HostToNetworkOrder((int)remoteHost);
                var ipBytes = BitConverter.GetBytes(remoteNbo);
                var remoteIp = new IPAddress(ipBytes);
                byte b0 = ipBytes[0];
                bool isIncluded = IsIncludedDestination(remoteNbo);

                // Diagnostic: log first N flows regardless of match so we can see
                // what process names WinDivert is giving us.
                if (_flowLogCount < 5)
                {
                    Interlocked.Increment(ref _flowLogCount);
                    bool isTarget = targetOwner != null || isIncluded;
                    string nameForLog = targetOwner != null && targetOwner != processName
                        ? $"{processName ?? "<unknown>"} [owner={targetOwner}]"
                        : processName ?? "<unknown>";
                    Logger.Info($"[FLOW] pid={pid} name='{nameForLog}' proto={addr.Flow_Protocol} → {remoteIp}:{addr.Flow_RemotePort} target={isTarget}");
                }

                if (targetOwner == null && !isIncluded)
                    continue;

                // Skip ranges that must NEVER be routed via VPN:
                //   0.0.0.0/8, 127.0.0.0/8, 224.0.0.0/4 (multicast), 255.x (broadcast)
                //   10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 (RFC1918 private)
                byte b1 = ipBytes[1];
                bool isPrivate =
                    b0 == 0 || b0 == 127 || b0 >= 224 ||
                    b0 == 10 ||
                    (b0 == 172 && b1 >= 16 && b1 <= 31) ||
                    (b0 == 192 && b1 == 168) ||
                    (b0 == 169 && b1 == 254); // link-local
                if (isPrivate) continue;

                // Check user exclude list (domains/IPs that should bypass tunnel)
                if (IsExcludedDestination(remoteNbo))
                {
                    Interlocked.Increment(ref _statFlowExcluded);
                    if (_loggedExcludedIps.TryAdd(remoteNbo, true))
                        Logger.Info($"[FLOW-EXCLUDED] {targetOwner} pid={pid} → {remoteIp}:{addr.Flow_RemotePort}  (destination excluded)");
                    continue;
                }

                Interlocked.Increment(ref _statFlowTargetMatched);
                var ownerForFlow = isIncluded ? "[INCLUDE]" : (targetOwner ?? "<unknown>");

                // Remember IP→process mapping so the egress/ingress sniff loops
                // can attribute bytes to this application.
                _ipToProcess[remoteNbo] = ownerForFlow;
                _flowOwnerByTuple[(addr.Flow_Protocol, addr.Flow_LocalPort, remoteNbo)] = ownerForFlow;
                // Bump refcount — route is removed once this drops back to 0
                // (handled in the FLOW_DELETED branch above).
                _ipRefCount.AddOrUpdate(remoteNbo, 1, (_, v) => v + 1);

                // Cancel any pending delayed removal for this IP.
                if (_pendingRouteRemoval.TryRemove(remoteNbo, out var pendingCts))
                    try { pendingCts.Cancel(); } catch { }

                bool newRoute = EnsureHostRouteViaVpn(remoteNbo, remoteIp);
                if ((newRoute || _loggedMatchIps.TryAdd(remoteNbo, true)) && _flowMatchLogCount < 20)
                {
                    Interlocked.Increment(ref _flowMatchLogCount);
                    string reason = isIncluded ? "(included destination)" : $"(proc={processName ?? "?"})";
                    Logger.Info($"[FLOW-MATCH] {ownerForFlow} pid={pid} {reason} → {remoteIp}:{addr.Flow_RemotePort}  (route installed)");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isRunning) Logger.Error($"Flow tracking loop error: {ex.Message}");
        }
    }
}
