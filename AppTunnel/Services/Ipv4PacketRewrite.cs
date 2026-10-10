namespace AppTunnel.Services;

/// <summary>
/// Rewrites the IPv4 destination or source of a TCP/UDP packet.
/// Checksums are left for WinDivertHelperCalcChecksums.
/// </summary>
internal static class Ipv4PacketRewrite
{
    internal static readonly byte[] LoopbackBytes = [127, 0, 0, 1];
    internal static readonly uint LoopbackNbo = BitConverter.ToUInt32(LoopbackBytes, 0);

    public static bool TryRewriteDestination(byte[] packet, int length, byte[] newAddress, ushort newPort)
        => TryRewrite(packet, length, addressOffset: 16, portOffset: 2, newAddress, newPort);

    public static bool TryRewriteSource(byte[] packet, int length, byte[] newAddress, ushort newPort)
        => TryRewrite(packet, length, addressOffset: 12, portOffset: 0, newAddress, newPort);

    private static bool TryRewrite(
        byte[] packet,
        int length,
        int addressOffset,
        int portOffset,
        byte[] newAddress,
        ushort newPort)
    {
        if (newAddress.Length != 4 || length < 20 || packet.Length < length)
            return false;
        if ((packet[0] >> 4) != 4)
            return false;

        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || length < ipHeaderLen + 4)
            return false;

        byte protocol = packet[9];
        if (protocol is not (6 or 17))
            return false;

        ushort frag = (ushort)((packet[6] << 8) | packet[7]);
        if ((frag & 0x3FFF) != 0)
            return false;

        Buffer.BlockCopy(newAddress, 0, packet, addressOffset, 4);
        int portAt = ipHeaderLen + portOffset;
        packet[portAt] = (byte)(newPort >> 8);
        packet[portAt + 1] = (byte)(newPort & 0xFF);
        return true;
    }

    /// <summary>
    /// Makes a forwarder reply look like the server the app connected to.
    /// When Windows delivered the redirected flow as loopback-to-loopback,
    /// the destination is restored to the app's real local address as well.
    /// </summary>
    public static bool TryRestoreSelectedAppReply(
        byte[] packet,
        int length,
        uint originalClientIp,
        uint originalDestIp,
        ushort originalDestPort)
    {
        if (length < 20 || packet.Length < length)
            return false;

        var dest = new byte[4];
        Buffer.BlockCopy(packet, 16, dest, 0, 4);
        int ipHeaderLen = (packet[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || length < ipHeaderLen + 4)
            return false;

        ushort destPort = (ushort)((packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3]);
        var server = BitConverter.GetBytes(originalDestIp);
        if (!TryRewriteSource(packet, length, server, originalDestPort))
            return false;

        bool destIsLoopback = dest[0] == 127 && dest[1] == 0 && dest[2] == 0 && dest[3] == 1;
        if (!destIsLoopback || originalClientIp == 0 || originalClientIp == LoopbackNbo)
            return true;

        var client = BitConverter.GetBytes(originalClientIp);
        return TryRewriteDestination(packet, length, client, destPort);
    }
}
