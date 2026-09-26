using System.Collections.Concurrent;
using System.Net.Sockets;

namespace TunnelX.ProxyLifecycle;

/// <summary>
/// Tracks client sockets accepted by a local proxy and resets them when the
/// tunnel drops. A TCP reset unblocks readers such as Telethon's
/// <c>run_until_disconnected()</c>, which wait on the proxy connection
/// rather than on a separate control channel.
/// </summary>
public sealed class ProxyClientSessionTable
{
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();

    public int ActiveCount => _clients.Count;

    public void Track(long id, TcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[id] = client;
    }

    public void Untrack(long id) => _clients.TryRemove(id, out _);

    /// <summary>RST every tracked client and drop it from the table.</summary>
    public int AbortAll()
    {
        var aborted = 0;
        foreach (var id in _clients.Keys)
        {
            if (!_clients.TryRemove(id, out var client))
                continue;
            Abort(client);
            aborted++;
        }

        return aborted;
    }

    public static void Abort(TcpClient client)
    {
        try
        {
            client.LingerState = new LingerOption(true, 0);
        }
        catch
        {
            // Socket may already be closed.
        }

        try
        {
            client.Dispose();
        }
        catch
        {
            // Dispose is idempotent; ignore races with the session handler.
        }
    }
}
