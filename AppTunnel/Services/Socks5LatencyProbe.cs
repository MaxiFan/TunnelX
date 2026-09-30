using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AppTunnel.Services;

/// <summary>
/// Measures delay through a local SOCKS5 proxy by completing a real request to the target.
/// The local mixed/SOCKS inbound is only probe transport (xray/sing-box), not the protocol
/// under test. A SOCKS CONNECT reply failure, connection close, reset, or a SOCKS success
/// with no upstream response is therefore the same user-facing result: the config did not
/// carry traffic to the ping target. Handshake/TCP failures against 127.0.0.1 stay technical
/// because those mean the local inbound never came up.
/// </summary>
internal static class Socks5LatencyProbe
{
    public const string DefaultProbeHost = "www.google.com";
    public const int DefaultProbePort = 443;
    public const string NoResponseMessage = "پاسخی از مقصد پینگ نیامد";

    public static async Task<long> MeasureAsync(
        string host,
        int port,
        int socks5Port,
        CancellationToken ct,
        int probeTimeoutMs = 8000)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(probeTimeoutMs);

        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", socks5Port, cts.Token);

        var stream = tcp.GetStream();
        // Greeting is the local inbound. Failure here is probe plumbing, not the outbound.
        await WriteGreetingAsync(stream, cts.Token);

        // Time from the SOCKS CONNECT (remote dial) through the first real upstream response.
        // sing-box may answer CONNECT before the outbound is up; only the response after that
        // proves the config can carry traffic.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await WriteConnectAsync(stream, host, port, cts.Token);
            if (port == 443)
                await MeasureTlsHttpAsync(stream, host, cts.Token);
            else if (port == 80)
                await MeasurePlainHttpAsync(stream, host, cts.Token);
            else
                await MeasurePayloadByteAsync(stream, host, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(NoResponseMessage, ex);
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a SOCKS CONNECT/upstream failure through the
    /// local probe inbound (already mapped to <see cref="NoResponseMessage"/>, or still
    /// the raw CONNECT reply). Handshake and TCP-to-localhost errors return false.
    /// </summary>
    internal static bool IsOutboundProbeFailure(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (string.Equals(cur.Message, NoResponseMessage, StringComparison.Ordinal))
                return true;
            if (cur.Message.StartsWith("SOCKS5 connect failed", StringComparison.Ordinal))
                return true;
            if (cur.Message == "SOCKS5 connect reply was incomplete")
                return true;
        }

        return false;
    }

    internal static bool TryParseHttpStatusLine(ReadOnlySpan<char> line, out int status)
    {
        status = 0;
        if (!line.StartsWith("HTTP/1.") && !line.StartsWith("HTTP/2"))
            return false;

        var space = line.IndexOf(' ');
        if (space < 0 || space + 3 >= line.Length)
            return false;

        if (!int.TryParse(line.Slice(space + 1, 3), out status))
            return false;

        return status is >= 100 and <= 599;
    }

    private static async Task WriteGreetingAsync(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, ct);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new InvalidOperationException("SOCKS5 handshake rejected");
    }

    private static async Task WriteConnectAsync(NetworkStream stream, string host, int port, CancellationToken ct)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length == 0 || hostBytes.Length > 255)
            throw new InvalidOperationException("SOCKS5 target host is invalid");

        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(req, ct);

        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, ct);
        if (resp[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], ct); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, ct);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], ct);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], ct); break;
            default:
                throw new InvalidOperationException("SOCKS5 connect reply was incomplete");
        }
    }

    private static async Task MeasureTlsHttpAsync(NetworkStream stream, string host, CancellationToken ct)
    {
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, ct);

        await WriteHttpGetAsync(ssl, host, ct);
        await ReadHttpStatusAsync(ssl, ct);
    }

    private static async Task MeasurePlainHttpAsync(NetworkStream stream, string host, CancellationToken ct)
    {
        await WriteHttpGetAsync(stream, host, ct);
        await ReadHttpStatusAsync(stream, ct);
    }

    private static async Task MeasurePayloadByteAsync(NetworkStream stream, string host, CancellationToken ct)
    {
        var probe = Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n");
        await stream.WriteAsync(probe, ct);
        var oneByte = new byte[1];
        var got = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
        if (got == 0)
            throw new InvalidOperationException(NoResponseMessage);
    }

    private static async Task WriteHttpGetAsync(Stream stream, string host, CancellationToken ct)
    {
        var request = Encoding.ASCII.GetBytes(
            $"GET /generate_204 HTTP/1.1\r\nHost: {host}\r\nUser-Agent: TunnelX\r\nAccept: */*\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, ct);
    }

    private static async Task ReadHttpStatusAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), ct);
            if (read == 0)
                throw new InvalidOperationException(NoResponseMessage);

            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd < 0)
                lineEnd = text.IndexOf('\n');
            if (lineEnd < 0)
                continue;

            if (!TryParseHttpStatusLine(text.AsSpan(0, lineEnd), out _))
                throw new InvalidOperationException(NoResponseMessage);
            return;
        }

        throw new InvalidOperationException(NoResponseMessage);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new InvalidOperationException("SOCKS5 connection closed unexpectedly");
            offset += read;
        }
    }
}
