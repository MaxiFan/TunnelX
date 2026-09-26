using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TunnelX.ProxyLifecycle;

namespace AppTunnel.Services;

/// <summary>
/// Mixed SOCKS5/HTTP CONNECT proxy that forces every outgoing connection to
/// use the VPN adapter by binding the outbound socket to the VPN local IP.
/// Protocol auto-detection on the first byte: 0x05 → SOCKS5, 'C' → HTTP CONNECT.
/// </summary>
internal sealed class MixedProxyServer
{
    private readonly int _listenPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _bindEndpoint;
    private long _connCount;
    private long _connActive;
    private Action<IPAddress>? _ensureRoute;
    private readonly ProxyClientSessionTable _sessions = new();

    public MixedProxyServer(int listenPort = 1080)
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
            Logger.Error($"[MIXED] Invalid VPN local IP '{vpnLocalIp}', server not started");
            return;
        }
        _bindEndpoint = new IPEndPoint(bindIp, 0);
        _ensureRoute = ensureRoute;

        var announced = false;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _listener.Start();
            _cts = new CancellationTokenSource();
            Logger.Info($"[MIXED] Listening on 127.0.0.1:{_listenPort}, outbound bind={vpnLocalIp}");
            LocalProxyAnnouncements.Connected(_listenPort);
            announced = true;
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Logger.Error($"[MIXED] Failed to start listener on port {_listenPort}: {ex.Message}");
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
            if (announced)
                LocalProxyAnnouncements.Disconnected(_listenPort);
        }
    }

    public void Stop()
    {
        var wasRunning = _listener != null;
        try { _cts?.Cancel(); } catch { }
        // Reset accepted clients before dropping the listener so proxies such as
        // Telethon observe the disconnect (run_until_disconnected) immediately.
        _sessions.AbortAll();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        if (wasRunning)
            LocalProxyAnnouncements.Disconnected(_listenPort);
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
                Logger.Warning($"[MIXED] Accept error: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        long connId = Interlocked.Increment(ref _connCount);
        Interlocked.Increment(ref _connActive);
        _sessions.Track(connId, client);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 15000;

            // Peek first byte to decide protocol
            var firstByte = new byte[1];
            int peeked = await stream.ReadAsync(firstByte.AsMemory(0, 1), ct);
            if (peeked == 0) return;

            if (firstByte[0] == 0x05)
            {
                await HandleSocks5Async(stream, firstByte, connId, ct);
            }
            else if (firstByte[0] == (byte)'C' || firstByte[0] == (byte)'c')
            {
                await HandleHttpConnectAsync(stream, firstByte, connId, ct);
            }
            else
            {
                Logger.Warning($"[MIXED #{connId}] Unknown first byte 0x{firstByte[0]:X2}, closing");
                try { client.Close(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning($"[MIXED #{connId}] error: {ex.Message}");
        }
        finally
        {
            _sessions.Untrack(connId);
            try { client.Dispose(); } catch { }
            Interlocked.Decrement(ref _connActive);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SOCKS5 handler (existing logic preserved)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task HandleSocks5Async(NetworkStream stream, byte[] firstByte, long connId, CancellationToken ct)
    {
        TcpClient? upstream = null;
        try
        {
            // We already read the first byte (0x05). Now read NMETHODS + methods.
            var hdr = new byte[1];
            if (!await ReadExactAsync(stream, hdr, 0, 1, ct)) return;
            int nMethods = hdr[0];
            if (nMethods <= 0 || nMethods > 32) return;
            var methods = new byte[nMethods];
            if (!await ReadExactAsync(stream, methods, 0, nMethods, ct)) return;

            // No-auth only
            if (!methods.Contains((byte)0x00))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xFF }, ct);
                return;
            }
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            // Request
            var req = new byte[4];
            if (!await ReadExactAsync(stream, req, 0, 4, ct)) return;
            if (req[0] != 0x05 || req[1] != 0x01)
            {
                await WriteSocks5ReplyAsync(stream, 0x07, ct);
                return;
            }

            byte atyp = req[3];
            string host;
            IPAddress? remoteIp = null;
            switch (atyp)
            {
                case 0x01:
                {
                    var addr = new byte[4];
                    if (!await ReadExactAsync(stream, addr, 0, 4, ct)) return;
                    remoteIp = new IPAddress(addr);
                    host = remoteIp.ToString();
                    break;
                }
                case 0x03:
                {
                    var lenBuf = new byte[1];
                    if (!await ReadExactAsync(stream, lenBuf, 0, 1, ct)) return;
                    int dlen = lenBuf[0];
                    var dbuf = new byte[dlen];
                    if (!await ReadExactAsync(stream, dbuf, 0, dlen, ct)) return;
                    host = Encoding.ASCII.GetString(dbuf);
                    break;
                }
                case 0x04:
                {
                    var addr = new byte[16];
                    if (!await ReadExactAsync(stream, addr, 0, 16, ct)) return;
                    remoteIp = new IPAddress(addr);
                    host = remoteIp.ToString();
                    break;
                }
                default:
                    await WriteSocks5ReplyAsync(stream, 0x08, ct);
                    return;
            }

            var portBuf = new byte[2];
            if (!await ReadExactAsync(stream, portBuf, 0, 2, ct)) return;
            int port = (portBuf[0] << 8) | portBuf[1];

            if (remoteIp == null)
            {
                try
                {
                    remoteIp = await DnsResolverCache.ResolveFirstIpv4Async(host, ct);
                    if (remoteIp == null)
                    {
                        Logger.Warning($"[SOCKS5 #{connId}] DNS for '{host}' returned no IPv4");
                        await WriteSocks5ReplyAsync(stream, 0x04, ct);
                        return;
                    }
                }
                catch (Exception dnsEx)
                {
                    Logger.Warning($"[SOCKS5 #{connId}] DNS resolve '{host}' failed: {dnsEx.Message}");
                    await WriteSocks5ReplyAsync(stream, 0x04, ct);
                    return;
                }
            }

            try { _ensureRoute?.Invoke(remoteIp); } catch { }

            upstream = await DialUpstreamAsync(remoteIp, port, connId, ct);
            if (upstream == null)
            {
                await WriteSocks5ReplyAsync(stream, 0x05, ct);
                return;
            }

            Logger.Info($"[SOCKS5 #{connId}] CONNECT → {host}:{port} (via {_bindEndpoint!.Address})");
            await WriteSocks5ReplyAsync(stream, 0x00, ct);
            await RelayAsync(stream, upstream.GetStream(), ct);
        }
        finally
        {
            try { upstream?.Dispose(); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTTP CONNECT handler
    // ─────────────────────────────────────────────────────────────────────────
    private async Task HandleHttpConnectAsync(NetworkStream stream, byte[] firstByte, long connId, CancellationToken ct)
    {
        TcpClient? upstream = null;
        try
        {
            // firstByte[0] is 'C' or 'c'. Read the rest of the first line.
            var sb = new StringBuilder(Encoding.ASCII.GetString(firstByte));
            var lineBuf = new byte[1];
            // Read until \r\n
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(lineBuf.AsMemory(0, 1), ct);
                if (n == 0) return;
                char c = (char)lineBuf[0];
                if (c == '\n')
                {
                    string line = sb.ToString().TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue; // skip empty lines before request
                    break;
                }
                sb.Append(c);
                if (sb.Length > 4096) return; // line too long
            }

            string firstLine = sb.ToString().TrimEnd('\r');
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpErrorAsync(stream, "400 Bad Request", ct);
                return;
            }
            var target = parts[1];
            if (!parts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpErrorAsync(stream, "400 Bad Request", ct);
                return;
            }

            // Read remaining headers until empty line
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var headerSb = new StringBuilder();
            int emptyLineCount = 0;
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(lineBuf.AsMemory(0, 1), ct);
                if (n == 0) return;
                char c = (char)lineBuf[0];
                if (c == '\n')
                {
                    string hline = headerSb.ToString().TrimEnd('\r');
                    if (string.IsNullOrEmpty(hline))
                    {
                        emptyLineCount++;
                        if (emptyLineCount >= 1) break;
                    }
                    else
                    {
                        emptyLineCount = 0;
                        var colonIdx = hline.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var key = hline[..colonIdx].Trim();
                            var val = hline[(colonIdx + 1)..].Trim();
                            headers[key] = val;
                        }
                    }
                    headerSb.Clear();
                    if (headerSb.Length > 8192) return; // headers too long
                }
                else
                {
                    headerSb.Append(c);
                }
            }

            // Parse target host:port
            string host;
            int port;
            var targetParts = target.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (targetParts.Length == 2 && int.TryParse(targetParts[1], out port))
            {
                host = targetParts[0];
            }
            else
            {
                host = target;
                port = 443;
            }

            IPAddress? remoteIp = null;
            if (IPAddress.TryParse(host, out var parsedIp))
            {
                remoteIp = parsedIp;
            }
            else
            {
                try
                {
                    remoteIp = await DnsResolverCache.ResolveFirstIpv4Async(host, ct);
                    if (remoteIp == null)
                    {
                        Logger.Warning($"[HTTP #{connId}] DNS for '{host}' returned no IPv4");
                        await WriteHttpErrorAsync(stream, "502 Bad Gateway", ct);
                        return;
                    }
                }
                catch (Exception dnsEx)
                {
                    Logger.Warning($"[HTTP #{connId}] DNS resolve '{host}' failed: {dnsEx.Message}");
                    await WriteHttpErrorAsync(stream, "502 Bad Gateway", ct);
                    return;
                }
            }

            try { _ensureRoute?.Invoke(remoteIp); } catch { }

            upstream = await DialUpstreamAsync(remoteIp, port, connId, ct);
            if (upstream == null)
            {
                await WriteHttpErrorAsync(stream, "502 Bad Gateway", ct);
                return;
            }

            Logger.Info($"[HTTP #{connId}] CONNECT → {host}:{port} (via {_bindEndpoint!.Address})");
            await WriteHttpAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
            await RelayAsync(stream, upstream.GetStream(), ct);
        }
        finally
        {
            try { upstream?.Dispose(); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<TcpClient?> DialUpstreamAsync(IPAddress remoteIp, int port, long connId, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            client.NoDelay = true;
            try
            {
                client.Client.Bind(_bindEndpoint!);
            }
            catch (Exception bex)
            {
                Logger.Warning($"[MIXED #{connId}] bind to VPN IP failed: {bex.Message}");
                client.Dispose();
                return null;
            }

            try
            {
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dialCts.CancelAfter(TimeSpan.FromSeconds(15));
                await client.ConnectAsync(remoteIp, port, dialCts.Token);
                return client;
            }
            catch (Exception dex) when (
                attempt < maxAttempts &&
                dex is SocketException sex &&
                (sex.SocketErrorCode == SocketError.NetworkUnreachable ||
                 sex.SocketErrorCode == SocketError.HostUnreachable))
            {
                Logger.Info($"[MIXED #{connId}] connect {remoteIp}:{port} unreachable, retrying...");
                client.Dispose();
                await Task.Delay(600, ct);
            }
            catch (Exception dex)
            {
                if (!IsBenignCanceledConnect(dex))
                    Logger.Warning($"[MIXED #{connId}] connect {remoteIp}:{port} failed: {dex.Message}");
                client.Dispose();
                return null;
            }
        }
        return null;
    }

    private static bool IsBenignCanceledConnect(Exception ex)
    {
        if (ex is OperationCanceledException || ex is TaskCanceledException)
            return true;
        if (ex is SocketException sx &&
            (sx.SocketErrorCode == SocketError.OperationAborted ||
             sx.SocketErrorCode == SocketError.TimedOut))
            return true;
        return ex.Message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RelayAsync(NetworkStream clientStream, NetworkStream upstreamStream, CancellationToken ct)
    {
        var t1 = PumpAsync(clientStream, upstreamStream, ct);
        var t2 = PumpAsync(upstreamStream, clientStream, ct);
        await Task.WhenAny(t1, t2);
        try { clientStream.Socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { upstreamStream.Socket?.Shutdown(SocketShutdown.Both); } catch { }
        await Task.WhenAll(t1, t2);
    }

    private static async Task WriteSocks5ReplyAsync(NetworkStream s, byte rep, CancellationToken ct)
    {
        var buf = new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        try { await s.WriteAsync(buf, ct); } catch { }
    }

    private static async Task WriteHttpErrorAsync(NetworkStream s, string status, CancellationToken ct, string extraHeaders = "")
    {
        var msg = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n{extraHeaders}\r\n";
        try { await s.WriteAsync(Encoding.UTF8.GetBytes(msg), ct); } catch { }
    }

    private static async Task WriteHttpAsync(NetworkStream s, string text, CancellationToken ct)
    {
        try { await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct); } catch { }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int offset, int count, CancellationToken ct)
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

    private static async Task PumpAsync(NetworkStream src, NetworkStream dst, CancellationToken ct)
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
