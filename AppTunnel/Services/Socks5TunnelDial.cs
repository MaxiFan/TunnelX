using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Dials through a local SOCKS5 inbound (sing-box/xray mixed proxy). No
/// authentication: the generated mixed inbound does not require it. UDP
/// associate carries QUIC and DNS, which HTTP CONNECT cannot.
/// </summary>
internal static class Socks5TunnelDial
{
    public static async Task<Socket> ConnectTcpAsync(
        IPEndPoint proxy,
        IPAddress destination,
        int port,
        CancellationToken ct,
        int timeoutMs = 12000)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("SOCKS5 dial only supports IPv4");
        if (port is <= 0 or > 65535)
            throw new InvalidOperationException("SOCKS5 dial port is invalid");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await socket.ConnectAsync(proxy, timeout.Token);
                await HandshakeAsync(socket, timeout.Token);
                await CommandAsync(socket, 0x01, destination, (ushort)port, timeout.Token);
                return socket;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"timeout after {timeoutMs}ms to {destination}:{port} via {proxy}");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<Socks5UdpChannel> AssociateUdpAsync(
        IPEndPoint proxy,
        CancellationToken ct,
        int timeoutMs = 8000)
    {
        var control = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Socket? udp = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await control.ConnectAsync(proxy, timeout.Token);
                await HandshakeAsync(control, timeout.Token);
                var (bindAddress, bindPort) = await CommandAsync(control, 0x03, IPAddress.Any, 0, timeout.Token);
                if (bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.None))
                    bindAddress = proxy.Address;
                if (bindPort == 0)
                    throw new InvalidOperationException("SOCKS5 UDP associate returned port 0");

                udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                return new Socks5UdpChannel(control, udp, new IPEndPoint(bindAddress, bindPort));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"timeout after {timeoutMs}ms opening SOCKS5 UDP associate via {proxy}");
            }
        }
        catch
        {
            try { udp?.Dispose(); } catch { }
            control.Dispose();
            throw;
        }
    }

    public static byte[] BuildConnectRequest(IPAddress ipv4, ushort port)
        => BuildCommand(0x01, ipv4, port);

    public static byte[] BuildUdpAssociateRequest()
        => BuildCommand(0x03, IPAddress.Any, 0);

    public static byte[] BuildUdpDatagram(IPAddress ipv4, ushort port, byte[] payload, int length)
    {
        if (ipv4.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("SOCKS5 UDP datagram only supports IPv4");
        if (length < 0 || length > payload.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        var packet = new byte[10 + length];
        packet[3] = 0x01;
        ipv4.GetAddressBytes().CopyTo(packet, 4);
        packet[8] = (byte)(port >> 8);
        packet[9] = (byte)(port & 0xFF);
        if (length > 0)
            Buffer.BlockCopy(payload, 0, packet, 10, length);
        return packet;
    }

    public static bool TryParseUdpDatagram(
        byte[] packet,
        int length,
        out IPAddress source,
        out ushort port,
        out int payloadOffset,
        out int payloadLength)
    {
        source = IPAddress.Any;
        port = 0;
        payloadOffset = 0;
        payloadLength = 0;
        if (length < 10 || packet.Length < length || packet[2] != 0)
            return false;

        int offset;
        switch (packet[3])
        {
            case 0x01:
                if (length < 10)
                    return false;
                source = new IPAddress(packet.AsSpan(4, 4));
                port = (ushort)((packet[8] << 8) | packet[9]);
                offset = 10;
                break;
            case 0x03:
                int nameLen = packet[4];
                offset = 5 + nameLen + 2;
                if (nameLen == 0 || length < offset)
                    return false;
                port = (ushort)((packet[5 + nameLen] << 8) | packet[6 + nameLen]);
                source = IPAddress.Any;
                break;
            case 0x04:
                if (length < 22)
                    return false;
                source = new IPAddress(packet.AsSpan(4, 16));
                port = (ushort)((packet[20] << 8) | packet[21]);
                offset = 22;
                break;
            default:
                return false;
        }

        payloadOffset = offset;
        payloadLength = length - offset;
        return payloadLength >= 0;
    }

    private static byte[] BuildCommand(byte command, IPAddress ipv4, ushort port)
    {
        var req = new byte[10];
        req[0] = 0x05;
        req[1] = command;
        req[3] = 0x01;
        ipv4.GetAddressBytes().CopyTo(req, 4);
        req[8] = (byte)(port >> 8);
        req[9] = (byte)(port & 0xFF);
        return req;
    }

    private static async Task HandshakeAsync(Socket socket, CancellationToken ct)
    {
        await SendAllAsync(socket, [0x05, 0x01, 0x00], ct);
        var greet = new byte[2];
        await ReadExactAsync(socket, greet, ct);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 handshake rejected (method 0x{greet[1]:X2})");
    }

    private static async Task<(IPAddress Address, ushort Port)> CommandAsync(
        Socket socket,
        byte command,
        IPAddress address,
        ushort port,
        CancellationToken ct)
    {
        await SendAllAsync(socket, BuildCommand(command, address, port), ct);
        var header = new byte[4];
        await ReadExactAsync(socket, header, ct);
        if (header[0] != 0x05)
            throw new InvalidOperationException("SOCKS5 reply was not version 5");
        if (header[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 command 0x{command:X2} failed (code {header[1]})");

        switch (header[3])
        {
            case 0x01:
            {
                var body = new byte[6];
                await ReadExactAsync(socket, body, ct);
                var bind = new IPAddress(body.AsSpan(0, 4));
                var bindPort = (ushort)((body[4] << 8) | body[5]);
                return (bind, bindPort);
            }
            case 0x03:
            {
                var lenBuf = new byte[1];
                await ReadExactAsync(socket, lenBuf, ct);
                var body = new byte[lenBuf[0] + 2];
                await ReadExactAsync(socket, body, ct);
                var bindPort = (ushort)((body[^2] << 8) | body[^1]);
                return (IPAddress.Loopback, bindPort);
            }
            case 0x04:
            {
                var body = new byte[18];
                await ReadExactAsync(socket, body, ct);
                var bind = new IPAddress(body.AsSpan(0, 16));
                var bindPort = (ushort)((body[16] << 8) | body[17]);
                return (bind, bindPort);
            }
            default:
                throw new InvalidOperationException("SOCKS5 reply address type is unsupported");
        }
    }

    private static async Task SendAllAsync(Socket socket, byte[] data, CancellationToken ct)
    {
        var sent = 0;
        while (sent < data.Length)
        {
            var n = await socket.SendAsync(data.AsMemory(sent), SocketFlags.None, ct);
            if (n <= 0)
                throw new IOException("SOCKS5 send failed");
            sent += n;
        }
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct);
            if (n == 0)
                throw new IOException("SOCKS5 connection closed");
            offset += n;
        }
    }
}

internal sealed class Socks5UdpChannel : IDisposable
{
    private readonly Socket _control;
    private readonly Socket _udp;
    private readonly IPEndPoint _relay;
    private readonly object _sendLock = new();
    private int _disposed;

    public Socks5UdpChannel(Socket control, Socket udp, IPEndPoint relay)
    {
        _control = control;
        _udp = udp;
        _relay = relay;
    }

    public IPEndPoint Relay => _relay;

    public void SetReceiveTimeout(int milliseconds) => _udp.ReceiveTimeout = milliseconds;

    public void SendPayload(IPAddress destination, ushort port, byte[] payload, int length)
    {
        var packet = Socks5TunnelDial.BuildUdpDatagram(destination, port, payload, length);
        lock (_sendLock)
            _udp.SendTo(packet, _relay);
    }

    public int ReceivePayload(byte[] destination)
    {
        var raw = new byte[65535];
        int n = _udp.Receive(raw);
        if (n <= 0)
            return 0;
        if (!Socks5TunnelDial.TryParseUdpDatagram(raw, n, out _, out _, out var offset, out var length))
            return 0;
        if (length > destination.Length)
            length = destination.Length;
        if (length > 0)
            Buffer.BlockCopy(raw, offset, destination, 0, length);
        return length;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _udp.Dispose(); } catch { }
        try { _control.Dispose(); } catch { }
    }
}
