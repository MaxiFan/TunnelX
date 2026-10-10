using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AppTunnel.Services;

/// <summary>
/// Removes leftover TunnelX TUN addresses and routes that make sing-box fail with
/// "set ipv4 address: The object already exists", without touching another app's adapter.
/// </summary>
internal static class TunIpv4ConflictCleanup
{
    private const string NetworkClassKey =
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    private const string TcpipInterfacesKey =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private const string PersistentRoutesKey =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes";

    public static void RemoveStaleLeftovers(string reason)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rows = ReadEntries();
        var removedAddresses = 0;
        foreach (var row in rows)
        {
            if (!TunIpv4AddressPlan.IsStaleManagedAddress(row.Observed))
                continue;

            if (TryDeleteAddress(row))
            {
                removedAddresses++;
                Logger.Info(
                    $"[TUN] removed stale address {row.Observed.Address}/{row.Observed.PrefixLength} " +
                    $"alias='{row.Observed.InterfaceAlias ?? "(orphaned)"}' ifIdx={row.Observed.InterfaceIndex} ({reason})");
            }
        }

        var removedRoutes = TryDeleteStaleRoutes();
        var clearedProfiles = TryClearSavedTunnelProfiles();
        var clearedPersistent = TryClearPersistentRoutes();
        if (removedAddresses == 0 && removedRoutes == 0 && clearedProfiles == 0 && clearedPersistent == 0)
            Logger.Info($"[TUN] no stale TunnelX address or route to remove ({reason})");
    }

    public static IReadOnlyList<ObservedIpv4> ReadAddresses()
        => ReadEntries().Select(e => e.Observed).ToList();

    internal static bool UnicastTableContainsLoopback()
        => TryReadUnicastTable(out var rows) && ContainsLoopback(rows);

    private static List<AddressEntry> ReadEntries()
    {
        if (TryReadUnicastTable(out var native) && ContainsLoopback(native))
            return native;

        Logger.Warning("[TUN] IP Helper unicast table was not usable; trying Get-NetIPAddress");
        if (TryReadPowerShell(out var powershell) && ContainsLoopback(powershell))
            return powershell;

        Logger.Warning("[TUN] Get-NetIPAddress was not usable; using visible NetworkInterface addresses only");
        return ReadFromNetworkInterfaces();
    }

    private static bool ContainsLoopback(IEnumerable<AddressEntry> rows)
        => rows.Any(r => r.Observed.Address == "127.0.0.1");

    private static bool TryDeleteAddress(AddressEntry row)
    {
        if (row.Native.HasValue)
        {
            var native = row.Native.Value;
            if (IpHelperNative.DeleteUnicastIpAddressEntry(ref native) == 0)
                return true;
        }

        var alias = row.Observed.InterfaceAlias;
        if (TunIpv4AddressPlan.IsManagedTunInterfaceName(alias) &&
            TryNetshDeleteAddress(alias!, row.Observed.Address))
            return true;

        return row.Observed.InterfaceIndex > 0 &&
               TryPowerShellDeleteAddress(row.Observed.InterfaceIndex, row.Observed.Address);
    }

    private static int TryDeleteStaleRoutes()
    {
        IReadOnlyList<VpnDefaultRouteInspector.Ipv4RouteRow> routes;
        try
        {
            routes = VpnDefaultRouteInspector.ReadIpv4ForwardTable();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] route table read failed: {ex.Message}");
            return 0;
        }

        var aliases = new Dictionary<uint, (bool Present, string? Alias)>();
        var deleted = 0;
        foreach (var route in routes)
        {
            if (!aliases.TryGetValue(route.IfIndex, out var iface))
            {
                iface = ResolveInterface(route.IfIndex);
                aliases[route.IfIndex] = iface;
            }

            var destination = TunIpv4AddressPlan.FromForwardTableOrder(route.Dest);
            var mask = TunIpv4AddressPlan.FromForwardTableOrder(route.Mask);
            if (!TunIpv4AddressPlan.IsStaleManagedRoute(destination, mask, iface.Present, iface.Alias))
                continue;

            var native = new MIB_IPFORWARDROW
            {
                dwForwardDest = route.Dest,
                dwForwardMask = route.Mask,
                dwForwardNextHop = route.NextHop,
                dwForwardIfIndex = route.IfIndex,
                dwForwardType = 3,
                dwForwardProto = route.Proto,
                dwForwardMetric1 = route.Metric
            };

            try
            {
                if (IpHelperNative.DeleteIpForwardEntry(ref native) != 0)
                    continue;
            }
            catch
            {
                continue;
            }

            deleted++;
            Logger.Info(
                $"[TUN] removed stale route {TunIpv4AddressPlan.FormatIpv4(destination)}/" +
                $"{TunIpv4AddressPlan.FormatIpv4(mask)} ifIdx={route.IfIndex} alias='{iface.Alias ?? "(orphaned)"}'");
        }

        return deleted;
    }

    private static int TryClearSavedTunnelProfiles()
    {
        var cleared = 0;
        try
        {
            using var network = Registry.LocalMachine.OpenSubKey(NetworkClassKey);
            if (network == null)
                return 0;

            foreach (var guid in network.GetSubKeyNames())
            {
                if (!Guid.TryParse(guid, out _))
                    continue;

                string? name;
                using (var connection = network.OpenSubKey(guid + @"\Connection"))
                    name = connection?.GetValue("Name") as string;

                if (!TunIpv4AddressPlan.IsManagedTunInterfaceName(name))
                    continue;

                using var ipKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesKey + @"\" + guid, writable: true);
                if (ipKey == null || !HasSavedAddress(ipKey))
                    continue;

                DeleteIfPresent(ipKey, "IPAddress");
                DeleteIfPresent(ipKey, "SubnetMask");
                DeleteIfPresent(ipKey, "DhcpIPAddress");
                DeleteIfPresent(ipKey, "DhcpSubnetMask");
                cleared++;
                Logger.Info($"[TUN] cleared saved IPv4 profile for '{name}' {{{guid}}}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] saved TUN profile cleanup failed: {ex.Message}");
        }

        return cleared;
    }

    private static int TryClearPersistentRoutes()
    {
        var cleared = 0;
        try
        {
            using var routes = Registry.LocalMachine.OpenSubKey(PersistentRoutesKey, writable: true);
            if (routes == null)
                return 0;

            foreach (var name in routes.GetValueNames())
            {
                if (!TunIpv4AddressPlan.IsManagedPersistentRoute(name))
                    continue;

                routes.DeleteValue(name, throwOnMissingValue: false);
                cleared++;
                Logger.Info($"[TUN] removed persistent route '{name}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] persistent route cleanup failed: {ex.Message}");
        }

        return cleared;
    }

    private static bool HasSavedAddress(RegistryKey key)
    {
        var address = key.GetValue("IPAddress");
        if (address is string[] many && many.Any(v => !string.IsNullOrWhiteSpace(v) && v != "0.0.0.0"))
            return true;
        if (address is string one && !string.IsNullOrWhiteSpace(one) && one != "0.0.0.0")
            return true;

        var dhcp = key.GetValue("DhcpIPAddress") as string;
        return !string.IsNullOrWhiteSpace(dhcp) && dhcp != "0.0.0.0";
    }

    private static void DeleteIfPresent(RegistryKey key, string valueName)
    {
        try
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] could not clear {valueName}: {ex.Message}");
        }
    }

    private static (bool Present, string? Alias) ResolveInterface(uint ifIndex)
    {
        if (ifIndex == 0)
            return (false, null);

        if (IpHelperNative.ConvertInterfaceIndexToLuid(ifIndex, out var luid) != 0)
            return (false, null);

        var alias = new StringBuilder(256);
        var status = IpHelperNative.ConvertInterfaceLuidToAlias(ref luid, alias, (nuint)alias.Capacity);
        if (status != 0 || alias.Length == 0)
            return (true, null);

        return (true, alias.ToString());
    }

    private static bool TryReadUnicastTable(out List<AddressEntry> rows)
    {
        rows = [];
        var table = IntPtr.Zero;
        try
        {
            if (IpHelperNative.GetUnicastIpAddressTable(IpHelperNative.AfInet, out table) != 0 || table == IntPtr.Zero)
                return false;

            var count = Marshal.ReadInt32(table);
            if (count <= 0 || count > 4096)
                return false;

            var rowSize = Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>();
            if (!TryCalibrateRowStart(table, count, rowSize, out var start))
                return false;

            for (var i = 0; i < count; i++)
            {
                var ptr = IntPtr.Add(table, start + (i * rowSize));
                if (!TryReadObserved(ptr, out var observed))
                    continue;

                var native = Marshal.PtrToStructure<MIB_UNICASTIPADDRESS_ROW>(ptr);
                rows.Add(new AddressEntry(observed, native));
            }

            return rows.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] GetUnicastIpAddressTable failed: {ex.Message}");
            rows = [];
            return false;
        }
        finally
        {
            if (table != IntPtr.Zero)
                IpHelperNative.FreeMibTable(table);
        }
    }

    private static bool TryCalibrateRowStart(IntPtr table, int count, int rowSize, out int start)
    {
        foreach (var candidate in Environment.Is64BitProcess ? new[] { 8, 4 } : new[] { 4, 8 })
        {
            var limit = Math.Min(count, 64);
            for (var i = 0; i < limit; i++)
            {
                var ptr = IntPtr.Add(table, candidate + (i * rowSize));
                if (Marshal.ReadInt16(ptr) != IpHelperNative.AfInet)
                    continue;

                var ip = ReadIpv4(ptr);
                var prefix = Marshal.ReadByte(ptr, PrefixLengthOffset);
                var ifIndex = (uint)Marshal.ReadInt32(ptr, InterfaceIndexOffset);
                if (ip == "127.0.0.1" && prefix is >= 1 and <= 32 && ifIndex is > 0 and < 16_000_000)
                {
                    start = candidate;
                    return true;
                }
            }
        }

        start = 0;
        return false;
    }

    private static readonly int Ipv4Offset =
        Marshal.OffsetOf<SOCKADDR_INET_TUN>(nameof(SOCKADDR_INET_TUN.Ipv4)).ToInt32();

    private static readonly int PrefixLengthOffset =
        Marshal.OffsetOf<MIB_UNICASTIPADDRESS_ROW>(nameof(MIB_UNICASTIPADDRESS_ROW.OnLinkPrefixLength)).ToInt32();

    private static readonly int InterfaceIndexOffset =
        Marshal.OffsetOf<MIB_UNICASTIPADDRESS_ROW>(nameof(MIB_UNICASTIPADDRESS_ROW.InterfaceIndex)).ToInt32();

    private static bool TryReadObserved(IntPtr ptr, out ObservedIpv4 observed)
    {
        observed = default;
        if (Marshal.ReadInt16(ptr) != IpHelperNative.AfInet)
            return false;

        var address = ReadIpv4(ptr);
        if (address == null)
            return false;

        var prefix = Marshal.ReadByte(ptr, PrefixLengthOffset);
        var ifIndex = (uint)Marshal.ReadInt32(ptr, InterfaceIndexOffset);
        var (present, alias) = ResolveInterface(ifIndex);
        observed = new ObservedIpv4(address, prefix, ifIndex, alias, present);
        return true;
    }

    private static string? ReadIpv4(IntPtr row)
    {
        var bytes = new byte[4];
        Marshal.Copy(IntPtr.Add(row, Ipv4Offset), bytes, 0, 4);
        if (bytes.All(b => b == 0))
            return null;
        return new IPAddress(bytes).ToString();
    }

    private static bool TryReadPowerShell(out List<AddressEntry> rows)
    {
        rows = [];
        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object {" +
            "$alias=[string]$_.InterfaceAlias; if($alias){$alias=$alias.Replace('|','/')};" +
            "Write-Output ('{0}|{1}|{2}|{3}' -f $_.IPAddress,$_.PrefixLength,$_.InterfaceIndex,$alias)}";

        if (!TryRunPowerShell(script, TimeSpan.FromSeconds(12), out var stdout))
            return false;

        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 3)
                continue;
            if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (!int.TryParse(parts[1], out var prefix))
                continue;
            if (!uint.TryParse(parts[2], out var ifIndex))
                continue;

            var alias = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            var (present, resolved) = ResolveInterface(ifIndex);
            rows.Add(new AddressEntry(
                new ObservedIpv4(ip.ToString(), prefix, ifIndex, resolved ?? alias, present),
                null));
        }

        return rows.Count > 0;
    }

    private static List<AddressEntry> ReadFromNetworkInterfaces()
    {
        var rows = new List<AddressEntry>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                uint ifIndex = 0;
                try
                {
                    ifIndex = (uint)(props.GetIPv4Properties()?.Index ?? 0);
                }
                catch
                {
                    ifIndex = 0;
                }

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    rows.Add(new AddressEntry(
                        new ObservedIpv4(uni.Address.ToString(), uni.PrefixLength, ifIndex, nic.Name, true),
                        null));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] NetworkInterface address inventory failed: {ex.Message}");
        }

        return rows;
    }

    private static bool TryNetshDeleteAddress(string interfaceName, string address)
    {
        if (!IsSafeInterfaceName(interfaceName) || !IPAddress.TryParse(address, out _))
            return false;

        return TryRunProcess(
            "netsh",
            $"interface ipv4 delete address name=\"{interfaceName}\" address={address}",
            TimeSpan.FromSeconds(8));
    }

    private static bool TryPowerShellDeleteAddress(uint interfaceIndex, string address)
    {
        if (interfaceIndex == 0 || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var script =
            $"Remove-NetIPAddress -InterfaceIndex {interfaceIndex} -IPAddress {ip} -Confirm:$false -ErrorAction Stop";
        return TryRunPowerShell(script, TimeSpan.FromSeconds(10), out _);
    }

    private static bool IsSafeInterfaceName(string name)
        => TunIpv4AddressPlan.IsManagedTunInterfaceName(name) &&
           name.IndexOfAny(['"', '\r', '\n', '&', '|', '%']) < 0;

    private static bool TryRunPowerShell(string script, TimeSpan timeout, out string stdout)
    {
        stdout = "";
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Logger.Warning("[TUN] PowerShell address cleanup timed out");
                return false;
            }

            stdout = outputTask.GetAwaiter().GetResult();
            var stderr = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                Logger.Warning($"[TUN] PowerShell exit={process.ExitCode} stderr={stderr.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] PowerShell failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryRunProcess(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TUN] {fileName} failed: {ex.Message}");
            return false;
        }
    }

    private readonly record struct AddressEntry(ObservedIpv4 Observed, MIB_UNICASTIPADDRESS_ROW? Native);
}

[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct SOCKADDR_INET_TUN
{
    [FieldOffset(0)] public ushort Family;
    [FieldOffset(4)] public uint Ipv4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UNICASTIPADDRESS_ROW
{
    public SOCKADDR_INET_TUN Address;
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public uint PrefixOrigin;
    public uint SuffixOrigin;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public byte OnLinkPrefixLength;
    public byte SkipAsSource;
    public uint DadState;
    public uint ScopeId;
    public long CreationTimeStamp;
}
