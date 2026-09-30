using System.Collections.Concurrent;
using AppTunnel.Services;
using Xunit;

namespace TunnelX.LatencyTests;

public class LatencyTestLimitsTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-3, 4)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(99, 8)]
    public void Normalize_UsesDefaultForNonPositive_AndClamps(int input, int expected)
    {
        Assert.Equal(expected, LatencyTestLimits.Normalize(input));
    }
}

public class ParallelWorkLimitTests
{
    [Fact]
    public async Task ForEachAsync_RespectsConcurrencyCap()
    {
        var current = 0;
        var peak = 0;
        var started = new TaskCompletionSource();
        using var hold = new SemaphoreSlim(0);

        var items = Enumerable.Range(0, 6).ToArray();
        var run = ParallelWorkLimit.ForEachAsync(items, concurrency: 3, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            while (true)
            {
                var snapshot = Volatile.Read(ref peak);
                if (now <= snapshot || Interlocked.CompareExchange(ref peak, now, snapshot) == snapshot)
                    break;
            }

            if (now == 3)
                started.TrySetResult();

            await hold.WaitAsync(ct);
            Interlocked.Decrement(ref current);
        }, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, peak);

        hold.Release(6);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task ForEachAsync_CancelStopsWaitingWorkers()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        using var entered = new SemaphoreSlim(0);

        var run = ParallelWorkLimit.ForEachAsync(Enumerable.Range(0, 4).ToArray(), 1, async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            entered.Release();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }, cts.Token);

        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, started);
    }
}

public class LatencyProbePortLeaseTests
{
    [Fact]
    public void Acquire_SamePreferredPort_YieldsDistinctLeasedPorts()
    {
        const int preferred = 2101;
        var leases = new List<LatencyProbePortLease>();
        try
        {
            for (var i = 0; i < 5; i++)
                leases.Add(LatencyProbePortLease.Acquire(preferred));

            var ports = leases.Select(l => l.Port).ToArray();
            Assert.Equal(ports.Length, ports.Distinct().Count());
            Assert.Contains(preferred, ports);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public void Dispose_RemovesPortFromInUseTable()
    {
        int port;
        using (var lease = LatencyProbePortLease.Acquire(0))
            port = lease.Port;

        using var again = LatencyProbePortLease.Acquire(port);
        Assert.Equal(port, again.Port);
    }
}
