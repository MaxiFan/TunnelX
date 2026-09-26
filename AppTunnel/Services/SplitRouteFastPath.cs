namespace AppTunnel.Services;

/// <summary>
/// Decides what the per-app outbound fast path should do when a /32 already
/// exists. Route add/delete must not run on the packet thread: <c>route.exe</c>
/// holds the Windows routing lock, and a long session of shared CDN addresses
/// (tunneled app + system/NCSI) otherwise stalls every outbound packet until
/// Windows reports the Wi-Fi as "Connected, no internet".
/// </summary>
internal static class SplitRouteFastPath
{
    internal readonly record struct Decision(
        bool SendViaVpn,
        bool RemoveRouteNow,
        bool ScheduleDelayedRemovalIfIdle);

    public static Decision Decide(
        bool fullRoute,
        bool probeIp,
        bool excluded,
        bool included,
        bool blockedProc,
        bool processIsTarget,
        int ipRefCount)
    {
        // Split mode: never keep a host route that would steer NCSI into the TUN.
        if (!fullRoute && probeIp)
            return new Decision(false, true, false);

        if (excluded || blockedProc)
            return new Decision(false, true, false);

        if (fullRoute || included || processIsTarget)
            return new Decision(true, false, false);

        // Another tunneled flow still owns this IP. Reinject this packet on the
        // physical NIC, but leave the /32 alone so we don't churn the route table.
        if (ipRefCount > 0)
            return new Decision(false, false, false);

        // No live flow. One delayed removal, not a synchronous delete per packet.
        return new Decision(false, false, true);
    }
}
