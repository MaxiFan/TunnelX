using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AppTunnel.Services;

internal static class TunnelPerformanceTuner
{
    public const int MinTunMtu = 1240;
    public const int MaxTunMtu = 1500;
    public const int DefaultTunMtu = 1420;

    public static int ClampTunMtu(int mtu)
        => Math.Max(MinTunMtu, Math.Min(MaxTunMtu, mtu));

    /// <summary>
    /// Picks a practical TUN MTU based on primary NIC MTU and optional DF probe.
    /// DF probe is best-effort only; if ICMP is blocked, the method still returns
    /// a safe value derived from interface MTU.
    /// </summary>
    public static async Task<int> GetRecommendedTunMtuAsync(
        string serverHostOrIp,
        bool highOverheadTransport,
        CancellationToken ct)
    {
        int overhead = highOverheadTransport ? 110 : 80;
        int baseMtu = GetPrimaryInterfaceMtuOrDefault();
        int candidate = ClampTunMtu(baseMtu - overhead);

        // If we cannot resolve a probe target, return the interface-derived value.
        var probeIp = await DnsResolverCache.ResolveFirstIpv4Async(serverHostOrIp, ct);
        if (probeIp == null)
            return candidate;

        // Probe descending candidates with DF enabled.
        // We keep this short to avoid slowing connect.
        foreach (var mtu in BuildProbeCandidates(candidate))
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeMtuAsync(probeIp, mtu);
            if (probe == MtuProbeResult.Success)
                return mtu;

            // When ICMP is blocked or route is unreachable, extra probe steps
            // rarely add signal and only slow connection startup.
            if (probe is MtuProbeResult.TimeoutOrFiltered or MtuProbeResult.Unreachable)
                break;
        }

        return candidate;
    }

    private static IEnumerable<int> BuildProbeCandidates(int candidate)
    {
        yield return candidate;
        yield return ClampTunMtu(candidate - 20);
        yield return ClampTunMtu(candidate - 40);
        yield return ClampTunMtu(candidate - 60);
        yield return ClampTunMtu(candidate - 80);
    }

    private enum MtuProbeResult
    {
        Success,
        TooBig,
        TimeoutOrFiltered,
        Unreachable
    }

    private static async Task<MtuProbeResult> ProbeMtuAsync(IPAddress target, int mtu)
    {
        // IPv4 header (20) + ICMP header (8) = 28 bytes overhead
        int payloadSize = Math.Max(0, mtu - 28);
        var payload = new byte[payloadSize];
        var options = new PingOptions(64, true);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 700, payload, options);
            return reply.Status switch
            {
                IPStatus.Success => MtuProbeResult.Success,
                IPStatus.PacketTooBig => MtuProbeResult.TooBig,
                IPStatus.TimedOut => MtuProbeResult.TimeoutOrFiltered,
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable => MtuProbeResult.Unreachable,
                _ => MtuProbeResult.TimeoutOrFiltered
            };
        }
        catch
        {
            return MtuProbeResult.TimeoutOrFiltered;
        }
    }

    private static int GetPrimaryInterfaceMtuOrDefault()
    {
        try
        {
            var primary = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n =>
                {
                    try
                    {
                        var props = n.GetIPProperties();
                        var v4 = props.GetIPv4Properties();
                        bool hasGateway = props.GatewayAddresses.Any(g =>
                            g.Address.AddressFamily == AddressFamily.InterNetwork);
                        return new { Nic = n, V4 = v4, HasGateway = hasGateway };
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x != null && x.V4 != null && x.HasGateway)
                .OrderBy(x => x!.V4!.Index)
                .FirstOrDefault();

            if (primary?.V4?.Mtu is int mtu && mtu >= 1300 && mtu <= 9000)
                return mtu;
        }
        catch { }

        return DefaultTunMtu + 80; // so derived candidate stays near 1420
    }
}
