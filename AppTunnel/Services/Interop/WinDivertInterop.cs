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

    public byte   Event    => (byte)((LayerEventFlags >> 8) & 0xFF);
    public bool   IsIPv6   => ((LayerEventFlags >> 20) & 0x1) != 0;
    public bool   Outbound => ((LayerEventFlags >> 17) & 0x1) != 0;
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
