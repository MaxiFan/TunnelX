using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

/// <summary>
/// Routes traffic from selected applications through the VPN tunnel using WinDivert.
/// </summary>
public partial class TrafficRouterService : IDisposable
{
    private readonly ConcurrentDictionary<string, bool> _targetExecutables = new(StringComparer.OrdinalIgnoreCase);
    // Apps explicitly disabled by the user while split mode is active.
    // This hard-deny guard prevents stale flow/process cache races from
    // reinstalling routes for disabled executables.
    private readonly ConcurrentDictionary<string, bool> _blockedExecutables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficCounter> _trafficCounters = new(StringComparer.OrdinalIgnoreCase);

    // Excluded destination IPs (network byte order). Populated from user-entered
    // domains/IPs; checked before installing host routes.
    private readonly ConcurrentDictionary<uint, bool> _excludedIps = new();
    // Raw exclude entries → resolved NBO IPs, so we can remove cleanly.
    private readonly ConcurrentDictionary<string, HashSet<uint>> _excludedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _excludedDomainRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, bool> _excludedDirectRoutes = new();

    // Included destination IPs (network byte order). Populated from user-entered
    // domains/IPs; forced through tunnel regardless of target app selection.
    private readonly ConcurrentDictionary<uint, bool> _includedIps = new();
    // Raw include entries → resolved NBO IPs, so we can remove cleanly.
    private readonly ConcurrentDictionary<string, HashSet<uint>> _includedEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _includedDomainRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DnsRuleQuery> _dnsRuleQueries = new();
    private readonly object _destinationListLock = new();
    private System.Threading.Timer? _destinationRefreshTimer;

    // NAT table: key=(protocol, srcPort); value=entry with original IP/ifIdx/process
    // Used to reverse-translate inbound packets so replies reach the correct socket.
    // NAT key = (protocol, client-srcPort, server-dstIp-NBO)
    // Including the destination IP prevents false matches when the OS recycles
    // a source port for a new connection to a *different* server before the old
    // NAT entry has been cleaned up (Bug #3 — port collision).
    private readonly ConcurrentDictionary<(byte proto, ushort port, uint dstIp), NatEntry> _natTable = new();

    // Host routes we added (dstIP in network byte order) so we can clean them up on stop.
    private readonly ConcurrentDictionary<uint, bool> _addedRoutes = new();
    private long _statRoutesAdded;
    private long _statRoutesFailed;

    private CancellationTokenSource? _cts;
    private Task? _routingTask;
    private Task? _inboundTask = null;
    private int _vpnInterfaceIndex = -1;
    private string _vpnLocalIp = "";
    private string _vpnServerIp = "";    // resolved IPv4 — used in WinDivert filter strings
    private string _vpnServerHost = "";   // original hostname/IP from config — used for TCP health checks
    private int _vpnServerPort = 443;     // original server port from config — used for TCP health checks
    private bool _vpnServerIsUdpOnly;     // true for WireGuard etc. — skip TCP probe in CONN-CHECK
    private bool _vpnServerSkipBareTcpProbe; // VMess/VLESS-WS: port may not accept raw TCP
    private bool _pinVpnServerOnPhysicalNic; // keep OpenVPN TCP / WireGuard UDP control on physical NIC
    private string _vpnGatewayIp = "";    // optional next hop for TAP/OpenVPN host routes
    private byte[]? _vpnLocalIpBytes;
    private volatile bool _isRunning;
    private bool _fullRouteEnabled;
    private int _inboundRewriteCount = 0;
    private IntPtr _outboundHandle = IntPtr.Zero;
    private IntPtr _inboundHandle = IntPtr.Zero;
    private readonly object _handleLock = new();

    public bool IsRunning => _isRunning;
    private long _redirectCount = 0;

    /// <summary>
    /// Checks whether the VPN network interface is still operational.
    /// Returns false if the interface no longer exists or is not Up.
    /// </summary>
    public bool IsVpnInterfaceUp()
    {
        if (!_isRunning || _vpnInterfaceIndex < 0) return false;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == _vpnInterfaceIndex)
                    return nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
            }
        }
        catch { }
        return false; // Interface not found
    }

    // Diagnostics counters
    private long _statTotalCaptured;
    private long _statRedirected;
    private long _statPassthrough;
    private long _statSendFailed;
    private long _statInboundCaptured;
    private long _statInboundNatMatched;
    private long _statInboundSendFailed;
    // Packet intercept (source-IP rewriting) counters
    private long _statNetOutRewritten;
    private long _statWgVpnOutRewritten;
    private long _statWgVpnOutSeen;
    private long _statWgDnsRedirect;
    private long _statWgQuicDropped;
    private long _statNetInRewritten;
    private long _statNetOutPassthrough;
    private long _statNetOutSendFailed;
    private long _statVpnEgressSniffed;
    private long _statVpnEgressFromOurIp;
    private long _statVpnIngressSniffed;
    private long _statVpnIngressToOurIp;
    // Leak accounting:
    // - Confirmed: packet escaped split policy (should remain 0 in normal operation)
    // - Blocked: attempted leak that was dropped locally by leak-guard
    private long _statLeakConfirmed;
    private long _statLeakBlocked;
    private long _statLeakBlockedRecovered;
    private long _statLeakBlockedSuppressed;
    private long _policyTransitionGraceUntilTick;
    // Total bytes observed on the VPN interface, regardless of per-app
    // attribution. These counters give an honest "total tunnel throughput"
    // value for the connection tab, even for traffic we haven't been able
    // to attribute to a selected app (e.g. tail packets after a flow ends).
    private long _totalVpnBytesSent;
    private long _totalVpnBytesReceived;
    private string? _vpnNicId;
    private long _baselineVpnBytesSent;
    private long _baselineVpnBytesReceived;
    // Physical NIC baseline for computing total network usage since connection start.
    private string? _physicalNicId;
    private int _physicalInterfaceIndex = -1;
    private string _physicalGatewayIp = "";
    private byte[]? _physicalLocalIpBytes;
    private long _baselinePhysBytesSent;
    private long _baselinePhysBytesReceived;
    private long _directBytesSent;
    private long _directBytesReceived;
    private long _diagTick;
    private int _statsReportTick;
    private string _lastStatsHealthSignature = "";
    private System.Threading.Timer? _statsTimer;
    private Task? _vpnSniffTask;
    private Task? _physSniffTask;
    private Task? _directSniffTask;
    private Task? _vpnIngressTask;
    private IntPtr _vpnSniffHandle = IntPtr.Zero;
    private IntPtr _physSniffHandle = IntPtr.Zero;
    private IntPtr _directSniffHandle = IntPtr.Zero;
    private IntPtr _vpnIngressHandle = IntPtr.Zero;
    private IntPtr _ipv6BlockHandle = IntPtr.Zero;
    private Task? _ipv6BlockTask;

    /// <summary>
    /// If true, do NOT rewrite any packets — just capture, classify, and reinject.
    /// Used to test whether WinDivert itself is breaking connectivity.
    /// Set via environment variable TUNNELX_PASSTHROUGH=1 or property.
    /// </summary>
    public bool PassthroughMode { get; set; } =
        Environment.GetEnvironmentVariable("TUNNELX_PASSTHROUGH") == "1";

    /// <summary>
    /// When true, a SOCKS5 proxy is started on 127.0.0.1:<see cref="Socks5Port"/>
    /// while the VPN is up. Outgoing sockets are bound to the VPN local IP,
    /// guaranteeing egress via the tunnel for any app configured to use the
    /// proxy (e.g. Telegram Settings → Connection type → SOCKS5).
    /// </summary>
    public bool EnableSocks5 { get; set; } = true;

    /// <summary>Listener port for the built-in mixed proxy (SOCKS5 + HTTP).</summary>
    public int Socks5Port { get; set; } = 1080;

    /// <summary>
    /// Enables DNS optimization features (cached resolves + best DNS redirect target).
    /// </summary>
    public bool EnableDnsOptimization { get; set; } = true;

    /// <summary>
    /// Enables packet-level UDP/TCP port-53 redirection for target apps.
    /// Some WireGuard providers drop plain DNS (UDP/53) inside the tunnel, so
    /// WireGuard can disable this while keeping the rest of split routing active.
    /// </summary>
    public bool EnableDnsRedirect { get; set; } = true;
    public string DnsRedirectOverrideIp { get; set; } = "";

    private bool _enableGameMode;
    /// <summary>
    /// Game mode prefers lower latency behavior for routed packets.
    /// </summary>
    public bool EnableGameMode
    {
        get => _enableGameMode;
        set
        {
            _enableGameMode = value;
            _routeRemovalGraceSeconds = value ? 180 : 60;
        }
    }

    private int _routeRemovalGraceSeconds = 60;
    internal int RouteRemovalGraceSeconds => _routeRemovalGraceSeconds;

    private IPAddress _dnsRedirectIp = IPAddress.Parse("8.8.8.8");
    private uint _dnsRedirectIpNbo = BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0);
    private byte[] _dnsRedirectIpBytes = new byte[] { 8, 8, 8, 8 };
    // Flow-layer hint: (proto, localPort) → target app for port-53 sockets.
    // Chrome often issues IPv6 DNS first; connCache may miss the UDP owner on IPv4.
    private readonly ConcurrentDictionary<(byte proto, ushort localPort), string> _dnsPortOwners = new();
    private readonly ConcurrentDictionary<int, string> _dnsPidOwners = new();

    private MixedProxyServer? _mixedProxy;
    private Task? _wgVpnOutTask;
    private IntPtr _wgVpnOutHandle = IntPtr.Zero;

