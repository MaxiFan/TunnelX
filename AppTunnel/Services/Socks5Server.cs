using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AppTunnel.Services;

/// <summary>
/// Minimal SOCKS5 CONNECT proxy that forces every outgoing connection to
/// use the VPN adapter by binding the outbound socket to the VPN local IP.
///
/// Use case: apps that natively support SOCKS5 (Telegram, Firefox, ...) can
/// be pointed at 127.0.0.1:1080 and their traffic is guaranteed to egress
/// the VPN, regardless of the default route or any host-route state.
///
/// Only CMD_CONNECT (0x01) is supported. No authentication (METHOD_NONE).
/// </summary>
internal sealed class Socks5Server
{
    private readonly int _listenPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _bindEndpoint;
    private long _connCount;
    private long _connActive;
    private Action<IPAddress>? _ensureRoute;

    public Socks5Server(int listenPort = 1080)
    {
        _listenPort = listenPort;
    }

    public bool IsRunning => _listener != null;
    public long TotalConnections => Interlocked.Read(ref _connCount);
    public long ActiveConnections => Interlocked.Read(ref _connActive);

    public void Start(string vpnLocalIp, Action<IPAddress>? ensureRoute = null)
    {
        if (_listener != null) return;
        if (!IPAddress.TryParse(vpnLocalIp, out var bindIp))
        {
            Logger.Error($"[SOCKS5] Invalid VPN local IP '{vpnLocalIp}', server not started");
            return;
        }
        _bindEndpoint = new IPEndPoint(bindIp, 0);
        _ensureRoute = ensureRoute;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _listener.Start();
            _cts = new CancellationTokenSource();
            Logger.Info($"[SOCKS5] Listening on 127.0.0.1:{_listenPort}, outbound bind={vpnLocalIp}");
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Logger.Error($"[SOCKS5] Failed to start listener on port {_listenPort}: {ex.Message}");
            _listener = null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Logger.Warning($"[SOCKS5] Accept error: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        long connId = Interlocked.Increment(ref _connCount);
        Interlocked.Increment(ref _connActive);
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 15000;

            // ---- Greeting ----
            // VER(1) NMETHODS(1) METHODS(NMETHODS)
            var hdr = new byte[2];
            if (!await ReadExactAsync(stream, hdr, 0, 2, ct)) return;
            if (hdr[0] != 0x05) return;
            int nMethods = hdr[1];
            if (nMethods <= 0 || nMethods > 32) return;
            var methods = new byte[nMethods];
            if (!await ReadExactAsync(stream, methods, 0, nMethods, ct)) return;

            // Reply: VER=5, METHOD=0 (no auth)
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            // ---- Request ----
            // VER CMD RSV ATYP DST.ADDR DST.PORT
            var req = new byte[4];
            if (!await ReadExactAsync(stream, req, 0, 4, ct)) return;
            if (req[0] != 0x05) return;
            if (req[1] != 0x01)
            {
                // CMD not supported (0x07)
                await stream.WriteAsync(
                    new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                return;
            }

            byte atyp = req[3];
            string host;
            IPAddress? remoteIp = null;
            switch (atyp)
            {
                case 0x01: // IPv4
                {
                    var addr = new byte[4];
                    if (!await ReadExactAsync(stream, addr, 0, 4, ct)) return;
                    remoteIp = new IPAddress(addr);
                    host = remoteIp.ToString();
                    break;
                }
                case 0x03: // domain
                {
                    var lenBuf = new byte[1];
                    if (!await ReadExactAsync(stream, lenBuf, 0, 1, ct)) return;
                    int dlen = lenBuf[0];
                    var dbuf = new byte[dlen];
                    if (!await ReadExactAsync(stream, dbuf, 0, dlen, ct)) return;
                    host = System.Text.Encoding.ASCII.GetString(dbuf);
                    break;
                }
                case 0x04: // IPv6
                {
                    var addr = new byte[16];
                    if (!await ReadExactAsync(stream, addr, 0, 16, ct)) return;
                    remoteIp = new IPAddress(addr);
                    host = remoteIp.ToString();
                    break;
                }
                default:
                    // ATYP not supported (0x08)
                    await stream.WriteAsync(
                        new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
                    return;
            }

            var portBuf = new byte[2];
            if (!await ReadExactAsync(stream, portBuf, 0, 2, ct)) return;
            int port = (portBuf[0] << 8) | portBuf[1];

            // Resolve domain to IP if needed, so we can install a route.
            if (remoteIp == null)
            {
                try
                {
                    remoteIp = await DnsResolverCache.ResolveFirstIpv4Async(host, ct);
                    if (remoteIp == null)
                    {
                        Logger.Warning($"[SOCKS5 #{connId}] DNS for '{host}' returned no IPv4");
                        await WriteReplyAsync(stream, 0x04 /*host unreachable*/, ct);
                        return;
                    }
                }
                catch (Exception dnsEx)
                {
                    Logger.Warning($"[SOCKS5 #{connId}] DNS resolve '{host}' failed: {dnsEx.Message}");
                    await WriteReplyAsync(stream, 0x04 /*host unreachable*/, ct);
                    return;
                }
            }

            // Install a /32 host route for the target IP via VPN so the
            // outbound socket (bound to VPN IP) can actually reach it.
            try { _ensureRoute?.Invoke(remoteIp); } catch { }

            // ---- Dial upstream, bound to VPN IP ----
            // ---- Dial upstream (with one retry for route propagation race) ----
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                upstream = new TcpClient(AddressFamily.InterNetwork);
                upstream.NoDelay = true;
                try
                {
                    upstream.Client.Bind(_bindEndpoint!);
                }
                catch (Exception bex)
                {
                    Logger.Warning($"[SOCKS5 #{connId}] bind to VPN IP failed: {bex.Message}");
                    await WriteReplyAsync(stream, 0x01 /*general failure*/, ct);
                    return;
                }

                try
                {
                    using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    dialCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await upstream.ConnectAsync(remoteIp, port, dialCts.Token);
                    break; // connected successfully
                }
                catch (Exception dex) when (
                    attempt < maxAttempts &&
                    dex is System.Net.Sockets.SocketException sex &&
                    (sex.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable ||
                     sex.SocketErrorCode == System.Net.Sockets.SocketError.HostUnreachable))
                {
                    Logger.Info($"[SOCKS5 #{connId}] connect {host}:{port} unreachable, retrying after route propagation...");
                    upstream.Dispose();
                    upstream = null;
                    await Task.Delay(600, ct);
                }
                catch (Exception dex)
                {
                    Logger.Warning($"[SOCKS5 #{connId}] connect {host}:{port} failed: {dex.Message}");
                    await WriteReplyAsync(stream, 0x05 /*conn refused*/, ct);
                    return;
                }
            }

            if (upstream == null || !upstream.Connected)
            {
                Logger.Warning($"[SOCKS5 #{connId}] connect {host}:{port} failed after retries");
                await WriteReplyAsync(stream, 0x05 /*conn refused*/, ct);
                return;
            }

            Logger.Info($"[SOCKS5 #{connId}] CONNECT → {host}:{port} (via {_bindEndpoint!.Address})");

            // Success reply with bound local addr (127.0.0.1:listenPort — we
            // lie here; clients don't actually use BND fields).
            await WriteReplyAsync(stream, 0x00, ct);

            // ---- Relay both directions ----
            var upstreamStream = upstream.GetStream();
            var t1 = PumpAsync(stream, upstreamStream, ct);
            var t2 = PumpAsync(upstreamStream, stream, ct);
            await Task.WhenAny(t1, t2);
            try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { upstream.Client.Shutdown(SocketShutdown.Both); } catch { }
            await Task.WhenAll(t1, t2);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[SOCKS5 #{connId}] error: {ex.Message}");
        }
        finally
        {
            try { upstream?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            Interlocked.Decrement(ref _connActive);
        }
    }

    private static async Task WriteReplyAsync(NetworkStream s, byte rep, CancellationToken ct)
    {
        // VER=5, REP, RSV=0, ATYP=IPv4, BND.ADDR=0.0.0.0, BND.PORT=0
        var buf = new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        try { await s.WriteAsync(buf, ct); } catch { }
    }

    private static async Task<bool> ReadExactAsync(
        NetworkStream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + read, count - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task PumpAsync(
        NetworkStream src, NetworkStream dst, CancellationToken ct)
    {
        var buf = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await src.ReadAsync(buf, ct);
                if (n <= 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        catch { }
    }
}
