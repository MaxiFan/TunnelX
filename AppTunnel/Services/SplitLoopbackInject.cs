namespace AppTunnel.Services;

/// <summary>
/// WinDivert flags for the per-app redirect. A packet captured on the way to
/// a public address and rewritten to 127.0.0.1 is not a loopback packet until
/// these bits say so. Without them the stack drops the inject, the forwarder
/// never accepts, and rewriteOut is the only evidence.
/// </summary>
internal static class SplitLoopbackInject
{
    public static void PrepareOutboundRedirect(ref WinDivertAddress addr)
    {
        addr.ClearChecksumFlags();
        addr.Outbound = true;
        addr.Loopback = true;
        addr.Impostor = true;
    }

    /// <summary>
    /// The reply has been rewritten to the server the app dialed. Inject it
    /// inbound on the interface that owned the original socket, and mark it
    /// impostor so NET-IN does not capture the restored UDP/53 source port.
    /// </summary>
    public static void PrepareReturn(ref WinDivertAddress addr, uint physicalIfIdx)
    {
        addr.ClearChecksumFlags();
        addr.Outbound = false;
        addr.Impostor = true;
        if (physicalIfIdx > 0)
        {
            addr.Loopback = false;
            addr.IfIdx = physicalIfIdx;
            addr.SubIfIdx = 0;
        }
        else
        {
            addr.Loopback = true;
        }
    }
}
