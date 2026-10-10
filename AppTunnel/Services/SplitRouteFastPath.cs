namespace AppTunnel.Services;

/// <summary>
/// Decides what the per-app outbound fast path should do when a /32 already
/// exists. Deleting that route must not spawn <c>route.exe</c> per packet:
/// the Windows routing lock stalls NCSI/DHCP/DNS until Wi-Fi shows
/// "Connected, no internet". The IP-helper delete runs once per destination.
/// </summary>
internal static class SplitRouteFastPath
{
    internal readonly record struct Decision(
        bool SendViaVpn,
        bool RemoveRouteNow,
        bool ScheduleDelayedRemovalIfIdle,
        bool RelaySelectedApp);

    public static Decision Decide(
        bool fullRoute,
        bool probeIp,
        bool excluded,
        bool included,
        bool blockedProc,
        bool processIsTarget,
        int ipRefCount,
        bool forwarderReady = false)
    {
        var action = SplitDirectPathPolicy.Decide(
            fullRoute,
            included,
            excluded,
            probeIp,
            blockedProc,
            processIsTarget,
            forwarderReady,
            ipRefCount);

        return new Decision(
            action.RewriteOntoExistingTunnel,
            action.RemoveHostRoute,
            false,
            action.RelaySelectedApp);
    }
}
