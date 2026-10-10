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
    /// Default route installed only so sockets bound to the VPN interface
    /// with IP_UNICAST_IF have a route on that adapter. It must lose to the
    /// physical default route or split mode becomes a full tunnel.
    /// </summary>
    public const uint RelayInterfaceRouteMetric = 9000;

    public readonly record struct OutboundAction(
        bool RelaySelectedApp,
        bool RemoveHostRoute,
        bool RewriteOntoExistingTunnel);

    public static bool IsRelayInterfaceRoute(uint metric)
        => metric == RelayInterfaceRouteMetric;

    /// <summary>
    /// Any VPN default route other than the relay route captures the machine.
    /// A high metric that is not exactly <see cref="RelayInterfaceRouteMetric"/>
    /// is still unexpected and must be removed in split mode.
    /// </summary>
    public static bool IsCapturingDefaultMetric(uint metric)
        => !IsRelayInterfaceRoute(metric);

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
