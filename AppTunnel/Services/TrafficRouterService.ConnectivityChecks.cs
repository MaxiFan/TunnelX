using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    // Well-known hostnames used by connectivity checks. Resolved at
    // runtime via DNS so no hardcoded IPs remain in the codebase.
    private const string IntranetCheckHost = "isna.ir";
    private const string InternationalCheckHost = "google.com";

    // When public health endpoints are disabled, CONN-CHECK follows the first custom target
    // instead of treating google.com as the international reachability signal.
    private bool _usePublicInternationalCheck = true;
    private string? _customHealthCheckHost;
    private int _customHealthCheckPort = 443;

    /// <summary>
    /// Aligns split/full-route diagnostics with the connection health-check settings.
    /// Public defaults keep the historical google.com checks. A custom-only plan probes that
    /// host through the tunnel and does not install a host route for private addresses.
    /// </summary>
    public void ConfigureHealthConnectivity(bool usePublicInternationalCheck, string? customHost, int customPort)
    {
        _customHealthCheckHost = string.IsNullOrWhiteSpace(customHost) ? null : customHost.Trim();
        _customHealthCheckPort = customPort is > 0 and <= 65535 ? customPort : 443;
        _usePublicInternationalCheck = usePublicInternationalCheck || _customHealthCheckHost == null;
    }

    private async Task RunConnectivityChecks()
    {
        try
        {
            // Give VPN a moment to fully settle.
            await Task.Delay(_vpnServerIsUdpOnly ? 3000 : 1000);

            using var ping = new System.Net.NetworkInformation.Ping();

            // 1. Reachability of the tunnel server.
            //    For TCP-based tunnels (V2Ray/Xray/OpenVPN-tcp): TCP-connect via the
            //    physical NIC. ICMP is useless because CDNs (Cloudflare, Arvancloud)
            //    never reply to pings. We connect by the original hostname so any
            //    per-resolve CDN IP rotation matches what sing-box actually uses.
            //
            //    For UDP-only tunnels (WireGuard, OpenVPN-udp): a TCP probe to the UDP port
            //    will ALWAYS time out, even when the tunnel is perfectly healthy.
            //    We do a best-effort UDP datagram instead — it cannot actually
            //    confirm WireGuard handshake success, but it at least won't lie.
            if (_vpnServerSkipBareTcpProbe)
            {
                Logger.Info($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort}: skipped (WebSocket/TLS transport — use CONN-VERIFY instead)");
            }
            else if (_vpnServerIsUdpOnly)
            {
                try
                {
                    var serverIp = await DnsResolverCache.ResolveFirstIpv4Async(_vpnServerHost, CancellationToken.None);
                    if (serverIp != null)
                    {
                        using var udp = new UdpClient(AddressFamily.InterNetwork);
                        udp.Connect(serverIp, _vpnServerPort);
                        var probe = new byte[] { 0x00 };
                        await udp.SendAsync(probe, probe.Length);
                        Logger.Info($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort} ({serverIp}, direct): probe sent — UDP is stateless; actual tunnel health is observed via traffic flow");
                    }
                    else
                    {
                        Logger.Warning($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort}: DNS resolution failed");
                    }
                }
                catch (Exception ex) { Logger.Warning($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort} probe failed: {ex.Message}"); }
            }
            else
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var tcpServer = new TcpClient();
                    using var serverCts = new System.Threading.CancellationTokenSource(3000);
                    await tcpServer.ConnectAsync(_vpnServerHost, _vpnServerPort, serverCts.Token);
                    sw.Stop();
                    Logger.Info($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort} (direct): {sw.ElapsedMilliseconds}ms — reachable");
                }
                catch (OperationCanceledException)
                {
                    Logger.Info($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort}: bare TCP timed out (3000ms) — normal for WebSocket-only proxies; see CONN-VERIFY");
                }
                catch (Exception ex) { Logger.Warning($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort} failed: {ex.Message}"); }
            }

            // 2. Resolve an Iranian intranet hostname and ping it via the
            //    default route (physical NIC). Confirms the local intranet
            //    still works while the VPN is connected.
            try
            {
                var intraIp = await DnsResolverCache.ResolveFirstIpv4Async(IntranetCheckHost, CancellationToken.None);
                if (intraIp != null)
                {
                    var reply = ping.Send(intraIp, 2000);
                    Logger.Info($"[CONN-CHECK] Ping intranet {IntranetCheckHost} ({intraIp}, default route): {reply.Status}, {reply.RoundtripTime}ms");
                }
                else
                    Logger.Warning($"[CONN-CHECK] DNS {IntranetCheckHost}: no IPv4 address");
            }
            catch (Exception ex) { Logger.Warning($"[CONN-CHECK] Ping intranet failed: {ex.Message}"); }

            // 3. Resolve an international hostname and ping it via the default
            //    route. This is diagnostic only: on some networks this can
            //    legitimately succeed without indicating full-route VPN mode.
            //    Custom-only health checks skip this google.com observation and
            //    use the configured target for the tunnel TCP probe instead.
            var intlHost = InternationalCheckHost;
            var intlPort = 443;
            var skipDefaultRoutePing = false;
            if (!_usePublicInternationalCheck && !string.IsNullOrWhiteSpace(_customHealthCheckHost))
            {
                intlHost = _customHealthCheckHost ?? InternationalCheckHost;
                intlPort = _customHealthCheckPort;
                skipDefaultRoutePing = true;
                Logger.Info($"[CONN-CHECK] Public health endpoints disabled — tunnel reachability target is {intlHost}:{intlPort}");
            }

            IPAddress? intlIp = null;
            try
            {
                intlIp = await DnsResolverCache.ResolveFirstIpv4Async(intlHost, CancellationToken.None);
            }
            catch { }

            if (intlIp != null)
            {
                uint intlNbo = BitConverter.ToUInt32(intlIp.GetAddressBytes(), 0);
                bool routeAlreadyPresent = _addedRoutes.ContainsKey(intlNbo);

                // 3. Ping international IP via default route. Treat success as a
                //    reachability observation, not proof of a split-tunnel failure.
                //    SKIP when a /32 host route for this IP is already installed (e.g.
                //    chrome started connecting before this check ran) — that route would
                //    win over the default route and give a false "Success, 0ms" result.
                if (!skipDefaultRoutePing)
                {
                    if (!routeAlreadyPresent)
                    {
                        try
                        {
                            var reply = ping.Send(intlIp, 3000);
                            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                                Logger.Info($"[CONN-CHECK] Ping {intlHost} ({intlIp}, default route): {reply.Status}, {reply.RoundtripTime}ms — direct route is reachable; not a leak by itself");
                            else
                                Logger.Info($"[CONN-CHECK] Ping {intlHost} ({intlIp}, default route): {reply.Status} — OK, international not reachable via default route (split-tunnel working)");
                        }
                        catch (Exception ex) { Logger.Warning($"[CONN-CHECK] Ping international (default route) failed: {ex.Message}"); }
                    }
                    else
                    {
                        Logger.Info($"[CONN-CHECK] Ping {intlHost} default-route check skipped — host route already present (app traffic in flight)");
                    }
                }

                // 4. End-to-end reachability through the tunnel.
                //    Raw TcpClient.Connect to an IP via the TUN NIC is NOT reliable for
                //    V2Ray/Xray (gvisor completes the local handshake in 1–3 ms before
                //    the remote path is ready). Use CONN-VERIFY (SOCKS + TLS RTT) instead.
                //    Private custom targets stay on the SOCKS probe so a /32 is not
                //    installed over a LAN address.
                if (_vpnServerSkipBareTcpProbe)
                {
                    Logger.Info($"[CONN-CHECK] TCP {intlHost} via VPN: skipped (TUN local handshake is misleading for WebSocket proxies — see CONN-VERIFY RTT)");
                }
                else if (skipDefaultRoutePing && HealthCheckTargets.IsPrivateOrLinkLocal(intlIp))
                {
                    Logger.Info($"[CONN-CHECK] TCP {intlHost} via VPN skipped — {intlIp} is private; the SOCKS health check covers it without installing a host route");
                }
                else
                {
                    try
                    {
                        if (_vpnServerIsUdpOnly &&
                            !await WaitForVpnIngressAsync(TimeSpan.FromSeconds(10)))
                        {
                            Logger.Info($"[CONN-CHECK] TCP {intlHost} via VPN delayed — WireGuard has not observed inbound tunnel traffic yet");
                            WireGuardHandshakeDiagnostics.LogStatus();
                            return;
                        }

                        bool routeAdded = false;
                        if (!routeAlreadyPresent)
                        {
                            EnsureHostRouteViaVpn(intlNbo, intlIp);
                            routeAdded = true;
                        }

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var tcp = new System.Net.Sockets.TcpClient();
                        using var tcpCts = new System.Threading.CancellationTokenSource(3000);
                        await tcp.ConnectAsync(intlIp, intlPort, tcpCts.Token);
                        sw.Stop();
                        Logger.Info($"[CONN-CHECK] TCP {intlHost} ({intlIp}:{intlPort}, via VPN tunnel): {sw.ElapsedMilliseconds}ms (raw TCP — may be lower than SOCKS/TLS probes)");

                        if (routeAdded && !_ipToProcess.ContainsKey(intlNbo))
                            ScheduleDelayedRouteRemoval(intlNbo);
                    }
                    catch (OperationCanceledException) { Logger.Warning($"[CONN-CHECK] TCP {intlHost} via VPN: timeout (3000ms)"); }
                    catch (Exception ex) { Logger.Warning($"[CONN-CHECK] TCP {intlHost} via VPN failed: {ex.Message}"); }
                }
            }
            else
                Logger.Warning($"[CONN-CHECK] Could not resolve {intlHost} — skipping international checks");
        }
        catch { }
    }

    /// <summary>
    /// Returns true once inbound packets on the VPN adapter have been observed.
    /// Used for WireGuard (UDP) before end-to-end probes — the TAP/TUN may be Up
    /// while the data plane is not ready yet.
    /// </summary>
    public async Task<bool> WaitForVpnIngressAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_isRunning && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref _statVpnIngressSniffed) > 0 ||
                Interlocked.Read(ref _statNetInRewritten) > 0)
                return true;

            await Task.Delay(300, ct);
        }

        return Interlocked.Read(ref _statVpnIngressSniffed) > 0 ||
               Interlocked.Read(ref _statNetInRewritten) > 0;
    }
}
