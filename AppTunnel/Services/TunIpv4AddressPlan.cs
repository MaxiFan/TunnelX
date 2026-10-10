using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Picks a TUN IPv4 prefix that does not collide with an address already on Windows.
/// sing-box fails with "set ipv4 address: The object already exists" (Win32 183 from
/// CreateUnicastIpAddressEntry) when the configured host is already assigned, including
/// on a non-Wintun adapter that ncpa.cpl / the Wintun inventory does not mention.
/// </summary>
internal static class TunIpv4AddressPlan
{
    public const string PrimaryInterfaceName = "TunnelX-V2Ray";

    public static readonly TunIpv4Prefix DefaultPrefix = new("172.18.0.1", 30);

    /// <summary>
    /// /30s TunnelX is willing to own. 172.18.0.1/30 stays first so a clean machine
    /// keeps the historical address. Later prefixes cover the Docker/Hyper-V/WSL
    /// range that commonly already owns 172.18.0.0/16.
    /// </summary>
    public static readonly TunIpv4Prefix[] Candidates =
    [
        DefaultPrefix,
        new("172.19.0.1", 30),
        new("172.20.0.1", 30),
        new("172.21.0.1", 30),
        new("172.22.0.1", 30),
        new("172.23.0.1", 30),
        new("172.24.0.1", 30),
        new("172.25.0.1", 30),
        new("172.26.0.1", 30),
        new("172.27.0.1", 30),
        new("172.28.0.1", 30),
        new("172.29.0.1", 30),
        new("172.30.0.1", 30),
        new("172.31.0.1", 30),
        new("10.253.0.1", 30),
        new("10.254.0.1", 30),
    ];

    public static readonly string[] InterfaceNames =
    [
        PrimaryInterfaceName,
        PrimaryInterfaceName + "-2",
        PrimaryInterfaceName + "-3",
    ];

    private static readonly HashSet<string> ManagedHosts = new(
        Candidates.Select(c => c.Host),
        StringComparer.OrdinalIgnoreCase);

    public static TunIpv4Prefix Select(
        IEnumerable<ObservedIpv4> observed,
        IReadOnlySet<string>? burnedHosts = null)
    {
        var rows = observed as IReadOnlyList<ObservedIpv4> ?? observed.ToList();
        TunIpv4Prefix? firstAvailable = null;
        foreach (var candidate in Candidates)
        {
            if (burnedHosts != null && burnedHosts.Contains(candidate.Host))
                continue;

            firstAvailable ??= candidate;
            if (!rows.Any(row => Conflicts(candidate, row)))
                return candidate;
        }

        return firstAvailable ?? DefaultPrefix;
    }

    public static string SelectInterfaceName(IReadOnlySet<string>? blockedNames = null)
    {
        foreach (var name in InterfaceNames)
        {
            if (blockedNames == null || !blockedNames.Contains(name))
                return name;
        }

        return InterfaceNames[^1];
    }

    public static bool Conflicts(TunIpv4Prefix candidate, ObservedIpv4 row)
    {
        if (!TryParse(row.Address, out var other))
            return false;

        if (row.PrefixLength is < 1 or > 32)
            return other == candidate.HostAddress;

        return candidate.Overlaps(other, row.PrefixLength);
    }

    /// <summary>
    /// Safe to delete: the host is one TunnelX itself assigns, and the interface is
    /// either gone or still named as a TunnelX TUN adapter. Addresses on Ethernet,
    /// vEthernet, Docker, or another VPN are left alone.
    /// </summary>
    public static bool IsStaleManagedAddress(ObservedIpv4 row)
    {
        if (!IsManagedHost(row.Address))
            return false;

        if (!row.InterfacePresent)
            return true;

        if (string.IsNullOrWhiteSpace(row.InterfaceAlias))
            return false;

        return IsManagedTunInterfaceName(row.InterfaceAlias);
    }

    public static bool IsStaleManagedRoute(uint destination, uint mask, bool interfacePresent, string? alias)
    {
        if (!IsManagedDestination(destination, mask))
            return false;

        if (!interfacePresent)
            return true;

        if (string.IsNullOrWhiteSpace(alias))
            return false;

        return IsManagedTunInterfaceName(alias);
    }

    public static bool IsManagedPersistentRoute(string? valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName))
            return false;

        var parts = valueName.Split(',');
        if (parts.Length < 2)
            return false;

        return TryParse(parts[0].Trim(), out var destination) &&
               TryParse(parts[1].Trim(), out var mask) &&
               IsManagedDestination(destination, mask);
    }

    public static bool IsManagedHost(string? address)
        => !string.IsNullOrWhiteSpace(address) && ManagedHosts.Contains(address);

    public static bool IsManagedTunInterfaceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Equals(PrimaryInterfaceName, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(PrimaryInterfaceName + "-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? text, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text) ||
            !IPAddress.TryParse(text.Trim(), out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        address = BinaryPrimitivesRead(ip.GetAddressBytes());
        return true;
    }

    public static uint MaskOf(int prefixLength)
    {
        if (prefixLength <= 0)
            return 0;
        if (prefixLength >= 32)
            return uint.MaxValue;
        return uint.MaxValue << (32 - prefixLength);
    }

    /// <summary>
    /// GetIpForwardTable stores IPv4 addresses as little-endian DWORDs of network-order bytes.
    /// Candidate prefixes in this type use numeric big-endian values from <see cref="TryParse"/>.
    /// </summary>
    public static uint FromForwardTableOrder(uint raw)
    {
        var bytes = BitConverter.GetBytes(raw);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static string FormatIpv4(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(address >> 24);
        bytes[1] = (byte)(address >> 16);
        bytes[2] = (byte)(address >> 8);
        bytes[3] = (byte)address;
        return new IPAddress(bytes).ToString();
    }

    private static bool IsManagedDestination(uint destination, uint mask)
    {
        foreach (var candidate in Candidates)
        {
            if (destination == candidate.Network && mask == candidate.Mask)
                return true;
            if (destination == candidate.HostAddress && mask == uint.MaxValue)
                return true;
        }

        return false;
    }

    private static uint BinaryPrimitivesRead(byte[] bytes)
        => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}

internal readonly record struct TunIpv4Prefix(string Host, int Length)
{
    public string Cidr => Host + "/" + Length.ToString(CultureInfo.InvariantCulture);

    public uint HostAddress => TunIpv4AddressPlan.TryParse(Host, out var address) ? address : 0;

    public uint Mask => TunIpv4AddressPlan.MaskOf(Length);

    public uint Network => HostAddress & Mask;

    public bool Overlaps(uint otherAddress, int otherLength)
    {
        if (Length is < 1 or > 32 || otherLength is < 1 or > 32)
            return false;

        var otherMask = TunIpv4AddressPlan.MaskOf(otherLength);
        var common = Mask & otherMask;
        return (Network & common) == ((otherAddress & otherMask) & common);
    }
}

internal readonly record struct ObservedIpv4(
    string Address,
    int PrefixLength,
    uint InterfaceIndex,
    string? InterfaceAlias,
    bool InterfacePresent);
