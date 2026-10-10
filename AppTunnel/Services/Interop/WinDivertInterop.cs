using System.Runtime.InteropServices;

namespace AppTunnel.Services;

internal enum WinDivertLayer : uint
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4
}

/// <summary>
/// WinDivert 2.x WINDIVERT_ADDRESS — matches the native struct layout.
/// The Layer/Event/Flags are packed into a single uint32 as bitfields.
/// Total size is 80 bytes (includes the Network/Flow/Socket/Reflect union).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    [FieldOffset(0)]  public long Timestamp;        // INT64
    [FieldOffset(8)]  public ulong LayerEventFlags; // packed bitfield: layer(8) + event(8) + sniffed(1) + outbound(1) + ... (UINT64)

    // ---- Network layer (offsets 16..23) ----
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    // ---- Flow / Socket layer (overlaps Network via C union; offsets 16..72) ----
    [FieldOffset(16)] public ulong Flow_EndpointId;
    [FieldOffset(24)] public ulong Flow_ParentEndpointId;
    [FieldOffset(32)] public uint  Flow_ProcessId;
    // LocalAddr[4]  (IPv6-mapped for IPv4; last uint is the IPv4 in host byte order)
    [FieldOffset(36)] public uint  Flow_LocalAddr0;
    [FieldOffset(40)] public uint  Flow_LocalAddr1;
    [FieldOffset(44)] public uint  Flow_LocalAddr2;
    [FieldOffset(48)] public uint  Flow_LocalAddr3;
    // RemoteAddr[4]
    [FieldOffset(52)] public uint  Flow_RemoteAddr0;
    [FieldOffset(56)] public uint  Flow_RemoteAddr1;
    [FieldOffset(60)] public uint  Flow_RemoteAddr2;
    [FieldOffset(64)] public uint  Flow_RemoteAddr3;
    [FieldOffset(68)] public ushort Flow_LocalPort;
    [FieldOffset(70)] public ushort Flow_RemotePort;
    [FieldOffset(72)] public byte  Flow_Protocol;

    public byte Event => (byte)((LayerEventFlags >> 8) & 0xFF);
    public bool IsIPv6 => ReadFlag(20);

    /// <summary>
    /// Bit 17 of the WinDivert 2.x address flags. A setter is required when a
    /// rewritten reply must be injected inbound.
    /// </summary>
    public bool Outbound
    {
        get => ReadFlag(17);
        set => WriteFlag(17, value);
    }

    /// <summary>Bit 18. Required when a captured external packet is reinjected onto 127.0.0.1.</summary>
    public bool Loopback
    {
        get => ReadFlag(18);
        set => WriteFlag(18, value);
    }

    /// <summary>
    /// Bit 19. Injected packets with this set are not captured again, so
    /// NET-IN (udp.SrcPort == 53) cannot steal a restored DNS reply.
    /// </summary>
    public bool Impostor
    {
        get => ReadFlag(19);
        set => WriteFlag(19, value);
    }

    public bool IpChecksum => ReadFlag(21);
    public bool TcpChecksum => ReadFlag(22);
    public bool UdpChecksum => ReadFlag(23);

    /// <summary>
    /// WinDivertHelperCalcChecksums skips a checksum whose flag is already set.
    /// After rewriting an address, those bits still describe the original packet.
    /// </summary>
    public void ClearChecksumFlags()
    {
        LayerEventFlags &= ~((1UL << 21) | (1UL << 22) | (1UL << 23));
    }

    private bool ReadFlag(int bit) => ((LayerEventFlags >> bit) & 0x1) != 0;

    private void WriteFlag(int bit, bool value)
    {
        var mask = 1UL << bit;
        if (value)
            LayerEventFlags |= mask;
        else
            LayerEventFlags &= ~mask;
    }
}

internal static class WinDivertNative
{
    private const string DLL = "WinDivert.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        WinDivertLayer layer,
        short priority,
        ulong flags);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecv(
        IntPtr handle, byte[] pPacket, uint packetLen,
        ref uint pReadLen, ref WinDivertAddress pAddr);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSend(
        IntPtr handle, byte[] pPacket, uint packetLen,
        IntPtr pSendLen, // optional; pass IntPtr.Zero for NULL
        ref WinDivertAddress pAddr);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSetParam(IntPtr handle, uint param, ulong value);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertHelperCalcChecksums(
        byte[] pPacket, uint packetLen, ref WinDivertAddress pAddr, ulong flags);
}
