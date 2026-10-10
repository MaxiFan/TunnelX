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
        ushort ForwardDestPort);

    private readonly ConcurrentDictionary<(byte Proto, uint ClientIp, ushort ClientPort), Session> _sessions = new();

    public void Register(
        byte protocol,
        uint clientIp,
        ushort clientPort,
        uint originalDestIp,
        ushort originalDestPort,
        uint forwardDestIp,
        ushort forwardDestPort)
    {
        _sessions[(protocol, clientIp, clientPort)] = new Session(
            originalDestIp,
            originalDestPort,
            forwardDestIp,
            forwardDestPort);
    }

    public bool TryGet(byte protocol, uint clientIp, ushort clientPort, out Session session)
        => _sessions.TryGetValue((protocol, clientIp, clientPort), out session);

    public bool Remove(byte protocol, uint clientIp, ushort clientPort)
        => _sessions.TryRemove((protocol, clientIp, clientPort), out _);
}
