using System.Net;
using System.Net.Sockets;
using System.Text;
using AppTunnel.Services;
using Xunit;

namespace AppTunnel.Tests;

public class SplitLoopbackInjectTests
{
    [Fact]
    public void Redirect_ClearsStaleChecksumBitsAndMarksLoopbackImpostor()
    {
        var addr = new WinDivertAddress
        {
            LayerEventFlags = (1UL << 17) | (1UL << 21) | (1UL << 22) | (1UL << 23),
            IfIdx = 4
        };

        SplitLoopbackInject.PrepareOutboundRedirect(ref addr);

        Assert.True(addr.Outbound);
        Assert.True(addr.Loopback);
        Assert.True(addr.Impostor);
        Assert.False(addr.IpChecksum);
        Assert.False(addr.TcpChecksum);
        Assert.False(addr.UdpChecksum);
        Assert.Equal(4u, addr.IfIdx);
    }

    [Fact]
    public void Return_IsInboundOnTheAppsInterfaceAndNotRecaptured()
    {
        var addr = new WinDivertAddress
        {
            LayerEventFlags = (1UL << 18) | (1UL << 21),
            IfIdx = 1
        };

        SplitLoopbackInject.PrepareReturn(ref addr, 4);

        Assert.False(addr.Outbound);
        Assert.False(addr.Loopback);
        Assert.True(addr.Impostor);
        Assert.False(addr.IpChecksum);
        Assert.Equal(4u, addr.IfIdx);
        Assert.Equal(0u, addr.SubIfIdx);
    }

    [Fact]
    public void ReturnWithoutAPhysicalIndex_StaysOnLoopback()
    {
        var addr = new WinDivertAddress { IfIdx = 1 };
        SplitLoopbackInject.PrepareReturn(ref addr, 0);

        Assert.False(addr.Outbound);
        Assert.True(addr.Loopback);
        Assert.True(addr.Impostor);
        Assert.Equal(1u, addr.IfIdx);
    }
}

public class Socks5TunnelDialTests
{
    [Fact]
    public void ConnectRequest_IsIpv4Socks5()
    {
        var req = Socks5TunnelDial.BuildConnectRequest(IPAddress.Parse("142.251.156.4"), 443);
        Assert.Equal(new byte[] { 0x05, 0x01, 0x00, 0x01, 142, 251, 156, 4, 0x01, 0xBB }, req);
    }

    [Fact]
    public void UdpDatagram_RoundTripsThePayload()
    {
        var payload = Encoding.ASCII.GetBytes("quic");
        var packet = Socks5TunnelDial.BuildUdpDatagram(IPAddress.Parse("1.1.1.1"), 443, payload, payload.Length);

        Assert.True(Socks5TunnelDial.TryParseUdpDatagram(packet, packet.Length, out var source, out var port, out var offset, out var length));
        Assert.Equal("1.1.1.1", source.ToString());
        Assert.Equal((ushort)443, port);
        Assert.Equal("quic", Encoding.ASCII.GetString(packet, offset, length));
    }

    [Fact]
    public void UdpAssociateRequest_AsksForAnUnboundRelay()
    {
        var req = Socks5TunnelDial.BuildUdpAssociateRequest();
        Assert.Equal(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, req);
    }

    [Fact(Timeout = 15000)]
    public async Task ConnectTcp_RelaysBytesThroughALocalSocks5Inbound()
    {
        await using var echo = await TcpEcho.StartAsync();
        await using var proxy = await FakeSocks5.StartAsync();

        using var socket = await Socks5TunnelDial.ConnectTcpAsync(
            new IPEndPoint(IPAddress.Loopback, proxy.Port),
            IPAddress.Loopback,
            echo.Port,
            CancellationToken.None,
            timeoutMs: 4000);

        await socket.SendAsync(Encoding.ASCII.GetBytes("hello"), SocketFlags.None);
        var buf = new byte[5];
        var got = 0;
        while (got < buf.Length)
        {
            var n = await socket.ReceiveAsync(buf.AsMemory(got), SocketFlags.None);
            Assert.NotEqual(0, n);
            got += n;
        }

        Assert.Equal("hello", Encoding.ASCII.GetString(buf));
    }

