using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Dispatcher: selects the correct ITunnelProvider based on ServerConfig.TunnelType
/// and delegates all connection operations to it.
/// </summary>
public class VpnService
{
    private ITunnelProvider? _activeProvider;
    private readonly ConnectionStatus _defaultStatus = new();

    public Action? OnTunnelFailed { get; set; }

    /// <summary>Live status, forwarded from the active provider.</summary>
    public ConnectionStatus Status => _activeProvider?.Status ?? _defaultStatus;

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct = default)
    {
        ConnectionProgressService.Report("cleanup", ConnectionProgressPhase.Active, "پاکسازی processهای قبلی TunnelX");
        await TunnelXCleanupService.CleanupAllAsync($"before connect {config.TunnelType}", ct);
        ConnectionProgressService.Report("cleanup", ConnectionProgressPhase.Complete, "پاکسازی processهای قبلی TunnelX");

        if (config.TunnelType == TunnelType.SocksProxy)
            config.V2RayConfig = config.BuildProxyUri();

        _activeProvider = config.TunnelType switch
        {
            TunnelType.L2tpIpsec => new L2tpTunnelProvider(),
            TunnelType.V2Ray => TunnelProviderFactory.Create(config.V2RayConfig),
            TunnelType.OpenVpn => new OpenVpnTunnelProvider(),
            TunnelType.SocksProxy => new V2RayTunnelProvider(),
            TunnelType.WireGuard => new WireGuardTunnelProvider(),
            _ => throw new NotImplementedException(LocalizationService.Instance.Format("نوع تانل ناشناخته: {0}", config.TunnelType))
        };

        // Wire up the tunnel-failure watchdog for sing-box-backed providers.
        if (_activeProvider is V2RayTunnelProvider v2r)
            v2r.OnTunnelFailed = OnTunnelFailed;
        else if (_activeProvider is XrayTunnelProvider xray)
            xray.OnTunnelFailed = OnTunnelFailed;
        else if (_activeProvider is WireGuardTunnelProvider wg)
            wg.OnTunnelFailed = OnTunnelFailed;
        else if (_activeProvider is OpenVpnTunnelProvider ovpn)
            ovpn.OnTunnelFailed = OnTunnelFailed;

        return await _activeProvider.ConnectAsync(config, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_activeProvider != null)
            await _activeProvider.DisconnectAsync();

        await TunnelXCleanupService.CleanupAllAsync("after disconnect", ct);
        _activeProvider = null;
    }

    public bool IsInterfaceUp() => _activeProvider?.IsInterfaceUp() ?? false;

    public void PrepareOpenVpnDropMessageIfNeeded() =>
        (_activeProvider as OpenVpnTunnelProvider)?.PrepareDropStatusIfNeeded();

    public string? GetOpenVpnDisconnectDialogTitle() =>
        (_activeProvider as OpenVpnTunnelProvider)?.GetDisconnectDialogTitle();

    public string? GetOpenVpnDisconnectShortStatus() =>
        _activeProvider is OpenVpnTunnelProvider o
            ? OpenVpnDisconnectInsight.BuildShortStatus(o.LastDisconnectReason)
            : null;
}
