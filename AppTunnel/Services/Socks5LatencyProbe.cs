using System.IO;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Measures true end-to-end TCP RTT through a local SOCKS5 proxy (time-to-first-byte after CONNECT).
/// </summary>
internal static class Socks5LatencyProbe
{
    public const string DefaultProbeHost = "www.google.com";
    public const int DefaultProbePort = 443;

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

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, cts.Token);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new InvalidOperationException("SOCKS5 handshake rejected");

        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(req, cts.Token);

        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, cts.Token);
        if (resp[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], cts.Token); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, cts.Token);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], cts.Token);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], cts.Token); break;
        }

        byte[] probe = port == 443
            ? BuildTlsClientHello(host)
            : System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stream.WriteAsync(probe, cts.Token);

        var oneByte = new byte[1];
        try
        {
            int got = await stream.ReadAsync(oneByte, 0, 1, cts.Token);
            sw.Stop();
            if (sw.ElapsedMilliseconds <= 1 && got == 0)
                throw new InvalidOperationException("upstream closed (no data)");
            return sw.ElapsedMilliseconds;
        }
        catch (IOException) when (sw.ElapsedMilliseconds > 1)
        {
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
    }

    private static byte[] BuildTlsClientHello(string sniHost)
    {
        var sni = System.Text.Encoding.ASCII.GetBytes(sniHost);

        var sniExt = new List<byte> { 0x00, 0x00 };
        int sniListLen = 1 + 2 + sni.Length;
        int sniExtLen = 2 + sniListLen;
        sniExt.AddRange(new byte[] { (byte)(sniExtLen >> 8), (byte)sniExtLen });
        sniExt.AddRange(new byte[] { (byte)(sniListLen >> 8), (byte)sniListLen });
        sniExt.Add(0x00);
        sniExt.AddRange(new byte[] { (byte)(sni.Length >> 8), (byte)sni.Length });
        sniExt.AddRange(sni);

        var verExt = new byte[] { 0x00, 0x2b, 0x00, 0x05, 0x04, 0x03, 0x04, 0x03, 0x03 };
        var grpExt = new byte[] { 0x00, 0x0a, 0x00, 0x04, 0x00, 0x02, 0x00, 0x1d };
        var sigExt = new byte[] { 0x00, 0x0d, 0x00, 0x06, 0x00, 0x04, 0x08, 0x04, 0x04, 0x03 };

        var extensions = new List<byte>();
        extensions.AddRange(sniExt);
        extensions.AddRange(verExt);
        extensions.AddRange(grpExt);
        extensions.AddRange(sigExt);

        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });
        for (int i = 0; i < 32; i++) body.Add(0xAA);
        body.Add(0x00);
        body.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 });
        body.AddRange(new byte[] { 0x01, 0x00 });
        body.AddRange(new byte[] { (byte)(extensions.Count >> 8), (byte)extensions.Count });
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(new byte[] { 0x00, (byte)(body.Count >> 8), (byte)body.Count });
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01, (byte)(handshake.Count >> 8), (byte)handshake.Count };
        record.AddRange(handshake);
        return record.ToArray();
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
            if (read == 0)
                throw new InvalidOperationException("SOCKS5 connection closed unexpectedly");
            offset += read;
        }
    }
}
