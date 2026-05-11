using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AppTunnel.Services;

/// <summary>
/// Caches the mapping of (localPort, protocol) → process name.
/// Uses GetExtendedTcpTable and GetExtendedUdpTable Windows APIs.
/// </summary>
internal class ConnectionProcessCache
{
    private readonly Dictionary<(ushort port, byte protocol), string> _cache = new();
    private Dictionary<int, string> _pidNameCache = new();
    private int _refreshCount;

    public void Refresh()
    {
        _cache.Clear();

        // Clear PID→name cache every 3 refreshes (~1.5s) to handle PID reuse
        if (++_refreshCount % 3 == 0)
            _pidNameCache = new Dictionary<int, string>();

        RefreshTcp();
        RefreshUdp();
    }

    public string? GetProcessName(ConnectionTuple tuple)
    {
        var key = (tuple.LocalPort, tuple.Protocol);
        return _cache.GetValueOrDefault(key);
    }

    public string? GetProcessNameByLocalPort(ushort localPort, byte protocol)
    {
        var key = (localPort, protocol);
        return _cache.GetValueOrDefault(key);
    }

    private void RefreshTcp()
    {
        int size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false,
            2 /* AF_INET */, TcpTableClass.OwnerPidAll, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, false,
                2, TcpTableClass.OwnerPidAll, 0) != 0) return;

            int count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var offset = Marshal.SizeOf<int>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + offset + i * rowSize);
                ushort localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.localPort);
                var processName = GetProcessName(row.owningPid);
                if (processName != null)
                    _cache[(localPort, 6)] = processName;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RefreshUdp()
    {
        int size = 0;
        NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, false,
            2 /* AF_INET */, UdpTableClass.OwnerPid, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedUdpTable(buffer, ref size, false,
                2, UdpTableClass.OwnerPid, 0) != 0) return;

            int count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            var offset = Marshal.SizeOf<int>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(buffer + offset + i * rowSize);
                ushort localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.localPort);
                var processName = GetProcessName(row.owningPid);
                if (processName != null)
                    _cache[(localPort, 17)] = processName;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string? GetProcessName(int pid)
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
        catch
        {
            return null;
        }
    }
}
