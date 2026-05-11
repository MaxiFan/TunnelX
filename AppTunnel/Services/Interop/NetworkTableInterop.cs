using System.Runtime.InteropServices;

namespace AppTunnel.Services;

internal enum TcpTableClass
{
    BasicListener = 0,
    BasicConnections = 1,
    BasicAll = 2,
    OwnerPidListener = 3,
    OwnerPidConnections = 4,
    OwnerPidAll = 5
}

internal enum UdpTableClass
{
    Basic = 0,
    OwnerPid = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPROW_OWNER_PID
{
    public uint state;
    public uint localAddr;
    public int localPort;
    public uint remoteAddr;
    public int remotePort;
    public int owningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UDPROW_OWNER_PID
{
    public uint localAddr;
    public int localPort;
    public int owningPid;
}

internal static class NativeMethods
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
        bool bOrder, int ulAf, TcpTableClass tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
        bool bOrder, int ulAf, UdpTableClass tableClass, int reserved);
}
