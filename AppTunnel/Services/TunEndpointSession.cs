namespace AppTunnel.Services;

/// <summary>
/// One connect attempt's TUN address / interface choice, plus the decision to
/// retry after sing-box reports that the IPv4 address already exists.
/// </summary>
internal sealed class TunEndpointSession
{
    public const int MaxAttempts = 4;

    private readonly HashSet<string> _burnedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedInterfaceNames = new(StringComparer.OrdinalIgnoreCase);

    public TunIpv4Prefix Address { get; private set; } = TunIpv4AddressPlan.DefaultPrefix;

    public string InterfaceName { get; private set; } = TunIpv4AddressPlan.PrimaryInterfaceName;

    public int AddressConflicts { get; private set; }

    public void PrepareForAttempt(string reason)
    {
        try
        {
            TunIpv4ConflictCleanup.RemoveStaleLeftovers(reason);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] stale-address cleanup failed ({reason}): {ex.Message}");
        }

        IReadOnlyList<ObservedIpv4> observed;
        try
        {
            observed = TunIpv4ConflictCleanup.ReadAddresses();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] address inventory failed ({reason}): {ex.Message}");
            observed = Array.Empty<ObservedIpv4>();
        }

        Address = TunIpv4AddressPlan.Select(observed, _burnedHosts);
        InterfaceName = TunIpv4AddressPlan.SelectInterfaceName(_blockedInterfaceNames);
        Logger.Info($"[TUN] {reason}: address={Address.Cidr} interface='{InterfaceName}'");

        foreach (var row in observed)
        {
            if (!TunIpv4AddressPlan.Conflicts(TunIpv4AddressPlan.DefaultPrefix, row) &&
                !TunIpv4AddressPlan.Conflicts(Address, row))
                continue;

            Logger.Warning(
                $"[TUN] {row.Address}/{row.PrefixLength} on '{row.InterfaceAlias ?? "(no adapter)"}' " +
                $"ifIdx={row.InterfaceIndex} present={row.InterfacePresent} overlaps a TunnelX TUN prefix");
        }
    }

    public TunFailureDisposition Classify(string? logs, int attempt, bool processExited)
    {
        if (SingBoxTunFailure.ShouldRetryAsAddressConflict(logs))
        {
            AddressConflicts++;
            _burnedHosts.Add(Address.Host);
            if (AddressConflicts >= 2)
                _blockedInterfaceNames.Add(InterfaceName);

            Logger.Warning(
                $"[TUN] IPv4 assign failed for {Address.Cidr} on '{InterfaceName}' " +
                $"(conflict {AddressConflicts}); burned=[{string.Join(", ", _burnedHosts)}]");

            return AddressConflicts < MaxAttempts
                ? TunFailureDisposition.RetryAddress
                : TunFailureDisposition.FailAddressConflict;
        }

        if (attempt < 2 && SingBoxTunFailure.ShouldRetryTunOpen(logs, processExited))
            return TunFailureDisposition.RetryReadiness;

        return TunFailureDisposition.FailOther;
    }
}

internal enum TunFailureDisposition
{
    RetryAddress,
    RetryReadiness,
    FailAddressConflict,
    FailOther,
}
