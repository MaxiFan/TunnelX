using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AppTunnel.Services;
using Xunit;

namespace TunnelX.LatencyTests;

public class PreConnectLatencyPlanTests
{
    [Theory]
    [InlineData("vmess://eyJhZGQiOiIxLjIuMy40In0")]
    [InlineData("VMESS://abc")]
    [InlineData("vless://11111111-1111-1111-1111-111111111111@example.com:443?type=ws&security=tls")]
    [InlineData("vless://11111111-1111-1111-1111-111111111111@example.com:443?type=xhttp&security=tls")]
    [InlineData("{\"streamSettings\":{\"network\":\"ws\"}}")]
    public void XrayConfigs_UseRealDelay_NotServerTcp(string config)
    {
        Assert.Equal(PreConnectLatencyMode.RealDelayXray, PreConnectLatencyPlan.ForV2RayConfig(config));
        Assert.True(PreConnectLatencyPlan.UsesRealDelay(config));
    }

    [Theory]
    [InlineData("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls&type=tcp")]
    [InlineData("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality&pbk=abc")]
    [InlineData("trojan://secret@example.com:443")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzcw@example.com:8388")]
    [InlineData("socks5://user:pass@example.com:1080")]
    [InlineData("http://user:pass@example.com:8080")]
    [InlineData("hysteria://example.com:443?auth=pw")]
    [InlineData("hysteria2://pw@example.com:443/?sni=www.example.com")]
    [InlineData("hy2://pw@example.com:443/")]
    [InlineData("{\"type\":\"hysteria2\",\"server\":\"example.com\",\"server_port\":443,\"password\":\"pw\"}")]
    public void SingBoxShareLinks_UseRealDelay(string config)
    {
        Assert.Equal(PreConnectLatencyMode.RealDelaySingBox, PreConnectLatencyPlan.ForV2RayConfig(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"outbounds\":[]}")]
    public void ConfigsWithoutAProbe_AreUnsupported_SoTheyAreNotReportedAsServerTcp(string config)
    {
        Assert.Equal(PreConnectLatencyMode.Unsupported, PreConnectLatencyPlan.ForV2RayConfig(config));
        Assert.False(PreConnectLatencyPlan.UsesRealDelay(config));
    }

    [Fact]
    public void WebSocketVless_IsNotProbedWithSingBox()
    {
        var config = "vless://11111111-1111-1111-1111-111111111111@example.com:443?type=ws&security=tls";
        Assert.NotEqual(PreConnectLatencyMode.RealDelaySingBox, PreConnectLatencyPlan.ForV2RayConfig(config));
        Assert.True(V2RayCoreSelector.RequiresXray(config));
        Assert.True(V2RayCoreSelector.IsWebSocketV2RayShareLink(config));
    }
}

public class Socks5LatencyProbeTests
{
    [Theory]
    [InlineData("HTTP/1.1 204 No Content", 204)]
    [InlineData("HTTP/1.0 200 OK", 200)]
    [InlineData("HTTP/1.1 404 Not Found", 404)]
    [InlineData("HTTP/2 204", 204)]
    public void HttpStatusLine_ParsesRealResponses(string line, int expected)
    {
        Assert.True(Socks5LatencyProbe.TryParseHttpStatusLine(line, out var status));
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("HTTP/1.1 OK")]
    [InlineData("HTTP/1.1 99")]
    public void HttpStatusLine_RejectsNonResponses(string line)
    {
        Assert.False(Socks5LatencyProbe.TryParseHttpStatusLine(line, out _));
    }

    [Fact]
    public async Task PlainHttp204_IsSuccess()
    {
        var ms = await WithSocksServerAsync(80, async stream =>
        {
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(response);
            await DrainUntilClientFinishesAsync(stream);
        });

        Assert.InRange(ms, 0, 3000);
    }

    [Fact]
    public async Task TlsHttp204_IsSuccess()
    {
        using var cert = CreateCert("www.google.com");
        var ms = await WithSocksServerAsync(443, async stream =>
        {
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            });

            var buffer = new byte[1024];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await ssl.ReadAsync(buffer.AsMemory(used, buffer.Length - used));
                if (read == 0)
                    break;
                used += read;
                if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }

            var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(response);
        });

        Assert.InRange(ms, 0, 5000);
    }

    [Theory]
    [InlineData(443)]
    [InlineData(80)]
    [InlineData(9)]
    public async Task CloseAfterSocksSuccess_IsNotALatency(int port)
    {
        await AssertNotALatencyAsync(port, stream =>
        {
            stream.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ResetAfterSocksSuccess_IsNotALatency()
    {
        await AssertNotALatencyAsync(443, stream =>
        {
            stream.Socket.LingerState = new LingerOption(true, 0);
            stream.Socket.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SingleTlsLookingByte_IsNotALatency()
    {
        await AssertNotALatencyAsync(443, async stream =>
        {
            await stream.WriteAsync(new byte[] { 0x16 });
            stream.Close();
        });
    }

    [Fact]
    public async Task GarbageInsteadOfHttp_IsNotALatency()
    {
        await AssertNotALatencyAsync(80, async stream =>
        {
            await stream.WriteAsync("hello\r\n"u8.ToArray());
            await DrainUntilClientFinishesAsync(stream);
        });
    }

    [Fact]
    public async Task SocksConnectFailure_IsNotALatency()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithSocksServerAsync(
            443,
            _ => Task.CompletedTask,
            connectReply: 0x05));

        Assert.Contains("SOCKS5 connect failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePayloadByteOnCustomPort_IsSuccess()
    {
        var ms = await WithSocksServerAsync(9, async stream =>
        {
            await stream.WriteAsync(new byte[] { 0x48 });
            await DrainUntilClientFinishesAsync(stream);
        });

        Assert.InRange(ms, 0, 3000);
    }

    private static async Task AssertNotALatencyAsync(int port, Func<NetworkStream, Task> afterSocks)
    {
        try
        {
            var ms = await WithSocksServerAsync(port, afterSocks);
            Assert.Fail($"upstream failure was reported as {ms} ms");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }

    private static async Task<long> WithSocksServerAsync(
        int targetPort,
        Func<NetworkStream, Task> afterSocks,
        byte connectReply = 0x00)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            var stream = client.GetStream();
            await ReadExactlyAsync(stream, new byte[3]);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var head = new byte[4];
            await ReadExactlyAsync(stream, head);
            var extra = head[3] switch
            {
                0x01 => 6,
                0x04 => 18,
                0x03 => 1,
                _ => throw new InvalidOperationException($"unexpected atyp {head[3]}")
            };
            if (head[3] == 0x03)
            {
                var len = new byte[1];
                await ReadExactlyAsync(stream, len);
                await ReadExactlyAsync(stream, new byte[len[0] + 2]);
            }
            else
            {
                await ReadExactlyAsync(stream, new byte[extra]);
            }

            await stream.WriteAsync(new byte[] { 0x05, connectReply, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            if (connectReply != 0x00)
                return;

            await afterSocks(stream);
        });

        try
        {
            return await Socks5LatencyProbe.MeasureAsync(
                "www.google.com",
                targetPort,
                proxyPort,
                CancellationToken.None,
                probeTimeoutMs: 4000);
        }
        finally
        {
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* the client may close before the server finishes */ }
            listener.Stop();
        }
    }

    private static async Task DrainUntilClientFinishesAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }
        }
        catch
        {
            // The probe closes as soon as it has a result.
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0)
                throw new IOException("test socks client closed");
            offset += read;
        }
    }

    private static X509Certificate2 CreateCert(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }
}
