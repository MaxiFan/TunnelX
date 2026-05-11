using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

/// <summary>
/// IPv6 connection table cache for mapping (localPort, protocol) → process name.
/// Uses AF_INET6 (23) variant of GetExtendedTcpTable / GetExtendedUdpTable.
/// </summary>
internal class ConnectionProcessCacheV6
{
    private readonly Dictionary<(ushort port, byte protocol), string> _cache = new();
    private Dictionary<int, string> _pidNameCache = new();
    private int _refreshCount;

    public void Refresh()
    {
        _cache.Clear();
        if (++_refreshCount % 3 == 0)
            _pidNameCache = new Dictionary<int, string>();
        RefreshTcp6();
        RefreshUdp6();
    }

    public string? GetProcessName(ushort localPort, byte protocol)
    {
        return _cache.GetValueOrDefault((localPort, protocol));
    }

    private void RefreshTcp6()
    {
        int size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false,
            23 /* AF_INET6 */, TcpTableClass.OwnerPidAll, 0);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, false,
                23, TcpTableClass.OwnerPidAll, 0) != 0) return;

            int count = Marshal.ReadInt32(buffer);
            // MIB_TCP6ROW_OWNER_PID: 16(localAddr) + 4(scopeId) + 4(localPort)
            //   + 16(remoteAddr) + 4(scopeId) + 4(remotePort) + 4(state) + 4(pid) = 56 bytes
            int rowSize = 56;
            int offset = Marshal.SizeOf<int>();

            for (int i = 0; i < count; i++)
            {
                IntPtr rowPtr = buffer + offset + i * rowSize;
                int localPort = Marshal.ReadInt32(rowPtr, 20); // offset of localPort in MIB_TCP6ROW_OWNER_PID
                int pid = Marshal.ReadInt32(rowPtr, 52); // offset of owningPid
                ushort port = (ushort)IPAddress.NetworkToHostOrder((short)localPort);
                var name = GetProcessNameByPid(pid);
                if (name != null)
                    _cache[(port, 6)] = name;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void RefreshUdp6()
    {
        int size = 0;
        NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, false,
            23 /* AF_INET6 */, UdpTableClass.OwnerPid, 0);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buffer, ref size, false,
                23, UdpTableClass.OwnerPid, 0) != 0) return;

            int count = Marshal.ReadInt32(buffer);
            // MIB_UDP6ROW_OWNER_PID: 16(localAddr) + 4(scopeId) + 4(localPort) + 4(pid) = 28 bytes
            int rowSize = 28;
            int offset = Marshal.SizeOf<int>();

            for (int i = 0; i < count; i++)
            {
                IntPtr rowPtr = buffer + offset + i * rowSize;
                int localPort = Marshal.ReadInt32(rowPtr, 20); // offset of localPort
                int pid = Marshal.ReadInt32(rowPtr, 24); // offset of owningPid
                ushort port = (ushort)IPAddress.NetworkToHostOrder((short)localPort);
                var name = GetProcessNameByPid(pid);
                if (name != null)
                    _cache[(port, 17)] = name;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string? GetProcessNameByPid(int pid)
    {
        if (pid <= 0) return null;
        if (_pidNameCache.TryGetValue(pid, out var name)) return name;
        try
        {
            using var proc = Process.GetProcessById(pid);
            var exeName = proc.ProcessName + ".exe";
            _pidNameCache[pid] = exeName;
            return exeName;
        }
        catch { return null; }
    }
}
