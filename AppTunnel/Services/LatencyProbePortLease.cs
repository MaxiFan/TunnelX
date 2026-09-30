using System.Collections.Concurrent;
using System.IO;

namespace AppTunnel.Services;

/// <summary>
/// Exclusive loopback port for a temporary xray/sing-box real-delay inbound.
/// The TCP reservation is released before the core binds (holding it would make the
/// probe talk to our listener). The port stays leased in this table until Dispose
/// so parallel probes cannot reuse it in that window.
/// </summary>
internal sealed class LatencyProbePortLease : IDisposable
{
    private static readonly ConcurrentDictionary<int, byte> InUse = new();

    public int Port { get; }

    private LatencyProbePortLease(int port)
    {
        Port = port;
    }

    public static LatencyProbePortLease Acquire(int preferredPort)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var excluded = InUse.Keys.ToArray();
            using var reservation = LocalPortReservation.ReservePreferredOrRandom(preferredPort, excluded);
            var port = reservation.Port;
            if (InUse.TryAdd(port, 0))
                return new LatencyProbePortLease(port);
        }

        throw new IOException("Could not lease a free local probe port.");
    }

    public void Dispose()
        => InUse.TryRemove(Port, out _);
}
