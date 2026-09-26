using System.Net;

namespace AppTunnel.Services;

/// <summary>
/// Windows decides "Connected" vs "Connected, no internet" from NCSI probes
/// (<c>dns.msftncsi.com</c> must answer 131.107.255.255, and
/// <c>http://www.msftconnecttest.com/connecttest.txt</c> must return
/// "Microsoft Connect Test"). In per-app mode those probes belong on the
/// physical NIC. A /32 learned for a tunneled app, or a DNS redirect, sends
/// them into the tunnel where they time out; Windows then drops Wi-Fi the
/// same way it did when those hosts were unreachable.
/// </summary>
internal static class WindowsConnectivityGuard
{
    private static readonly string[] ProbeSuffixes =
    [
        "msftconnecttest.com",
        "msftncsi.com"
    ];

    // Published NCSI addresses. Kept in the same byte order as the rest of
    // the router (BitConverter over IPAddress.GetAddressBytes).
    private static readonly uint ConnectTestIpv4 = ToNetworkOrder("13.107.4.52");
    private static readonly uint DnsProbeAnswerIpv4 = ToNetworkOrder("131.107.255.255");

    public static bool IsProbeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var suffix in ProbeSuffixes)
        {
            if (host.Equals(suffix, StringComparison.Ordinal) ||
                host.EndsWith("." + suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool IsProbeIpv4(uint destinationNetworkOrder)
        => destinationNetworkOrder == ConnectTestIpv4 ||
           destinationNetworkOrder == DnsProbeAnswerIpv4;

    private static uint ToNetworkOrder(string ip)
        => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
}