    [Fact(Timeout = 15000)]
    public async Task UdpAssociate_RelaysADatagram()
    {
        using var echo = UdpEcho.Start();
        await using var proxy = await FakeSocks5.StartAsync();
        using var channel = await Socks5TunnelDial.AssociateUdpAsync(
            new IPEndPoint(IPAddress.Loopback, proxy.Port),
            CancellationToken.None,
            timeoutMs: 4000);

        var payload = Encoding.ASCII.GetBytes("dns");
        channel.SendPayload(IPAddress.Loopback, (ushort)echo.Port, payload, payload.Length);
        channel.SetReceiveTimeout(4000);
        var buf = new byte[16];
        var n = channel.ReceivePayload(buf);
        Assert.Equal("dns", Encoding.ASCII.GetString(buf, 0, n));
    }
}

public class SplitAppForwarderSocksTests
{
    [Fact]
    public void WithoutAMixedProxy_DiagnosticsSayVpnBind()
    {
        using var forwarder = new SplitAppForwarder();
        forwarder.Start(IPAddress.Parse("172.18.0.1"), 6);
        Assert.False(forwarder.UsesMixedProxy);
        Assert.Contains("vpn-bind/6", forwarder.Diagnostics);
    }

    [Fact(Timeout = 15000)]
    public async Task SelectedTcp_IsSplicedThroughTheMixedProxy()
    {
        await using var echo = await TcpEcho.StartAsync();
        await using var proxy = await FakeSocks5.StartAsync();
        using var forwarder = new SplitAppForwarder();
        forwarder.Start(
            IPAddress.Parse("172.18.0.1"),
            6,
            new IPEndPoint(IPAddress.Loopback, proxy.Port));
        Assert.True(forwarder.UsesMixedProxy);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var clientNbo = BitConverter.ToUInt32(local.Address.GetAddressBytes(), 0);
        var echoNbo = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
        forwarder.Register(6, clientNbo, (ushort)local.Port, echoNbo, (ushort)echo.Port, echoNbo, (ushort)echo.Port, originalIfIdx: 4);

        await client.ConnectAsync(IPAddress.Loopback, forwarder.TcpPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("pages"));
        var buf = new byte[5];
        var got = 0;
        using var readCts = new CancellationTokenSource(4000);
        while (got < buf.Length)
            got += await stream.ReadAsync(buf.AsMemory(got), readCts.Token);

        Assert.Equal("pages", Encoding.ASCII.GetString(buf));
        Assert.Contains("socks5/", forwarder.Diagnostics);
    }

    [Fact(Timeout = 15000)]
    public async Task SelectedUdp_IsAssociatedThroughTheMixedProxy()
    {
        using var echo = UdpEcho.Start();
        await using var proxy = await FakeSocks5.StartAsync();
        using var forwarder = new SplitAppForwarder();
        forwarder.Start(
            IPAddress.Parse("172.18.0.1"),
            6,
            new IPEndPoint(IPAddress.Loopback, proxy.Port));

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        client.ReceiveTimeout = 4000;
        var local = (IPEndPoint)client.LocalEndPoint!;
        var clientNbo = BitConverter.ToUInt32(local.Address.GetAddressBytes(), 0);
        var echoNbo = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
        forwarder.Register(17, clientNbo, (ushort)local.Port, echoNbo, (ushort)echo.Port, echoNbo, (ushort)echo.Port);

        var payload = Encoding.ASCII.GetBytes("quic");
        client.SendTo(payload, new IPEndPoint(IPAddress.Loopback, forwarder.UdpPort));
        var buf = new byte[16];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        int n = client.ReceiveFrom(buf, ref remote);
        Assert.Equal("quic", Encoding.ASCII.GetString(buf, 0, n));
    }
}

internal sealed class TcpEcho : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private TcpEcho(TcpListener listener) => _listener = listener;

    public int Port { get; private set; }

    public static async Task<TcpEcho> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var echo = new TcpEcho(listener) { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        _ = Task.Run(() => echo.LoopAsync(echo._cts.Token));
        await Task.Yield();
        return echo;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        await Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => EchoAsync(client));
        }
    }

    private static async Task EchoAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buf = new byte[64];
            int n = await stream.ReadAsync(buf);
            if (n > 0)
                await stream.WriteAsync(buf.AsMemory(0, n));
        }
    }
}

