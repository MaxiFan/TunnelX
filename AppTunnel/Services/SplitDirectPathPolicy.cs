namespace AppTunnel.Services;

/// <summary>
/// Split-mode decisions that keep unselected apps on the physical path.
/// A VPN /32 is a system-wide route. WinDivert cannot choose the outbound
/// interface (IfIdx is ignored on inject), so a non-target packet that still
/// has its physical source is delivered to the VPN adapter and dropped by
/// Windows' strong-host model. That is why unselected apps, including browser
/// DNS and Google search, look offline while a selected app is tunneled.
/// </summary>
internal static class SplitDirectPathPolicy
{
    /// <summary>
    /// Route metric passed to <c>route.exe</c>. Sockets bound to the VPN
    /// address with IP_UNICAST_IF (selected-app TCP, UDP, and DNS) need a
    /// default on that adapter. It must lose to the physical default route
    /// or split mode becomes a full tunnel.
    /// </summary>
    public const uint RelayInterfaceRouteMetric = 9000;

    /// <summary>
    /// Highest automatic interface metric Windows assigns from link speed
    /// (KB299540). Slower links stop at 50.
    /// </summary>
    public const uint MaxAutomaticInterfaceMetric = 50;

    /// <summary>
    /// Lowest <c>GetIpForwardTable</c> metric that is still the relay route.
    /// On Vista and later, <c>dwForwardMetric1</c> is the route metric plus
    /// the interface metric, so <c>route add METRIC 9000</c> is observed as
    /// 9000 or higher. Some <c>route.exe</c> builds store
    /// <c>9000 - interface metric</c> instead; automatic interface metrics
    /// stay within <see cref="MaxAutomaticInterfaceMetric"/> of 9000.
    /// Anything in this band loses to a normal physical default. The #60
    /// guard must not delete it. 9025 (interface metric 25) is the same
    /// relay route, not a second capturing default.
    /// </summary>
    public const uint RelayMetricFloor = RelayInterfaceRouteMetric - MaxAutomaticInterfaceMetric;

    public readonly record struct OutboundAction(
        bool RelaySelectedApp,
        bool RemoveHostRoute,
        bool RewriteOntoExistingTunnel);

    /// <summary>
    /// What the split-mode default-route guard does with the 0.0.0.0/0
    /// metrics installed on the VPN interface.
    /// </summary>
    public readonly record struct VpnDefaultGuardPlan(uint[] StripMetrics, uint[] KeptRelayMetrics)
    {
        public bool RelaySurvives => KeptRelayMetrics.Length > 0;
    }

    /// <summary>
    /// Metric <c>GetIpForwardTable</c> reports when the relay was requested
    /// as <see cref="RelayInterfaceRouteMetric"/> and Windows added
    /// <paramref name="interfaceMetric"/>.
    /// </summary>
    public static uint ObservedRelayMetric(uint interfaceMetric)
        => RelayInterfaceRouteMetric + interfaceMetric;

    public static bool IsRelayInterfaceRoute(uint metric)
        => metric >= RelayMetricFloor;

    /// <summary>
    /// VPN default that would win over a normal physical default. OpenVPN
    /// IPCP, WireGuard, and TAP/Wintun re-add these (metric 0, 1, 25, …).
    /// The relay route is not one of them.
    /// </summary>
    public static bool IsCapturingDefaultMetric(uint metric)
        => !IsRelayInterfaceRoute(metric);

    /// <summary>
    /// Splits VPN-interface default metrics into routes the #60 guard
    /// deletes and relay routes selected-app sockets still need.
    /// </summary>
    public static VpnDefaultGuardPlan ClassifyVpnDefaults(IEnumerable<uint> vpnDefaultMetrics)
    {
        var metrics = vpnDefaultMetrics as uint[] ?? vpnDefaultMetrics.ToArray();
        return new VpnDefaultGuardPlan(
            metrics.Where(IsCapturingDefaultMetric).ToArray(),
            metrics.Where(IsRelayInterfaceRoute).ToArray());
    }

    /// <summary>
    /// True when split mode must install the relay route. Reinstalling a
    /// route the guard just deleted is the v2.2.3 loop.
    /// </summary>
    public static bool ShouldReinstallRelayRoute(IEnumerable<uint> vpnDefaultMetrics)
        => !vpnDefaultMetrics.Any(IsRelayInterfaceRoute);

    /// <summary>
    /// Selected-app TCP (6), UDP (17), and DNS (either protocol, port 53)
    /// dial from a socket bound to the VPN address. That dial has a route
    /// when the relay default is still installed after the guard runs.
    /// Full-route keeps its own capturing default and does not run the guard.
    /// </summary>
    public static bool ForwardedFlowHasVpnRoute(bool fullRoute, IEnumerable<uint> vpnDefaultMetrics)
    {
        if (fullRoute)
            return vpnDefaultMetrics.Any();

        return ClassifyVpnDefaults(vpnDefaultMetrics).RelaySurvives;
    }

    public static bool TransportUsesRelaySocket(byte ipProtocol)
        => ipProtocol is 6 or 17;

    /// <summary>
    /// Host routes are only for modes that are supposed to capture every
    /// process (full route, or an include rule). Per-app destinations must
    /// not get one.
    /// </summary>
    public static bool ShouldInstallCapturingHostRoute(bool fullRoute, bool includedDestination)
        => fullRoute || includedDestination;

    public static OutboundAction Decide(
        bool fullRoute,
        bool included,
        bool excluded,
        bool probeIp,
        bool blockedProc,
        bool processIsTarget,
        bool forwarderReady,
        int targetFlowRefCount)
    {
        if (excluded || blockedProc)
            return new OutboundAction(false, true, false);

        if (!fullRoute && probeIp)
            return new OutboundAction(false, true, false);

        if (fullRoute || included)
            return new OutboundAction(false, false, true);

        if (processIsTarget && forwarderReady)
            return new OutboundAction(true, true, false);

        if (processIsTarget)
            return new OutboundAction(false, false, true);

        // No selected flow still needs this /32. Delete it so the physical
        // default route is used. While a selected flow still depends on the
        // legacy /32 (forwarder unavailable), share the tunnel instead of
        // dropping the packet.
        if (forwarderReady || targetFlowRefCount <= 0)
            return new OutboundAction(false, true, false);

        return new OutboundAction(false, false, true);
    }
}