#pragma warning disable CS0067
    public event Action<string, long, long>? TrafficUpdated;
#pragma warning restore CS0067

    public void AddTargetApp(string executableName)
    {
        _blockedExecutables.TryRemove(executableName, out _);
        _targetExecutables[executableName] = true;
        _trafficCounters.TryAdd(executableName, new TrafficCounter());
        InvalidateProcessCaches();
        Logger.Info($"[APP-TARGET] Enabled '{executableName}' (targets={_targetExecutables.Count}, blocked={_blockedExecutables.Count})");
        ReconcileTargetAppPolicy(executableName, enabled: true);
    }

    public void ClearTargetApps()
    {
        foreach (var exe in _targetExecutables.Keys)
            _blockedExecutables[exe] = true;
        _targetExecutables.Clear();
        InvalidateProcessCaches();
        CleanupRoutesForCurrentMode();
    }

    public void RemoveTargetApp(string executableName)
    {
        // Policy transitions can trigger short-lived retransmits from stale sockets.
        // Keep a grace window so these blocked packets are not surfaced as hard leaks.
        MarkPolicyTransitionGrace(TimeSpan.FromSeconds(20));
        _targetExecutables.TryRemove(executableName, out _);
        _blockedExecutables[executableName] = true;
        InvalidateProcessCaches();
        Logger.Info($"[APP-TARGET] Disabled '{executableName}' (targets={_targetExecutables.Count}, blocked={_blockedExecutables.Count})");
        ReconcileTargetAppPolicy(executableName, enabled: false);
        CleanupRoutesForCurrentMode(dropStaleNat: true);
    }

    public (long sent, long received) GetTraffic(string executableName)
    {
        if (_trafficCounters.TryGetValue(executableName, out var counter))
            return (counter.BytesSent, counter.BytesReceived);
        return (0, 0);
    }

    /// <summary>
    /// Returns the total number of bytes sent and received through the VPN
    /// interface since the tunnel was started. Unlike <see cref="GetTraffic"/>,
    /// this is NOT filtered by process and includes all tunnelled traffic,
    /// so the connection-tab "total" reading reflects actual tunnel usage.
    /// </summary>
    public (long sent, long received) GetTotalVpnTraffic()
    {
        var sniffSent = Interlocked.Read(ref _totalVpnBytesSent);
        var sniffReceived = Interlocked.Read(ref _totalVpnBytesReceived);
        if (_vpnNicId == null)
            return (sniffSent, sniffReceived);

        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == _vpnNicId);
            if (nic == null)
                return (sniffSent, sniffReceived);

            var stats = nic.GetIPStatistics();
            var adapterSent = Math.Max(0, stats.BytesSent - _baselineVpnBytesSent);
            var adapterReceived = Math.Max(0, stats.BytesReceived - _baselineVpnBytesReceived);

            // Sniff counters are still used for per-app attribution, but adapter
            // counters are the safer total when a read-only WinDivert handle misses
            // packets on a specific VPN driver.
            return (Math.Max(sniffSent, adapterSent), Math.Max(sniffReceived, adapterReceived));
        }
        catch
        {
            return (sniffSent, sniffReceived);
        }
    }

    /// <summary>
    /// Returns the sum of tunnel traffic attributed to app counters during the
    /// current connection. This intentionally includes apps that were disabled
    /// later in the same session, so per-app totals remain consistent with the
    /// history and total tunnel counters.
    /// </summary>
    public (long sent, long received) GetTrackedAppsTraffic()
    {
        long sent = 0;
        long received = 0;
        foreach (var counter in _trafficCounters.Values)
        {
            sent += Interlocked.Read(ref counter.BytesSent);
            received += Interlocked.Read(ref counter.BytesReceived);
        }
        return (sent, received);
    }

    /// <summary>
    /// Returns total bytes sent/received on the physical NIC since the tunnel started.
    /// This represents ALL network activity (VPN-encapsulated + direct).
    /// </summary>
    public (long sent, long received) GetTotalNetworkTraffic()
    {
        if (_physicalNicId == null) return (0, 0);
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == _physicalNicId);
            if (nic == null) return (0, 0);
            var stats = nic.GetIPStatistics();
            return (stats.BytesSent - _baselinePhysBytesSent,
                    stats.BytesReceived - _baselinePhysBytesReceived);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Returns traffic observed on the physical NIC that did not belong to the
    /// tunnel carrier connection itself. This is the UI's "direct traffic"
    /// source; it avoids subtracting inner tunnel payload from encrypted
    /// physical bytes, which is inaccurate for Xray/sing-box transports.
    /// </summary>
    public (long sent, long received) GetDirectTraffic()
        => (Interlocked.Read(ref _directBytesSent),
            Interlocked.Read(ref _directBytesReceived));

    /// <summary>
    /// Confirmed leak count (actual escape from split policy).
    /// Expected to stay zero when leak-guard is active.
    /// </summary>
    public long LeakCount => Interlocked.Read(ref _statLeakConfirmed);
    /// <summary>
    /// Number of attempted leaks blocked locally by leak-guard.
    /// Diagnostic-only signal; these packets did not escape the machine.
    /// </summary>
    public long LeakBlockedCount => Interlocked.Read(ref _statLeakBlocked);
    public long LeakBlockedRecoveredCount => Interlocked.Read(ref _statLeakBlockedRecovered);
    public long LeakBlockedSuppressedCount => Interlocked.Read(ref _statLeakBlockedSuppressed);
    public long Ipv6BlockedCount => Interlocked.Read(ref _statFlowIPv6Blocked);
    public long DnsRedirectCount => Interlocked.Read(ref _redirectCount);
    public long ActiveRouteCount => _addedRoutes.Count;
    public long RouteFailureCount => Interlocked.Read(ref _statRoutesFailed);

    public void Start(
        int vpnInterfaceIndex,
        string vpnLocalIp,
        string vpnServerIp,
        string vpnGatewayIp = "",
        int vpnServerPort = 443,
        bool resetCounters = true,
        bool serverIsUdpOnly = false,
        bool pinVpnServerOnPhysicalNic = false,
        bool serverSkipBareTcpProbe = false)
    {
        if (vpnInterfaceIndex <= 0 || string.IsNullOrWhiteSpace(vpnLocalIp))
        {
            Logger.Error($"[ROUTER] Start aborted: invalid VPN session (ifIdx={vpnInterfaceIndex}, ip='{vpnLocalIp}')");
            throw new InvalidOperationException($"Invalid VPN session for TrafficRouter (ifIdx={vpnInterfaceIndex}).");
        }

        if (_isRunning)
        {
            Logger.Warning("[ROUTER] Start called while already running — restarting for new VPN session");
            StopAsync(resetCounters: false).GetAwaiter().GetResult();
        }

        _vpnInterfaceIndex = vpnInterfaceIndex;
        _vpnLocalIp = vpnLocalIp;
        _vpnGatewayIp = vpnGatewayIp;
        _vpnServerIsUdpOnly = serverIsUdpOnly;
        _vpnServerSkipBareTcpProbe = serverSkipBareTcpProbe;
        _pinVpnServerOnPhysicalNic = pinVpnServerOnPhysicalNic;
        _vpnLocalIpBytes = IPAddress.TryParse(vpnLocalIp, out var vpnAddr)
            ? vpnAddr.GetAddressBytes()
            : null;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        if (!_vpnServerIsUdpOnly)
            _wgVpnOutTask = null;

        // Keep the original hostname for TCP-based health checks (domain may be behind
        // a CDN that returns different IPs; we should connect by name, not cached IP).
        _vpnServerHost = vpnServerIp;
        _vpnServerPort = vpnServerPort > 0 && vpnServerPort <= 65535 ? vpnServerPort : 443;

        // Resolve VPN server address to an IPv4 string.
        // WinDivert filters require a literal IP address — hostnames are invalid
        // and cause WinDivertOpen to fail, silently killing packet interception.
        // Re-resolved on every Start() so CDN IP changes are picked up on reconnect.
        if (IPAddress.TryParse(vpnServerIp, out _))
        {
            _vpnServerIp = vpnServerIp;
        }
        else if (_vpnServerIsUdpOnly)
        {
            // WireGuard connect already resolves the endpoint; avoid a second
            // blocking GetHostAddresses call here (often ~20s on filtered DNS).
            _vpnServerIp = "0.0.0.0";
            Logger.Warning($"[DNS] WireGuard server '{vpnServerIp}' is not IPv4 yet — WG-OUT filter uses 0.0.0.0 until refresh");
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var v4 = await DnsResolverCache.ResolveFirstIpv4Async(vpnServerIp, cts.Token);
                    if (v4 != null)
                    {
                        _vpnServerIp = v4.ToString();
                        Logger.Info($"[DNS] WireGuard server resolved async '{vpnServerIp}' → {_vpnServerIp}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[DNS] Async WireGuard server resolve failed: {ex.Message}");
                }
            });
        }
        else
        {
            try
            {
                var v4 = DnsResolverCache.ResolveFirstIpv4(vpnServerIp);
                _vpnServerIp = v4?.ToString() ?? vpnServerIp;
                if (v4 != null)
                    Logger.Info($"[DNS] Resolved VPN server '{vpnServerIp}' → {_vpnServerIp}");
                else
                    Logger.Warning($"[DNS] Could not resolve VPN server '{vpnServerIp}' to IPv4 — WinDivert filters may fail");
            }
            catch (Exception ex)
            {
                _vpnServerIp = vpnServerIp;
                Logger.Warning($"[DNS] Hostname resolution failed for '{vpnServerIp}': {ex.Message}");
            }
        }

        // Defensive: if _vpnServerIp is still not a literal IPv4 address (DNS
        // resolution failed completely), fall back to a sentinel that is a
        // syntactically valid IPv4 but unroutable. Without this guard, the
        // hostname would be embedded into WinDivert filter strings such as
        // "ip.DstAddr != example.com", which fail to compile silently and
        // disable packet interception. The sentinel keeps the filter valid;
        // the connection will of course not work end-to-end but at least the
        // failure mode is observable (sing-box reports a clear DNS error).
        if (!IPAddress.TryParse(_vpnServerIp, out _))
        {
            Logger.Error($"[DNS] '{_vpnServerIp}' is not a valid IPv4 — using sentinel 0.0.0.0 in filters so they remain syntactically valid");
            _vpnServerIp = "0.0.0.0";
        }

        ConfigureDnsRedirectTarget();

        if (resetCounters)
            ResetLiveTrafficCounters();

        // Reset flow-log counters so session 2 gets fresh log output.
        _flowLogCount = 0;
        _flowMatchLogCount = 0;

        Logger.Info($"TrafficRouter starting: VPN Interface={vpnInterfaceIndex}, LocalIP={vpnLocalIp}, Gateway={_vpnGatewayIp}, ServerIP={vpnServerIp}");

        // Fast path: Wi-Fi/Ethernet by name only (full scan runs deferred).
        if (!TryBootstrapPhysicalNicFast(vpnInterfaceIndex))
        {
            _physicalInterfaceIndex = -1;
            _physicalGatewayIp = "";
            _physicalLocalIpBytes = null;
        }
        TryBootstrapVpnNicBaseline(vpnInterfaceIndex);

        Logger.Info($"Target apps: {string.Join(", ", _targetExecutables.Keys)}");
        if (PassthroughMode)
            Logger.Warning("DIAGNOSTIC PASSTHROUGH MODE ENABLED — packets will NOT be redirected. For testing only.");

        if (_pinVpnServerOnPhysicalNic)
        {
            if (AddVpnServerPhysicalRoute())
                Logger.Info($"[VPN-ROUTE] VPN server {_vpnServerIp} pinned to physical NIC for tunnel control traffic");
            else
                Logger.Warning($"[VPN-ROUTE] Could not pin VPN server {_vpnServerIp} to physical gateway");
        }

        if (_vpnServerIsUdpOnly)
        {
            AutoExcludeSystemDnsServers();
            RemoveDefaultRouteOnVpn();
            InstallWireGuardDnsBootstrap();
        }

        // Flow tracking must run before NET-OUT so DNS port→app hints exist when
        // Chrome falls back from blocked IPv6 DNS to IPv4 resolver traffic.
        _routingTask = Task.Run(() => FlowTrackingLoop(_cts.Token));
        // Start source-IP rewriting before DNS/include refresh. If system DNS is
        // filtered, DnsResolverCache may use DoH bootstrap IPs that we just routed
        // via the VPN, and those sockets need the same strong-host NAT handling.
        _networkOutTask = Task.Run(() => NetworkOutboundLoop(_cts.Token));
        _networkInTask = Task.Run(() => NetworkInboundLoop(_cts.Token));
        if (_vpnServerIsUdpOnly)
        {
            _wgVpnOutTask = null;
            Logger.Info("[WG-OUT] Active VPN-interface intercept disabled for WireGuard; using NET-OUT/NET-IN plus read-only sniffing");
        }
        _fullRouteEnabled = false;
        _routeBootstrapReady = false;
        // Install DoH/bootstrap routes before INCLUDE/EXCLUDE DNS refresh so lookups
        // egress via the tunnel (not filtered system DNS on the physical path).
        _ = Task.Run(RunDeferredRouteBootstrap);
        _destinationRefreshTimer = new System.Threading.Timer(_ => RefreshDestinationLists(installIncludedRoutes: true), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _ = Task.Run(RunConnectivityChecks);

        // NEW ARCHITECTURE (flow-based, zero-copy):
        //   We no longer capture/rewrite every packet. Instead we listen at the
        //   FLOW layer for connect events from target apps, and proactively add
        //   a /32 host route via the VPN adapter. Windows then natively routes
        //   that destination via the VPN — picking the VPN IP as source —
        // Block outbound IPv6 from target apps at the NETWORK layer.
        // The FLOW layer is observe-only (SNIFF mode) and cannot block;
        // intercepting at the packet level lets us silently drop IPv6
        // packets whose owning process is a target app, forcing IPv4
        // fallback which gets properly tunneled via host routes.
        _ipv6BlockTask = Task.Run(() => IPv6BlockLoop(_cts.Token));

        // Diagnostic sniff loops stay enabled so we can verify traffic actually
        // exits via VPN and that no srcIP leakage happens on the physical NIC.
        _vpnSniffTask = Task.Run(() => VpnEgressSniffLoop(_cts.Token));
        _physSniffTask = Task.Run(() => PhysicalEgressSniffLoop(_cts.Token));
        _directSniffTask = Task.Run(() => PhysicalDirectTrafficSniffLoop(_cts.Token));
        _vpnIngressTask = Task.Run(() => VpnIngressSniffLoop(_cts.Token));

        // Stats reporter every 5 seconds
        _statsTimer = new System.Threading.Timer(_ => ReportStats(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Optional mixed SOCKS5/HTTP proxy
        if (EnableSocks5)
        {
            _mixedProxy = new MixedProxyServer(Socks5Port);
            _mixedProxy.Start(vpnLocalIp, EnsureHostRouteForSocks5);
        }
    }

    private void RegisterDnsPortOwner(byte proto, ushort localPort, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return;
        _dnsPortOwners[(proto, localPort)] = owner;
    }

    private string? TryGetDnsPortOwner(byte proto, ushort localPort)
        => _dnsPortOwners.TryGetValue((proto, localPort), out var owner) ? owner : null;

    private void RegisterDnsPidOwner(int pid, string owner)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(owner))
            return;
        _dnsPidOwners[pid] = owner;
    }

    private string? TryGetDnsPidOwner(int pid)
        => pid > 0 && _dnsPidOwners.TryGetValue(pid, out var owner) ? owner : null;

    private string? ResolveDnsProcessOwner(ConnectionProcessCache connCache, ConnectionTuple tuple)
    {
        var proc = connCache.GetProcessName(tuple);
        if (!string.IsNullOrWhiteSpace(proc))
            return proc;

        proc = TryGetDnsPortOwner(tuple.Protocol, tuple.LocalPort);
        if (!string.IsNullOrWhiteSpace(proc))
            return proc;

        var pid = connCache.GetOwningPid(tuple);
        if (pid > 0)
        {
            proc = TryGetDnsPidOwner(pid);
            if (!string.IsNullOrWhiteSpace(proc))
                return proc;
        }

        return null;
    }

    private void InstallWireGuardDnsBootstrap()
    {
        if (!_vpnServerIsUdpOnly || !EnableDnsRedirect)
            return;

        EnsureHostRouteViaVpn(_dnsRedirectIpNbo, _dnsRedirectIp);
        _ipToProcess[_dnsRedirectIpNbo] = "[WG-DNS]";
        Logger.Info($"[WG-DNS] Resolver {_dnsRedirectIp} host-routed via WireGuard (NET-OUT/WG-OUT DNS + DoH bootstrap)");

        // DoH resolvers from DnsResolverCache + profile DNS (no hardcoded public IP list).
        var dohRoutes = new HashSet<uint> { _dnsRedirectIpNbo };
        foreach (var resolver in DnsResolverCache.DohBootstrapResolvers)
        {
            var nbo = BitConverter.ToUInt32(resolver.GetAddressBytes(), 0);
            if (!dohRoutes.Add(nbo) || IsExcludedDestination(nbo))
                continue;
            EnsureHostRouteViaVpn(nbo, resolver);
            _ipToProcess[nbo] = "[WG-DOH]";
        }

    }

    private void InstallDohBootstrapRoutes()
    {
        if (!EnableDnsOptimization)
            return;

        // TunnelX DoH bootstrap must not be forced through WireGuard when the peer
        // drops UDP/53 — it would stall include/exclude refresh and health checks.
        if (_vpnServerIsUdpOnly)
            return;

        foreach (var resolver in DnsResolverCache.DohBootstrapResolvers)
        {
            try
            {
                var nbo = BitConverter.ToUInt32(resolver.GetAddressBytes(), 0);
                _includedIps[nbo] = true;
                _ipToProcess[nbo] = "[DNS-DOH]";
                EnsureHostRouteViaVpn(nbo, resolver);
                Logger.Info($"[DNS-DOH] Bootstrap resolver routed via VPN: {resolver}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DNS-DOH] Could not route bootstrap resolver {resolver}: {ex.Message}");
            }
        }
    }

    private void ReportStats()
    {
        if (!_isRunning) return;
        long flowEst = Interlocked.Read(ref _statFlowEstablished);
        long flowDel = Interlocked.Read(ref _statFlowDeleted);
        long flowHit = Interlocked.Read(ref _statFlowTargetMatched);
        long flowExcl = Interlocked.Read(ref _statFlowExcluded);
        long ipv6Blocked = Interlocked.Read(ref _statFlowIPv6Blocked);
        long leakConfirmed = Interlocked.Read(ref _statLeakConfirmed);
        long leakBlocked = Interlocked.Read(ref _statLeakBlocked);
        long leakBlockedRecovered = Interlocked.Read(ref _statLeakBlockedRecovered);
        long leakBlockedSuppressed = Interlocked.Read(ref _statLeakBlockedSuppressed);
        long netOutRw = Interlocked.Read(ref _statNetOutRewritten);
        long wgOutRw = Interlocked.Read(ref _statWgVpnOutRewritten);
        long wgOutSeen = Interlocked.Read(ref _statWgVpnOutSeen);
        long wgDnsRedirect = Interlocked.Read(ref _statWgDnsRedirect);
        long wgQuicDropped = Interlocked.Read(ref _statWgQuicDropped);
        long netInRw = Interlocked.Read(ref _statNetInRewritten);
        long netOutFail = Interlocked.Read(ref _statNetOutSendFailed);
        long vpnOut = Interlocked.Read(ref _statVpnEgressSniffed);
        long vpnOutOurIp = Interlocked.Read(ref _statVpnEgressFromOurIp);
        long vpnIn = Interlocked.Read(ref _statVpnIngressSniffed);
        long vpnInOurIp = Interlocked.Read(ref _statVpnIngressToOurIp);
        string mode = _fullRouteEnabled ? "full-route" : "split";
        string leakState = leakConfirmed > 0 ? "LEAK-DETECTED" :
            (leakBlocked > 0 ? "PROTECTED" : "OK");
        var statsTick = Interlocked.Increment(ref _statsReportTick);
        var healthSignature = $"{mode}|{leakState}";
        var logStats = leakState != "OK" ||
                       !string.Equals(_lastStatsHealthSignature, healthSignature, StringComparison.Ordinal) ||
                       statsTick % 6 == 0;
        if (logStats)
        {
            _lastStatsHealthSignature = healthSignature;
            Logger.Info(
                $"[STATS] mode={mode} health={leakState} " +
                $"flows={flowEst}/{flowDel} targetHit={flowHit} excluded={flowExcl} ipv6Drop={ipv6Blocked} " +
                $"routes={Interlocked.Read(ref _statRoutesAdded)}({Interlocked.Read(ref _statRoutesFailed)}fail)/{_addedRoutes.Count}active " +
                $"rewriteOut={netOutRw} wgSeen={wgOutSeen} wgRewrite={wgOutRw} wgDns={wgDnsRedirect} wgQuicDrop={wgQuicDropped} rewriteIn={netInRw} rewriteFail={netOutFail} " +
                $"vpnOut={vpnOut}/{vpnOutOurIp} vpnIn={vpnIn}/{vpnInOurIp} nat={_natTable.Count} " +
                $"leakConfirmed={leakConfirmed} protectedBlocked={leakBlocked} recovered={leakBlockedRecovered} suppressed={leakBlockedSuppressed} " +
                $"targets={_targetExecutables.Count} blockedApps={_blockedExecutables.Count}");
        }
        if (_vpnServerIsUdpOnly)
        {
            var wgTicks = Interlocked.Increment(ref _diagTick);
            if (wgTicks % 2 == 0)
            {
                try { RemoveDefaultRouteOnVpn(); } catch { }
            }
        }

        // Loop health check — warn if any background loop has exited unexpectedly
        var deadLoops = new List<string>();
        if (_routingTask?.IsCompleted == true) deadLoops.Add("FlowTracking");
        if (_networkOutTask?.IsCompleted == true) deadLoops.Add("NetOut");
        if (_networkInTask?.IsCompleted == true) deadLoops.Add("NetIn");
        if (_vpnServerIsUdpOnly && _wgVpnOutTask?.IsCompleted == true) deadLoops.Add("WgVpnOut");
        if (_vpnSniffTask?.IsCompleted == true) deadLoops.Add("VpnSniff");
        if (_physSniffTask?.IsCompleted == true) deadLoops.Add("PhysSniff");
        if (_directSniffTask?.IsCompleted == true) deadLoops.Add("DirectSniff");
        if (_vpnIngressTask?.IsCompleted == true) deadLoops.Add("VpnIngress");
        if (_ipv6BlockTask?.IsCompleted == true) deadLoops.Add("IPv6Block");
        if (deadLoops.Count > 0)
            Logger.Warning($"[HEALTH] Dead loops detected: {string.Join(", ", deadLoops)}");

        // Cleanup stale NAT entries (connections closed > 2 minutes ago)
        int natCleaned = 0;
        var natStaleTime = DateTime.UtcNow.AddMinutes(-2);
        foreach (var kv in _natTable)
            if (kv.Value.LastSeen < natStaleTime)
                if (_natTable.TryRemove(kv.Key, out _))
                    natCleaned++;

        // Periodic network-state diagnostics (every ~30s) so we can tell from
        // the log whether a sing-box "missing default interface" or sustained
        // i/o timeout coincides with the physical NIC actually flapping.
        var ticks = Interlocked.Increment(ref _diagTick);
        if (ticks % 6 == 1) // 6 \u00d7 5s = 30s
        {
            try
            {
                var defGwCount = 0;
                string? defNic = null;
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    System.Net.NetworkInformation.IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); } catch { continue; }
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            defGwCount++;
                            defNic ??= nic.Name;
                        }
                    }
                }
                Logger.Info($"[DIAG] defaultGateways={defGwCount} primaryNic='{defNic ?? "<none>"}'");
            }
            catch { }
        }
        if (natCleaned > 0)
            Logger.Info($"[NET-STATS] NAT stale entries cleaned: {natCleaned}");
    }

    private volatile bool _routeBootstrapReady;

    private void RunDeferredRouteBootstrap()
    {
        try
        {
            DiscoverPhysicalNicBaseline(_vpnInterfaceIndex);
            AutoExcludeSystemDnsServers();
            LogNetworkInterfaces();
            try
            {
                var vpnNic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => {
                        try { return n.GetIPProperties().GetIPv4Properties()?.Index == _vpnInterfaceIndex; }
                        catch { return false; }
                    });
                if (vpnNic == null)
                    Logger.Warning($"[VPN-DETECT] No NIC found with interface index {_vpnInterfaceIndex}!");
                else
                    Logger.Info($"[VPN-DETECT] VPN NIC confirmed: name='{vpnNic.Name}' type={vpnNic.NetworkInterfaceType} status={vpnNic.OperationalStatus}");
            }
            catch (Exception ex) { Logger.Warning($"[VPN-DETECT] NIC validation failed: {ex.Message}"); }

            RemoveDefaultRouteOnVpn();
            InstallDohBootstrapRoutes();
            InstallWireGuardDnsBootstrap();
            // INCLUDE tunnel-DNS may run as soon as DoH/bootstrap routes exist.
            _routeBootstrapReady = true;

            if (_vpnServerIsUdpOnly)
            {
                if (AddVpnServerPhysicalRoute())
                    Logger.Info($"[WG-ROUTE] VPN server {_vpnServerIp} pinned to physical NIC for WireGuard control traffic");
                else
                    Logger.Warning($"[WG-ROUTE] Could not pin VPN server {_vpnServerIp} to physical gateway");
            }

            try
            {
                RefreshDestinationLists(installIncludedRoutes: true);
                Logger.Info("[DEST] Initial include/exclude refresh completed (INCLUDE=tunnel, EXCLUDE=direct-first)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DEST] Initial refresh failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ROUTER] Deferred route bootstrap failed: {ex.Message}");
        }
    }

    private TunnelDnsResolveOptions? CreateTunnelDnsResolveOptions()
    {
        if (!_isRunning || !_routeBootstrapReady)
            return null;

        if (!IPAddress.TryParse(_vpnLocalIp, out var bindIp))
            return null;

        return new TunnelDnsResolveOptions
        {
            BindLocalAddress = bindIp,
            PreferDohFirst = true,
            TunnelOnly = true,
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    /// <summary>
    /// Registers Windows-configured DNS resolvers as excluded so general traffic uses the
    /// physical path (exclude = outside tunnel). Target-app DNS to these resolvers is still
    /// redirected to <see cref="_dnsRedirectIp"/> via NET-OUT / WG-OUT.
    /// </summary>
    private void AutoExcludeSystemDnsServers()
    {
        try
        {
            var dnsCount = 0;

            void TryExcludeDns(IPAddress? dns, string reason)
            {
                if (dns == null || dns.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return;
                var nbo = BitConverter.ToUInt32(dns.GetAddressBytes(), 0);
                if (nbo == _dnsRedirectIpNbo)
                    return;
                byte b0 = dns.GetAddressBytes()[0];
                if (b0 is 0 or 127 or >= 224)
                    return;
                if (_excludedIps.TryAdd(nbo, true))
                {
                    dnsCount++;
                    Logger.Info($"[DNS-EXCLUDE] {dns} ({reason})");
                }
            }

            System.Net.NetworkInformation.NetworkInterface? physicalNic = null;
            if (_physicalInterfaceIndex > 0)
            {
                physicalNic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n =>
                    {
                        try { return n.GetIPProperties().GetIPv4Properties()?.Index == _physicalInterfaceIndex; }
                        catch { return false; }
                    });
            }

            if (physicalNic != null)
            {
                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = physicalNic.GetIPProperties(); }
                catch { props = null!; }
                if (props != null)
                {
                    foreach (var dns in props.DnsAddresses)
                        TryExcludeDns(dns, $"auto-excluded — DNS on physical NIC '{physicalNic.Name}'");
                }
            }

            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (IsVirtualNicName(nic.Name)) continue;
                if (physicalNic != null && nic.Id == physicalNic.Id) continue;

                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                foreach (var dns in props.DnsAddresses)
                    TryExcludeDns(dns, $"auto-excluded — DNS on '{nic.Name}'");
            }

            if (dnsCount == 0)
                Logger.Info("[DNS-EXCLUDE] No system DNS servers found to auto-exclude");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[DNS-EXCLUDE] Enumeration failed: {ex.Message}");
        }
    }

    private void ConfigureDnsRedirectTarget()
    {
        var selected = IPAddress.TryParse(DnsRedirectOverrideIp, out var overrideIp) &&
                       overrideIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? overrideIp
            : EnableDnsOptimization
                ? (EnableGameMode ? IPAddress.Parse("1.1.1.1") : IPAddress.Parse("8.8.8.8"))
                : IPAddress.Parse("8.8.8.8");

        _dnsRedirectIp = selected;
        _dnsRedirectIpBytes = selected.GetAddressBytes();
        _dnsRedirectIpNbo = BitConverter.ToUInt32(_dnsRedirectIpBytes, 0);

        var source = string.IsNullOrWhiteSpace(DnsRedirectOverrideIp) ? "default" : "override";
        Logger.Info($"[DNS] Redirect target={_dnsRedirectIp} source={source} enabled={EnableDnsRedirect} optimization={EnableDnsOptimization} gameMode={EnableGameMode}");
    }

    private void LogNetworkInterfaces()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                var props = nic.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                int idx = ipv4?.Index ?? -1;
                var addrs = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString());
                var gws = props.GatewayAddresses.Select(g => g.Address.ToString());
                Logger.Info($"[NIC] idx={idx} name='{nic.Name}' type={nic.NetworkInterfaceType} ips=[{string.Join(",", addrs)}] gw=[{string.Join(",", gws)}]");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"LogNetworkInterfaces failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears process/ownership caches so policy changes apply immediately to
    /// already-running processes.
    /// </summary>
    private void InvalidateProcessCaches()
    {
        _pidTargetOwnerCache.Clear();
        _pidNameCache.Clear();
        _pidParentCache.Clear();
    }

    private bool IsExecutableBlocked(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;
        return _blockedExecutables.ContainsKey(executableName);
    }

    private bool IsExecutableTargeted(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return false;
        if (IsExecutableBlocked(executableName))
            return false;
        return _targetExecutables.ContainsKey(executableName);
    }

    private void ResetLiveTrafficCounters()
    {
        Interlocked.Exchange(ref _totalVpnBytesSent, 0);
        Interlocked.Exchange(ref _totalVpnBytesReceived, 0);
        Interlocked.Exchange(ref _directBytesSent, 0);
        Interlocked.Exchange(ref _directBytesReceived, 0);
        foreach (var counter in _trafficCounters.Values)
        {
            Interlocked.Exchange(ref counter.BytesSent, 0);
            Interlocked.Exchange(ref counter.BytesReceived, 0);
        }
    }

    private void MarkPolicyTransitionGrace(TimeSpan duration)
    {
        long now = Environment.TickCount64;
        long until = now + (long)duration.TotalMilliseconds;
        while (true)
        {
            long current = Interlocked.Read(ref _policyTransitionGraceUntilTick);
            if (current >= until)
                return;
            if (Interlocked.CompareExchange(ref _policyTransitionGraceUntilTick, until, current) == current)
                return;
        }
    }

    private bool IsPolicyTransitionGraceActive()
    {
        long now = Environment.TickCount64;
        long until = Interlocked.Read(ref _policyTransitionGraceUntilTick);
        return now < until;
    }

    /// <summary>
    /// Reconciles routing/NAT state for one target app immediately after the user
    /// toggles it in the UI. When disabled, lingering host-routes and NAT records
    /// owned by that app are removed so policy changes take effect right away.
    /// </summary>
    private void ReconcileTargetAppPolicy(string executableName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return;

        if (enabled)
            return;

        var removedRouteIps = new HashSet<uint>();
        foreach (var kv in _ipToProcess)
        {
            if (!string.Equals(kv.Value, executableName, StringComparison.OrdinalIgnoreCase))
                continue;

            uint ipNbo = kv.Key;
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

        int natRemoved = 0;
        foreach (var nat in _natTable)
        {
            bool remove = string.Equals(nat.Value.ProcessName, executableName, StringComparison.OrdinalIgnoreCase) ||
                          removedRouteIps.Contains(nat.Key.dstIp);
            if (remove && _natTable.TryRemove(nat.Key, out _))
                natRemoved++;
        }

        if (removedRouteIps.Count > 0 || natRemoved > 0)
            Logger.Info($"[APP-RECONCILE] '{executableName}' disabled: removedRoutes={removedRouteIps.Count}, removedNat={natRemoved}");
    }

    public async Task StopAsync(bool resetCounters = true)
    {
        if (!_isRunning) return;

        Logger.Info("TrafficRouter stopping...");
        _isRunning = false;
        _routeBootstrapReady = false;
        _cts?.Cancel();
        _statsTimer?.Dispose();
        _statsTimer = null;
        _destinationRefreshTimer?.Dispose();
        _destinationRefreshTimer = null;

        try { _mixedProxy?.Stop(); } catch { }
        _mixedProxy = null;

        // Cancel all pending delayed route removals.
        foreach (var kvp in _pendingRouteRemoval)
            try { kvp.Value.Cancel(); } catch { }
        _pendingRouteRemoval.Clear();

        // Close WinDivert handles to unblock WinDivertRecv calls
        CloseHandles();

        if (_routingTask != null)
        {
            try { await _routingTask; }
            catch (OperationCanceledException) { }
        }
        if (_inboundTask != null)
        {
            try { await _inboundTask; }
            catch (OperationCanceledException) { }
        }
        if (_vpnSniffTask != null)
        {
            try { await _vpnSniffTask; }
            catch (OperationCanceledException) { }
        }
        if (_physSniffTask != null)
        {
            try { await _physSniffTask; }
            catch (OperationCanceledException) { }
        }
        if (_directSniffTask != null)
        {
            try { await _directSniffTask; }
            catch (OperationCanceledException) { }
        }
        if (_vpnIngressTask != null)
        {
            try { await _vpnIngressTask; }
            catch (OperationCanceledException) { }
        }
        if (_ipv6BlockTask != null)
        {
            try { await _ipv6BlockTask; }
            catch (OperationCanceledException) { }
        }
        if (_networkOutTask != null)
        {
            try { await _networkOutTask; }
            catch (OperationCanceledException) { }
        }
        if (_networkInTask != null)
        {
            try { await _networkInTask; }
            catch (OperationCanceledException) { }
        }
        if (_wgVpnOutTask != null)
        {
            try { await _wgVpnOutTask; }
            catch (OperationCanceledException) { }
        }
        _wgVpnOutTask = null;

        if (_fullRouteEnabled)
            try { SetFullRouteEnabled(false); } catch (Exception ex) { Logger.Warning($"Disable full-route failed: {ex.Message}"); }

        // Clean up host routes we added so we don't pollute the system routing table.
        try { RemoveAllHostRoutes(); } catch (Exception ex) { Logger.Warning($"RemoveAllHostRoutes failed: {ex.Message}"); }

        // Clear stale per-session state so it doesn't affect the next connection.
        _natTable.Clear();
        _pidNameCache.Clear();
        _pidParentCache.Clear();
        _pidTargetOwnerCache.Clear();
        _ipToProcess.Clear();
        _ipRefCount.Clear();
        _flowOwnerByTuple.Clear();
        _dnsPortOwners.Clear();
        _dnsPidOwners.Clear();
        _loggedMatchIps.Clear();
        _loggedExcludedIps.Clear();
        _recentLeakByDst.Clear();
        // Note: do NOT clear _excludedEntries (user-set list) or _targetExecutables.
        // Auto-added DNS-server excludes are mixed into _excludedIps; rebuild the
        // _excludedIps map from _excludedEntries only, so DNS auto-excludes from
        // this session are dropped (they will be re-detected on next Start()).
        _excludedIps.Clear();
        foreach (var ips in _excludedEntries.Values)
            foreach (var nbo in ips)
                _excludedIps[nbo] = true;

        // Reset diagnostic counters that should not span sessions.
        Interlocked.Exchange(ref _statTotalCaptured, 0);
        Interlocked.Exchange(ref _statRedirected, 0);
        Interlocked.Exchange(ref _statPassthrough, 0);
        Interlocked.Exchange(ref _statSendFailed, 0);
        Interlocked.Exchange(ref _statInboundCaptured, 0);
        Interlocked.Exchange(ref _statInboundNatMatched, 0);
        Interlocked.Exchange(ref _statInboundSendFailed, 0);
        Interlocked.Exchange(ref _statNetOutRewritten, 0);
        Interlocked.Exchange(ref _statWgVpnOutRewritten, 0);
        Interlocked.Exchange(ref _statWgVpnOutSeen, 0);
        Interlocked.Exchange(ref _statWgDnsRedirect, 0);
        Interlocked.Exchange(ref _statWgQuicDropped, 0);
        Interlocked.Exchange(ref _statNetInRewritten, 0);
        Interlocked.Exchange(ref _statNetOutPassthrough, 0);
        Interlocked.Exchange(ref _statNetOutSendFailed, 0);
        Interlocked.Exchange(ref _statVpnEgressSniffed, 0);
        Interlocked.Exchange(ref _statVpnEgressFromOurIp, 0);
        Interlocked.Exchange(ref _statVpnIngressSniffed, 0);
        Interlocked.Exchange(ref _statVpnIngressToOurIp, 0);
        Interlocked.Exchange(ref _statLeakConfirmed, 0);
        Interlocked.Exchange(ref _statLeakBlocked, 0);
        Interlocked.Exchange(ref _statLeakBlockedRecovered, 0);
        Interlocked.Exchange(ref _statLeakBlockedSuppressed, 0);
        Interlocked.Exchange(ref _policyTransitionGraceUntilTick, 0);
        if (resetCounters)
            ResetLiveTrafficCounters();
        Interlocked.Exchange(ref _statRoutesAdded, 0);
        Interlocked.Exchange(ref _statRoutesFailed, 0);
        Interlocked.Exchange(ref _statFlowEstablished, 0);
        Interlocked.Exchange(ref _statFlowTargetMatched, 0);
        Interlocked.Exchange(ref _statFlowDeleted, 0);
        Interlocked.Exchange(ref _statFlowExcluded, 0);
        Interlocked.Exchange(ref _statFlowIPv6Blocked, 0);
        Interlocked.Exchange(ref _diagTick, 0);
        Interlocked.Exchange(ref _statsReportTick, 0);
        _lastStatsHealthSignature = "";
        _fullRouteEnabled = false;
        _inboundRewriteCount = 0;
        _redirectCount = 0;

        Logger.Info($"TrafficRouter stopped. Final: routes={Interlocked.Read(ref _statRoutesAdded)} netRewrites={Interlocked.Read(ref _statNetOutRewritten)} natEntries={_natTable.Count}");
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Synchronous disposal: makes a best-effort cleanup if the service is
    /// still running.  Calling Dispose without first awaiting StopAsync is
    /// supported but will block briefly while WinDivert handles are closed
    /// and background loops drain.  Idempotent.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (_isRunning)
                StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[DISPOSE] StopAsync during Dispose threw: {ex.Message}");
        }
        GC.SuppressFinalize(this);
    }

    private void CloseHandles()
    {
        lock (_handleLock)
        {
            int closed = 0;
            if (_outboundHandle != IntPtr.Zero && _outboundHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_outboundHandle);
                _outboundHandle = IntPtr.Zero;
                closed++;
            }
            if (_inboundHandle != IntPtr.Zero && _inboundHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_inboundHandle);
                _inboundHandle = IntPtr.Zero;
                closed++;
            }
            if (_vpnSniffHandle != IntPtr.Zero && _vpnSniffHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_vpnSniffHandle);
                _vpnSniffHandle = IntPtr.Zero;
                closed++;
            }
            if (_physSniffHandle != IntPtr.Zero && _physSniffHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_physSniffHandle);
                _physSniffHandle = IntPtr.Zero;
                closed++;
            }
            if (_directSniffHandle != IntPtr.Zero && _directSniffHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_directSniffHandle);
                _directSniffHandle = IntPtr.Zero;
                closed++;
            }
            if (_vpnIngressHandle != IntPtr.Zero && _vpnIngressHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_vpnIngressHandle);
                _vpnIngressHandle = IntPtr.Zero;
                closed++;
            }
            if (_ipv6BlockHandle != IntPtr.Zero && _ipv6BlockHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_ipv6BlockHandle);
                _ipv6BlockHandle = IntPtr.Zero;
                closed++;
            }
            if (_networkOutHandle != IntPtr.Zero && _networkOutHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_networkOutHandle);
                _networkOutHandle = IntPtr.Zero;
                closed++;
            }
            if (_networkInHandle != IntPtr.Zero && _networkInHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_networkInHandle);
                _networkInHandle = IntPtr.Zero;
                closed++;
            }
            if (_wgVpnOutHandle != IntPtr.Zero && _wgVpnOutHandle != new IntPtr(-1))
            {
                WinDivertNative.WinDivertClose(_wgVpnOutHandle);
                _wgVpnOutHandle = IntPtr.Zero;
                closed++;
            }
            Logger.Info($"[HANDLE] Closed {closed} WinDivert handles");
        }
    }

    internal class TrafficCounter
    {
        public long BytesSent;
        public long BytesReceived;
    }

    /// <summary>
    /// NAT table entry: remembers the original physical source IP and interface
    /// for a (protocol, srcPort) connection so replies can be reverse-translated.
    /// </summary>
    internal class NatEntry
    {
        public byte[] OriginalSrcIp = new byte[4];
        public uint PhysicalIfIdx;
        public string ProcessName = "";
        public DateTime LastSeen;
        // Set when this entry was created by a DNS-redirect rewrite.
        // On inbound response the source IP is spoofed back to DnsOrigDstIp so the
        // application believes the answer came from its configured DNS server.
        public bool IsDnsRedirect;
        public byte[]? DnsOrigDstIp;
    }

    private static string GetWinDivertErrorMessage(int error)
    {
        return error switch
        {
            2 => "WinDivert driver not found (WinDivert64.sys missing)",
            5 => "Access denied - run as Administrator",
            87 => "Invalid filter syntax",
            577 => "Driver blocked by security policy",
            654 => "WinDivert driver version mismatch",
            1060 => "WinDivert service not installed",
            1275 => "Driver blocked by Windows - disable driver signature enforcement",
            _ => $"Unknown error {error}"
        };
    }

    private static bool IsVirtualNicName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("TunnelX", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("TUN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetNicProperties(
        System.Net.NetworkInformation.NetworkInterface nic,
        out System.Net.NetworkInformation.IPInterfaceProperties? props)
    {
        props = null;
        try
        {
            var task = Task.Run(() => nic.GetIPProperties());
            if (!task.Wait(TimeSpan.FromMilliseconds(400)))
                return false;
            props = task.Result;
            return props != null;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBootstrapPhysicalNicFast(int vpnInterfaceIndex)
    {
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var type = n.NetworkInterfaceType;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (IsVirtualNicName(n.Name)) continue;

                var name = n.Name ?? "";
                var isLikelyPhysical = type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ||
                                       type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                                       name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);
                if (!isLikelyPhysical) continue;

                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = n.GetIPProperties(); }
                catch { continue; }

                var ifIdx = props.GetIPv4Properties()?.Index ?? -1;
                if (ifIdx <= 0 || ifIdx == vpnInterfaceIndex) continue;

                var gw = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                         !g.Address.Equals(IPAddress.Any) &&
                                         !g.Address.Equals(IPAddress.Parse("0.0.0.0")));
                if (gw == null) continue;

                var physIp = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(a.Address))
                    ?.Address;
                if (physIp == null) continue;

                _physicalNicId = n.Id;
                _physicalInterfaceIndex = ifIdx;
                _physicalGatewayIp = gw.Address.ToString();
                _physicalLocalIpBytes = physIp.GetAddressBytes();
                var nicStats = n.GetIPStatistics();
                _baselinePhysBytesSent = nicStats.BytesSent;
                _baselinePhysBytesReceived = nicStats.BytesReceived;
                Logger.Info($"[NIC-BASELINE] Fast physical NIC '{n.Name}' ifIdx={ifIdx} gw={_physicalGatewayIp} baseline: sent={_baselinePhysBytesSent} recv={_baselinePhysBytesReceived}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[NIC-BASELINE] Fast bootstrap failed: {ex.Message}");
        }

        return false;
    }

    private void TryBootstrapVpnNicBaseline(int vpnInterfaceIndex)
    {
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = n.GetIPProperties(); }
                catch { continue; }

                var ifIdx = props.GetIPv4Properties()?.Index ?? -1;
                if (ifIdx != vpnInterfaceIndex)
                    continue;

                var nicStats = n.GetIPStatistics();
                _vpnNicId = n.Id;
                _baselineVpnBytesSent = nicStats.BytesSent;
                _baselineVpnBytesReceived = nicStats.BytesReceived;
                Logger.Info($"[VPN-BASELINE] VPN NIC '{n.Name}' ifIdx={ifIdx} baseline: sent={_baselineVpnBytesSent} recv={_baselineVpnBytesReceived}");
                return;
            }

            _vpnNicId = null;
            _baselineVpnBytesSent = 0;
            _baselineVpnBytesReceived = 0;
            Logger.Warning($"[VPN-BASELINE] No VPN NIC baseline found for ifIdx={vpnInterfaceIndex}; using packet sniff counters only.");
        }
        catch (Exception ex)
        {
            _vpnNicId = null;
            _baselineVpnBytesSent = 0;
            _baselineVpnBytesReceived = 0;
            Logger.Warning($"[VPN-BASELINE] Failed: {ex.Message}");
        }
    }

    private void DiscoverPhysicalNicBaseline(int vpnInterfaceIndex)
    {
        try
        {
            var ranked = new List<(System.Net.NetworkInformation.NetworkInterface Nic, int Score)>();
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var type = n.NetworkInterfaceType;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (type == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (IsVirtualNicName(n.Name)) continue;

                var score = type switch
                {
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => 100,
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet => 90,
                    _ => 10
                };
                var name = n.Name ?? "";
                if (name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("WLAN", StringComparison.OrdinalIgnoreCase))
                    score += 30;
                ranked.Add((n, score));
            }

            ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var (candidate, _) in ranked.Take(6))
            {
                if (!TryGetNicProperties(candidate, out var props) || props == null) continue;
                int ifIdx;
                try { ifIdx = props.GetIPv4Properties()?.Index ?? -1; }
                catch { continue; }
                if (ifIdx <= 0 || ifIdx == vpnInterfaceIndex) continue;

                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any) &&
                    !g.Address.Equals(IPAddress.Parse("0.0.0.0")));
                if (!hasGateway) continue;

                var physIp = props.UnicastAddresses
                    .FirstOrDefault(a =>
                        a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address))
                    ?.Address;
                if (physIp == null) continue;

                _physicalNicId = candidate.Id;
                _physicalInterfaceIndex = ifIdx;
                _physicalGatewayIp = props.GatewayAddresses
                    .First(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Address.ToString();
                _physicalLocalIpBytes = physIp.GetAddressBytes();
                var nicStats = candidate.GetIPStatistics();
                _baselinePhysBytesSent = nicStats.BytesSent;
                _baselinePhysBytesReceived = nicStats.BytesReceived;
                Logger.Info($"[NIC-BASELINE] Physical NIC '{candidate.Name}' ifIdx={_physicalInterfaceIndex} gw={_physicalGatewayIp} baseline: sent={_baselinePhysBytesSent} recv={_baselinePhysBytesReceived}");
                return;
            }

            if (_physicalInterfaceIndex > 0)
            {
                Logger.Info($"[NIC-BASELINE] Deferred scan kept existing physical NIC ifIdx={_physicalInterfaceIndex} gw={_physicalGatewayIp}");
                return;
            }

            Logger.Warning("[NIC-BASELINE] No suitable physical NIC found — DirectTraffic counter will be unavailable.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[NIC-BASELINE] Failed: {ex.Message}");
        }
    }
}
