using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

/// <summary>
/// IPv4 default-route (0.0.0.0/0) inspection via GetIpForwardTable.
/// Used to detect a silent full tunnel on the VPN adapter without spawning route.exe.
/// </summary>
internal static class VpnDefaultRouteInspector
{
    public readonly record struct Ipv4RouteRow(
        uint Dest,
        uint Mask,
        uint NextHop,
        uint IfIndex,
        uint Metric,
        uint Proto);

    public static bool IsIpv4DefaultRoute(uint dest, uint mask) => dest == 0 && mask == 0;

    public static IReadOnlyList<Ipv4RouteRow> FilterDefaultRoutes(IEnumerable<Ipv4RouteRow> rows)
        => rows.Where(r => IsIpv4DefaultRoute(r.Dest, r.Mask)).ToList();

    public static IReadOnlyList<Ipv4RouteRow> OnInterface(IEnumerable<Ipv4RouteRow> rows, int ifIndex)
        => rows.Where(r => r.IfIndex == (uint)ifIndex).ToList();

    public static string FormatNextHop(uint nextHopNbo)
        => new IPAddress(BitConverter.GetBytes(nextHopNbo)).ToString();

    public static string FormatSummary(IReadOnlyList<Ipv4RouteRow> defaults, int vpnIfIndex)
    {
        var vpnCount = defaults.Count(r => r.IfIndex == (uint)vpnIfIndex);
        var parts = defaults.Select(r =>
            $"if={r.IfIndex} nh={FormatNextHop(r.NextHop)} metric={r.Metric} proto={r.Proto}" +
            (r.IfIndex == (uint)vpnIfIndex ? " vpn" : " phys"));
        return $"defaultGateways={defaults.Count} vpnIf={vpnIfIndex} vpnIfDefault={vpnCount} [{string.Join("; ", parts)}]";
    }

    public static IReadOnlyList<Ipv4RouteRow> ReadIpv4ForwardTable()
    {
        var size = 0;
        var status = IpHelperNative.GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (status != IpHelperNative.ErrorInsufficientBuffer || size <= 0)
            return Array.Empty<Ipv4RouteRow>();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = IpHelperNative.GetIpForwardTable(buffer, ref size, false);
            if (status != 0)
                return Array.Empty<Ipv4RouteRow>();

            var count = Marshal.ReadInt32(buffer);
            if (count <= 0)
                return Array.Empty<Ipv4RouteRow>();

            var rowSize = Marshal.SizeOf<MIB_IPFORWARDROW>();
            var rows = new List<Ipv4RouteRow>(count);
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<MIB_IPFORWARDROW>(IntPtr.Add(buffer, offset));
                rows.Add(new Ipv4RouteRow(
                    native.dwForwardDest,
                    native.dwForwardMask,
                    native.dwForwardNextHop,
                    native.dwForwardIfIndex,
                    native.dwForwardMetric1,
                    native.dwForwardProto));
                offset += rowSize;
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static IReadOnlyList<Ipv4RouteRow> ReadDefaultRoutes()
        => FilterDefaultRoutes(ReadIpv4ForwardTable());

    public static int DeleteDefaultRoutesOnInterface(int ifIndex)
    {
        if (ifIndex <= 0)
            return 0;

        var deleted = 0;
        foreach (var row in ReadDefaultRoutes())
        {
            if (row.IfIndex != (uint)ifIndex)
                continue;

            var native = new MIB_IPFORWARDROW
            {
                dwForwardDest = 0,
                dwForwardMask = 0,
                dwForwardNextHop = row.NextHop,
                dwForwardIfIndex = row.IfIndex,
                dwForwardType = 3,
                dwForwardProto = row.Proto,
                dwForwardMetric1 = row.Metric
            };
            try
            {
                if (IpHelperNative.DeleteIpForwardEntry(ref native) == 0)
                    deleted++;
            }
            catch
            {
                // Best-effort; TrafficRouter falls back to metric/proto sweeps and route.exe.
            }
        }

        return deleted;
    }
}
