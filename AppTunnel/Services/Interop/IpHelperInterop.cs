using System.Runtime.InteropServices;
using System.Text;

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
    public const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    public static extern int CreateIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [DllImport("iphlpapi.dll")]
    public static extern int DeleteIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [DllImport("iphlpapi.dll")]
    public static extern int SetIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

    public const ushort AfInet = 2;

    [DllImport("iphlpapi.dll")]
    public static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern uint DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    public static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint ConvertInterfaceLuidToAlias(
        ref ulong interfaceLuid,
        StringBuilder interfaceAlias,
        nuint length);
}
