using System.Runtime.InteropServices;

namespace AppTunnel.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_IPFORWARDROW
{
    public uint dwForwardDest;
    public uint dwForwardMask;
    public uint dwForwardPolicy;
    public uint dwForwardNextHop;
    public uint dwForwardIfIndex;
    public uint dwForwardType;     // 3 = DIRECT (on-link), 4 = INDIRECT (via gw)
    public uint dwForwardProto;    // 3 = NETMGMT (user added)
    public uint dwForwardAge;
    public uint dwForwardNextHopAS;
    public uint dwForwardMetric1;
    public uint dwForwardMetric2;
    public uint dwForwardMetric3;
    public uint dwForwardMetric4;
    public uint dwForwardMetric5;
}

internal static class IpHelperNative
{
    [DllImport("iphlpapi.dll")]
    public static extern int CreateIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [DllImport("iphlpapi.dll")]
    public static extern int DeleteIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [DllImport("iphlpapi.dll")]
    public static extern int SetIpForwardEntry(ref MIB_IPFORWARDROW pRoute);
}
