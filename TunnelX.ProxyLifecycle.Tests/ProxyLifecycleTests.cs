using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using TunnelX.ProxyLifecycle;
using Xunit;

namespace TunnelX.ProxyLifecycle.Tests;

public class ProxyLifecycleTests
{
    private static readonly Regex LogLine = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \S+:\d+ is now (connected|disconnected)$",
        RegexOptions.Compiled);

    [Theory]
    [InlineData(ProxyListenState.Disconnected, "127.0.0.1:1080 is now disconnected")]
    [InlineData(ProxyListenState.Connected, "127.0.0.1:1080 is now connected")]
    public void Format_uses_host_port_sentence(ProxyListenState state, string expected)
    {
        Assert.True(ProxyEndpointAnnouncement.TryFormat("127.0.0.1", 1080, state, out _, out var line));
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData("", 1080)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 65536)]
    public void Format_rejects_missing_endpoint(string host, int port)
    {
        Assert.False(ProxyEndpointAnnouncement.TryFormat(host, port, ProxyListenState.Disconnected, out _, out var line));
        Assert.Equal("", line);
    }

    [Fact]
    public void Announce_emits_disconnect_then_reconnect_and_suppresses_duplicates()
    {
        var published = new List<string>();
        var announcer = new ProxyLifecycleAnnouncer(published.Add);

        Assert.Equal("127.0.0.1:1080 is now connected", announcer.Announce("127.0.0.1", 1080, ProxyListenState.Connected));
        Assert.Null(announcer.Announce(" 127.0.0.1 ", 1080, ProxyListenState.Connected));
        Assert.Equal("127.0.0.1:1080 is now disconnected", announcer.Announce("127.0.0.1", 1080, ProxyListenState.Disconnected));
        Assert.Null(announcer.Announce("127.0.0.1", 1080, ProxyListenState.Disconnected));
        Assert.Equal("127.0.0.1:1080 is now connected", announcer.Announce("127.0.0.1", 1080, ProxyListenState.Connected));

        Assert.Equal(
            [
                "127.0.0.1:1080 is now connected",
                "127.0.0.1:1080 is now disconnected",
                "127.0.0.1:1080 is now connected"
            ],
            published);
    }

    [Fact]
    public void Announce_tracks_each_listener_port_independently()
    {
        var published = new List<string>();
        var announcer = new ProxyLifecycleAnnouncer(published.Add);

        announcer.Announce("127.0.0.1", 1080, ProxyListenState.Connected);
        announcer.Announce("127.0.0.1", 2080, ProxyListenState.Connected);
        announcer.Announce("127.0.0.1", 2081, ProxyListenState.Disconnected);
        announcer.Announce("127.0.0.1", 1080, ProxyListenState.Disconnected);

        Assert.Equal(
            [
                "127.0.0.1:1080 is now connected",
                "127.0.0.1:2080 is now connected",
                "127.0.0.1:2081 is now disconnected",
                "127.0.0.1:1080 is now disconnected"
            ],
            published);
    }

    [Fact]
    public void Log_appends_timestamped_announcements_and_trims()
    {
        var path = Path.Combine(Path.GetTempPath(), "tunnelx-proxy-lifecycle-" + Guid.NewGuid().ToString("N"), "proxy-lifecycle.log");
        try
        {
            ProxyLifecycleLog.Append("127.0.0.1:1080 is now disconnected", path, maxBytes: 10_000);
            ProxyLifecycleLog.Append("127.0.0.1:1080 is now connected", path, maxBytes: 10_000);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Matches(LogLine, lines[0]);
            Assert.EndsWith("127.0.0.1:1080 is now disconnected", lines[0]);
            Assert.EndsWith("127.0.0.1:1080 is now connected", lines[1]);

            for (var i = 0; i < 40; i++)
            {
                var state = i % 2 == 0 ? "disconnected" : "connected";
                ProxyLifecycleLog.Append($"127.0.0.1:{20000 + i} is now {state}", path, maxBytes: 500);
            }

            var trimmed = File.ReadAllText(path);
            Assert.True(trimmed.Length <= 500, $"trimmed log was {trimmed.Length} bytes");
            Assert.Contains("is now ", trimmed);
            foreach (var line in File.ReadAllLines(path))
                Assert.Matches(LogLine, line);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AbortAll_resets_the_client_socket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var accepted = await listener.AcceptTcpClientAsync();

            var sessions = new ProxyClientSessionTable();
            sessions.Track(7, accepted);
            Assert.Equal(1, sessions.ActiveCount);

            var readTask = Task.Run(async () =>
            {
                var buffer = new byte[8];
                try
                {
                    return await client.GetStream().ReadAsync(buffer);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    return 0;
                }
            });

            Assert.Equal(1, sessions.AbortAll());
            Assert.Equal(0, sessions.ActiveCount);

            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(readTask, completed);
            Assert.Equal(0, await readTask);
        }
        finally
        {
            listener.Stop();
        }
    }
}
