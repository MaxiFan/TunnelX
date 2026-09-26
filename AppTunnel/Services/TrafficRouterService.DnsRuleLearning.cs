using System.Net;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    private int _dnsRuleQueryLogCount;
    private int _dnsRuleApplyLogCount;

    private sealed class DnsRuleQuery
    {
        public required string Host { get; init; }
        public required bool Excluded { get; init; }
        public required bool Included { get; init; }
        public string? TargetOwner { get; init; }
        public DateTime LastSeenUtc { get; init; } = DateTime.UtcNow;
    }

    private void LearnTargetDnsQueryFromOutboundPacket(byte[] buffer, uint readLen, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return;
        if (!TryGetUdpPayload(buffer, readLen, out var payloadOffset, out var payloadLength, out var srcPort, out var dstPort))
            return;
        if (dstPort != 53 || payloadLength < 12)
            return;

        ushort txId = ReadUInt16(buffer, payloadOffset);
        if (!TryReadDnsQuestionHost(buffer, payloadOffset, payloadLength, out var host))
            return;

        // Per-app mode keeps Windows connectivity probes on the physical resolver.
        if (!_fullRouteEnabled && WindowsConnectivityGuard.IsProbeHost(host))
            return;

        uint dnsServerNbo = BitConverter.ToUInt32(buffer, 16);
        var key = BuildDnsRuleKey(txId, srcPort, dnsServerNbo);
        _dnsRuleQueries.AddOrUpdate(key, _ => new DnsRuleQuery
        {
            Host = host,
            Excluded = false,
            Included = false,
            TargetOwner = owner
        }, (_, existing) => new DnsRuleQuery
        {
            Host = existing.Host,
            Excluded = existing.Excluded,
            Included = existing.Included,
            TargetOwner = owner,
            LastSeenUtc = existing.LastSeenUtc
        });
    }

    private void LearnDnsRuleFromOutboundPacket(byte[] buffer, uint readLen)
    {
        if (!TryGetUdpPayload(buffer, readLen, out var payloadOffset, out var payloadLength, out var srcPort, out var dstPort))
            return;
        if (dstPort != 53 || payloadLength < 12)
            return;

        ushort txId = ReadUInt16(buffer, payloadOffset);
        if (!TryReadDnsQuestionHost(buffer, payloadOffset, payloadLength, out var host))
            return;

        if (!_fullRouteEnabled && WindowsConnectivityGuard.IsProbeHost(host))
            return;

        bool excluded = IsExcludedDomain(host);
        bool included = !excluded && IsIncludedDomain(host);
        if (!excluded && !included)
            return;

        if (Interlocked.Increment(ref _dnsRuleQueryLogCount) <= 20)
        {
            var policy = excluded ? "EXCLUDE" : "INCLUDE";
            Logger.Info($"[DNS-RULE] Query '{host}' matched {policy} domain rule");
        }

        uint dnsServerNbo = BitConverter.ToUInt32(buffer, 16);
        _dnsRuleQueries[BuildDnsRuleKey(txId, srcPort, dnsServerNbo)] = new DnsRuleQuery
        {
            Host = host,
            Excluded = excluded,
            Included = included
        };
    }

    private void ApplyDnsRuleFromInboundPacket(byte[] buffer, uint readLen)
    {
        if (!TryGetUdpPayload(buffer, readLen, out var payloadOffset, out var payloadLength, out var srcPort, out var dstPort))
            return;
        if (srcPort != 53 || payloadLength < 12)
            return;

        ushort txId = ReadUInt16(buffer, payloadOffset);
        uint dnsServerNbo = BitConverter.ToUInt32(buffer, 12);
        if (!_dnsRuleQueries.TryRemove(BuildDnsRuleKey(txId, dstPort, dnsServerNbo), out var query))
        {
            // DNS redirect may change the resolver IP between request and response.
            // The client port + transaction ID remain stable, so use them as a
            // fallback match.
            var prefix = $"{txId}:{dstPort}:";
            var fallback = _dnsRuleQueries.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(fallback.Key) ||
                !_dnsRuleQueries.TryRemove(fallback.Key, out query))
            {
                return;
            }
        }

        var learnedIps = ReadDnsAAnswers(buffer, payloadOffset, payloadLength)
            .Where(IsUsableRouteIpv4)
            .ToList();
        foreach (var ip in learnedIps)
        {
            var nbo = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            if (!_fullRouteEnabled && WindowsConnectivityGuard.IsProbeIpv4(nbo))
                continue;

            if (query.Excluded)
            {
                _excludedIps[nbo] = true;
                PurgeRouteForExcludedIp(nbo, ip);
                continue;
            }

            if (query.Included && !IsExcludedDestination(nbo))
            {
                _includedIps[nbo] = true;
                _ipToProcess[nbo] = "[INCLUDE]";
                QueueHostRouteViaVpn(nbo, ip);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query.TargetOwner) &&
                IsExecutableTargeted(query.TargetOwner) &&
                !IsExcludedDestination(nbo))
            {
                _ipToProcess[nbo] = query.TargetOwner;
                QueueHostRouteViaVpn(nbo, ip);
            }
        }

        if (learnedIps.Count > 0 && Interlocked.Increment(ref _dnsRuleApplyLogCount) <= 20)
        {
            var policy = query.Excluded ? "EXCLUDE" :
                query.Included ? "INCLUDE" : $"TARGET-DNS/{query.TargetOwner}";
            Logger.Info($"[DNS-RULE] Applied {policy} for '{query.Host}' → {string.Join(", ", learnedIps.Select(ip => ip.ToString()))}");
        }

        CleanupOldDnsRuleQueries();
    }

    private void CleanupOldDnsRuleQueries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (var item in _dnsRuleQueries)
        {
            if (item.Value.LastSeenUtc < cutoff)
                _dnsRuleQueries.TryRemove(item.Key, out _);
        }
    }

    private static string BuildDnsRuleKey(ushort txId, ushort clientPort, uint dnsServerNbo)
        => $"{txId}:{clientPort}:{dnsServerNbo}";

    /// <summary>
    /// True when this UDP/53 question is a Windows NCSI name. Split mode must
    /// not redirect it; the physical resolver is what NCSI is measuring.
    /// </summary>
    private bool IsSplitModeConnectivityProbeQuery(byte[] buffer, uint readLen)
    {
        if (_fullRouteEnabled)
            return false;
        if (!TryGetUdpPayload(buffer, readLen, out var payloadOffset, out var payloadLength, out _, out var dstPort))
            return false;
        if (dstPort != 53 || payloadLength < 12)
            return false;
        return TryReadDnsQuestionHost(buffer, payloadOffset, payloadLength, out var host) &&
               WindowsConnectivityGuard.IsProbeHost(host);
    }

    private void QueueHostRouteViaVpn(uint dstIpNbo, IPAddress dstIp)
    {
        _ = Task.Run(() => EnsureHostRouteViaVpn(dstIpNbo, dstIp));
    }

    private static bool TryGetUdpPayload(
        byte[] buffer,
        uint readLen,
        out int payloadOffset,
        out int payloadLength,
        out ushort srcPort,
        out ushort dstPort)
    {
        payloadOffset = 0;
        payloadLength = 0;
        srcPort = 0;
        dstPort = 0;

        if (readLen < 28 || (buffer[0] >> 4) != 4 || buffer[9] != 17)
            return false;

        int ipHeaderLen = (buffer[0] & 0x0F) * 4;
        if (ipHeaderLen < 20 || readLen < ipHeaderLen + 8)
            return false;

        srcPort = ReadUInt16(buffer, ipHeaderLen);
        dstPort = ReadUInt16(buffer, ipHeaderLen + 2);
        int udpLen = ReadUInt16(buffer, ipHeaderLen + 4);
        payloadOffset = ipHeaderLen + 8;
        payloadLength = Math.Min(udpLen - 8, (int)readLen - payloadOffset);
        return payloadLength >= 0;
    }

    private static bool TryReadDnsQuestionHost(byte[] buffer, int dnsOffset, int dnsLength, out string host)
    {
        host = "";
        if (dnsLength < 12)
            return false;

        bool isResponse = (buffer[dnsOffset + 2] & 0x80) != 0;
        int qdCount = ReadUInt16(buffer, dnsOffset + 4);
        if (isResponse || qdCount <= 0)
            return false;

        int pos = dnsOffset + 12;
        if (!TryReadDnsName(buffer, dnsOffset, dnsLength, ref pos, out host))
            return false;

        return pos + 4 <= dnsOffset + dnsLength;
    }

    private static IEnumerable<IPAddress> ReadDnsAAnswers(byte[] buffer, int dnsOffset, int dnsLength)
    {
        bool isResponse = (buffer[dnsOffset + 2] & 0x80) != 0;
        if (!isResponse)
            yield break;

        int qdCount = ReadUInt16(buffer, dnsOffset + 4);
        int anCount = ReadUInt16(buffer, dnsOffset + 6);
        int pos = dnsOffset + 12;

        for (int i = 0; i < qdCount; i++)
        {
            if (!TryReadDnsName(buffer, dnsOffset, dnsLength, ref pos, out _))
                yield break;
            pos += 4;
            if (pos > dnsOffset + dnsLength)
                yield break;
        }

        for (int i = 0; i < anCount; i++)
        {
            if (!TryReadDnsName(buffer, dnsOffset, dnsLength, ref pos, out _))
                yield break;
            if (pos + 10 > dnsOffset + dnsLength)
                yield break;

            ushort type = ReadUInt16(buffer, pos);
            ushort klass = ReadUInt16(buffer, pos + 2);
            ushort rdLen = ReadUInt16(buffer, pos + 8);
            pos += 10;
            if (pos + rdLen > dnsOffset + dnsLength)
                yield break;

            if (type == 1 && klass == 1 && rdLen == 4)
                yield return new IPAddress(new[] { buffer[pos], buffer[pos + 1], buffer[pos + 2], buffer[pos + 3] });

            pos += rdLen;
        }
    }

    private static bool TryReadDnsName(byte[] buffer, int dnsOffset, int dnsLength, ref int pos, out string name)
    {
        name = "";
        var labels = new List<string>();
        int limit = dnsOffset + dnsLength;
        int cursor = pos;
        int jumps = 0;
        bool jumped = false;

        while (cursor < limit)
        {
            byte len = buffer[cursor++];
            if (len == 0)
            {
                if (!jumped)
                    pos = cursor;
                name = string.Join(".", labels);
                return !string.IsNullOrWhiteSpace(name);
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor >= limit || ++jumps > 8)
                    return false;

                int pointer = ((len & 0x3F) << 8) | buffer[cursor++];
                if (!jumped)
                    pos = cursor;
                cursor = dnsOffset + pointer;
                jumped = true;
                continue;
            }

            if ((len & 0xC0) != 0 || cursor + len > limit)
                return false;

            labels.Add(System.Text.Encoding.ASCII.GetString(buffer, cursor, len));
            cursor += len;
        }

        return false;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
}
