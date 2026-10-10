using System.Collections.Concurrent;

namespace AppTunnel.Services;

/// <summary>
/// Maps a client socket to the destination it originally connected to, and to
/// the address the VPN-side socket should actually dial (DNS may differ).
/// </summary>
internal sealed class SplitForwarderSessions
{
    internal readonly record struct Session(
        uint OriginalDestIp,
        ushort OriginalDestPort,
        uint ForwardDestIp,
        ushort ForwardDestPort,
        uint OriginalClientIp,
        uint OriginalIfIdx,
        string? ProcessName);

    private readonly ConcurrentDictionary<(byte Proto, uint ClientIp, ushort ClientPort), Session> _sessions = new();

    public void Register(
        byte protocol,
        uint clientIp,
        ushort clientPort,
        uint originalDestIp,
        ushort originalDestPort,
        uint forwardDestIp,
        ushort forwardDestPort,
        uint originalIfIdx = 0,
        string? processName = null)
    {
        if (string.IsNullOrWhiteSpace(processName) &&
            _sessions.TryGetValue((protocol, clientIp, clientPort), out var existing))
            processName = existing.ProcessName;

        var session = new Session(
            originalDestIp,
            originalDestPort,
            forwardDestIp,
            forwardDestPort,
            clientIp,
            originalIfIdx,
            processName);
        _sessions[(protocol, clientIp, clientPort)] = session;

        // Windows may deliver the redirected socket as 127.0.0.1:<clientPort>
        // even though the app's packet used the physical source address.
        var loopback = Ipv4PacketRewrite.LoopbackNbo;
        if (clientIp != loopback)
            _sessions[(protocol, loopback, clientPort)] = session;
    }

    public bool TryGet(byte protocol, uint clientIp, ushort clientPort, out Session session)
        => _sessions.TryGetValue((protocol, clientIp, clientPort), out session);

    public bool Remove(byte protocol, uint clientIp, ushort clientPort)
    {
        var removed = _sessions.TryRemove((protocol, clientIp, clientPort), out var session);
        var loopback = Ipv4PacketRewrite.LoopbackNbo;
        if (clientIp != loopback)
        {
            _sessions.TryRemove((protocol, loopback, clientPort), out _);
        }
        else if (removed && session.OriginalClientIp != loopback)
        {
            _sessions.TryRemove((protocol, session.OriginalClientIp, clientPort), out _);
        }

        return removed;
    }
}
