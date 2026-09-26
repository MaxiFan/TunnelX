using AppTunnel.Models;

namespace AppTunnel.Services;

public sealed class SubscriptionSyncResult
{
    public bool Applied { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }
    public int Skipped { get; init; }
    public ConnectionProfile? FocusProfile { get; init; }
}

/// <summary>
/// Creates, updates, and removes profiles that belong to one subscription.
/// Profiles that disappeared from a successful fetch are removed. A fetch with no usable
/// configs is not applied, so a bad response cannot wipe an existing subscription.
/// </summary>
public static class SubscriptionSync
{
    public static SubscriptionSyncResult Apply(
        string subscriptionId,
        IReadOnlyList<ImportedConfigDraft> drafts,
        IList<ConnectionProfile> profiles)
    {
        var usable = drafts.Where(d => string.IsNullOrWhiteSpace(d.SkipReason)).ToList();
        var skipped = drafts.Count - usable.Count;
        if (usable.Count == 0 || string.IsNullOrWhiteSpace(subscriptionId))
        {
            return new SubscriptionSyncResult
            {
                Applied = false,
                Skipped = skipped
            };
        }

        var owned = profiles.Where(p => string.Equals(p.SubscriptionId, subscriptionId, StringComparison.Ordinal)).ToList();
        var consumed = new HashSet<ConnectionProfile>();
        var added = 0;
        var updated = 0;
        ConnectionProfile? focus = null;

        foreach (var draft in usable)
        {
            var incoming = ConfigImportService.CreateProfile(draft);
            var key = ConfigImportService.GetSubscriptionNodeKey(incoming);

            var match = owned.FirstOrDefault(p =>
                !consumed.Contains(p) &&
                !string.IsNullOrEmpty(key) &&
                string.Equals(p.SubscriptionNodeKey, key, StringComparison.Ordinal));

            if (match == null)
            {
                var nameMatches = owned
                    .Where(p => !consumed.Contains(p) && string.Equals(p.Name, draft.SuggestedName, StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                if (nameMatches.Count == 1)
                    match = nameMatches[0];
            }

            if (match != null)
            {
                CopyTunnelConfig(match, incoming);
                match.Name = draft.SuggestedName;
                match.SubscriptionId = subscriptionId;
                match.SubscriptionNodeKey = key;
                consumed.Add(match);
                updated++;
                focus ??= match;
                continue;
            }

            if (ConfigImportService.IsDuplicateConfig(incoming, profiles))
            {
                skipped++;
                continue;
            }

            incoming.SubscriptionId = subscriptionId;
            incoming.SubscriptionNodeKey = key;
            profiles.Add(incoming);
            consumed.Add(incoming);
            added++;
            focus = incoming;
        }

        var removed = 0;
        foreach (var stale in owned)
        {
            if (consumed.Contains(stale))
                continue;
            profiles.Remove(stale);
            removed++;
        }

        return new SubscriptionSyncResult
        {
            Applied = true,
            Added = added,
            Updated = updated,
            Removed = removed,
            Skipped = skipped,
            FocusProfile = focus
        };
    }

    public static void CopyTunnelConfig(ConnectionProfile target, ConnectionProfile source)
    {
        target.TunnelType = source.TunnelType;
        target.V2RayConfig = source.TunnelType == TunnelType.V2Ray ? source.V2RayConfig : "";
        target.OpenVpnConfig = source.TunnelType == TunnelType.OpenVpn ? source.OpenVpnConfig : "";
        target.OpenVpnConfigPath = source.TunnelType == TunnelType.OpenVpn ? source.OpenVpnConfigPath : "";
        target.WireGuardConfig = source.TunnelType == TunnelType.WireGuard ? source.WireGuardConfig : "";
        target.WireGuardConfigPath = source.TunnelType == TunnelType.WireGuard ? source.WireGuardConfigPath : "";
        target.ProxyProtocol = source.ProxyProtocol;
        target.ProxyServerAddress = source.TunnelType == TunnelType.SocksProxy ? source.ProxyServerAddress : "";
        target.ProxyPort = source.ProxyPort;
        target.ProxyUsername = source.TunnelType == TunnelType.SocksProxy ? source.ProxyUsername : "";
        target.ProxyPassword = source.TunnelType == TunnelType.SocksProxy ? source.ProxyPassword : "";
        target.ServerAddress = source.TunnelType == TunnelType.L2tpIpsec ? source.ServerAddress : target.ServerAddress;
        target.Username = source.TunnelType == TunnelType.L2tpIpsec ? source.Username : target.Username;
        target.Password = source.TunnelType == TunnelType.L2tpIpsec ? source.Password : target.Password;
        target.PreSharedKey = source.TunnelType == TunnelType.L2tpIpsec ? source.PreSharedKey : target.PreSharedKey;
        target.ResetLatencyResult();
        target.ResetServerPingResult();
    }
}