internal sealed class UdpEcho : IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();

    private UdpEcho(Socket socket) => _socket = socket;

    public int Port { get; private set; }

    public static UdpEcho Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var echo = new UdpEcho(socket) { Port = ((IPEndPoint)socket.LocalEndPoint!).Port };
        _ = Task.Run(() => echo.Loop(echo._cts.Token));
        return echo;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _socket.Dispose(); } catch { }
        _cts.Dispose();
    }

    private void Loop(CancellationToken ct)
    {
        var buf = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = _socket.ReceiveFrom(buf, ref remote);
                _socket.SendTo(buf.AsSpan(0, n), SocketFlags.None, remote);
            }
            catch
            {
                break;
            }
        }
    }
}

internal sealed class FakeSocks5 : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private FakeSocks5(TcpListener listener) => _listener = listener;

    public int Port { get; private set; }

    public static async Task<FakeSocks5> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FakeSocks5(listener) { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        _ = Task.Run(() => proxy.LoopAsync(proxy._cts.Token));
        await Task.Yield();
        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        await Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private static async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var greet = new byte[3];
            if (!await ReadExactAsync(stream, greet, ct) || greet[0] != 0x05)
                return;
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            var hdr = new byte[4];
            if (!await ReadExactAsync(stream, hdr, ct) || hdr[3] != 0x01)
                return;
            var body = new byte[6];
            if (!await ReadExactAsync(stream, body, ct))
                return;

            var dest = new IPAddress(body.AsSpan(0, 4));
            var destPort = (body[4] << 8) | body[5];
            if (hdr[1] == 0x01)
            {
                await stream.WriteAsync(SuccessReply(IPAddress.Loopback, 0), ct);
                using var remote = new TcpClient();
                await remote.ConnectAsync(dest, destPort, ct);
                var other = remote.GetStream();
                var a = CopyAsync(stream, other, ct);
                var b = CopyAsync(other, stream, ct);
                await Task.WhenAny(a, b);
            }
            else if (hdr[1] == 0x03)
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var relay = (IPEndPoint)udp.LocalEndPoint!;
                using var relayDone = new CancellationTokenSource();
                var relayTask = Task.Run(() => RelayUdp(udp, relayDone.Token));
                await stream.WriteAsync(SuccessReply(IPAddress.Loopback, (ushort)relay.Port), ct);
                var idle = new byte[1];
                try { await stream.ReadAsync(idle, ct); }
                catch { }
                relayDone.Cancel();
                try { udp.Dispose(); } catch { }
                try { await relayTask; } catch { }
            }
        }
    }

    private static void RelayUdp(Socket udp, CancellationToken ct)
    {
        var buf = new byte[65535];
        EndPoint? client = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n = udp.ReceiveFrom(buf, ref remote);
                if (client == null || remote.Equals(client))
                {
                    client = remote;
                    if (!Socks5TunnelDial.TryParseUdpDatagram(buf, n, out var dest, out var port, out var offset, out var length))
                        continue;
                    udp.SendTo(buf.AsSpan(offset, length), SocketFlags.None, new IPEndPoint(dest, port));
                }
                else if (client != null)
                {
                    var wrapped = Socks5TunnelDial.BuildUdpDatagram(((IPEndPoint)remote).Address, (ushort)((IPEndPoint)remote).Port, buf, n);
                    udp.SendTo(wrapped, client);
                }
            }
            catch
            {
                break;
            }
        }
    }

    private static byte[] SuccessReply(IPAddress address, ushort port)
    {
        var reply = new byte[10];
        reply[0] = 0x05;
        reply[3] = 0x01;
        address.GetAddressBytes().CopyTo(reply, 4);
        reply[8] = (byte)(port >> 8);
        reply[9] = (byte)(port & 0xFF);
        return reply;
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await from.ReadAsync(buf, ct); }
            catch { return; }
            if (n == 0)
                return;
            try { await to.WriteAsync(buf.AsMemory(0, n), ct); }
            catch { return; }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(offset), ct); }
            catch { return false; }
            if (n == 0)
                return false;
            offset += n;
        }

        return true;
    }
}
