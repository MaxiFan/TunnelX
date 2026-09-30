namespace AppTunnel.Services;

/// <summary>
/// Runs async work with a concurrency cap. Used so bulk real-delay tests do not start
/// one xray/sing-box probe per config at the same time.
/// </summary>
internal static class ParallelWorkLimit
{
    public static async Task ForEachAsync<T>(
        IReadOnlyList<T> items,
        int concurrency,
        Func<T, CancellationToken, Task> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);

        concurrency = LatencyTestLimits.Normalize(concurrency);
        if (items.Count == 0)
            return;

        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new Task[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            tasks[i] = RunOneAsync(item, gate, body, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task RunOneAsync<T>(
        T item,
        SemaphoreSlim gate,
        Func<T, CancellationToken, Task> body,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await body(item, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
