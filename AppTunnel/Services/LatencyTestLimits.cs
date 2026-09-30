namespace AppTunnel.Services;

/// <summary>
/// Bounds for how many V2Ray real-delay probes may run at once (each starts xray or sing-box).
/// </summary>
internal static class LatencyTestLimits
{
    public const int Default = 4;
    public const int Min = 1;
    public const int Max = 8;

    public static readonly IReadOnlyList<int> Options = Enumerable.Range(Min, Max - Min + 1).ToArray();

    /// <summary>
    /// Missing or non-positive persisted values become <see cref="Default"/>; otherwise clamp to Min..Max.
    /// </summary>
    public static int Normalize(int value)
        => value <= 0 ? Default : Math.Clamp(value, Min, Max);
}
