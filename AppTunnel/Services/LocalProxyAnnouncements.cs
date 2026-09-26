using TunnelX.ProxyLifecycle;

namespace AppTunnel.Services;

/// <summary>
/// Announces local SOCKS/HTTP listener lifecycle to the in-app log and
/// <c>%LOCALAPPDATA%\TunnelX\proxy-lifecycle.log</c>.
/// </summary>
internal static class LocalProxyAnnouncements
{
    public const string ListenHost = "127.0.0.1";

    private static readonly ProxyLifecycleAnnouncer Announcer = new(Publish);

    public static void Connected(params int[] ports) => Announce(ProxyListenState.Connected, ports);

    public static void Disconnected(params int[] ports) => Announce(ProxyListenState.Disconnected, ports);

    private static void Announce(ProxyListenState state, int[] ports)
    {
        foreach (var port in ports)
            Announcer.Announce(ListenHost, port, state);
    }

    private static void Publish(string line)
    {
        Logger.Info("[PROXY] " + line);
        try
        {
            ProxyLifecycleLog.Append(line);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[PROXY] lifecycle log write failed: {ex.Message}");
        }
    }
}
