using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Carries selected-app TCP/UDP to the VPN interface without a destination
/// /32. Unselected apps never see that route, so they keep the physical
/// default path (including DNS and browser search).
/// </summary>
internal sealed class SplitAppForwarder : IDisposable
{
    private readonly SplitForwarderSessions _sessions = new();
    private readonly ConcurrentDictionary<(byte Proto, uint ClientIp, ushort ClientPort), UdpFlow> _udpFlows = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private IPAddress? _vpnAddress;
    private int _vpnInterfaceIndex;
    private int _connectLogs;

    public SplitForwarderSessions Sessions => _sessions;
    public bool IsRunning { get; private set; }
    public ushort TcpPort { get; private set; }
    public ushort UdpPort { get; private set; }

    public void Start(IPAddress vpnAddress, int vpnInterfaceIndex)
    {
        if (IsRunning)
            return;
        if (vpnInterfaceIndex <= 0)
            throw new InvalidOperationException("VPN interface index is not ready.");

        _vpnAddress = vpnAddress;
        _vpnInterfaceIndex = vpnInterfaceIndex;
        _cts = new CancellationTokenSource();

        _tcp = new TcpListener(IPAddress.Loopback, 0);
        _tcp.Start();
        TcpPort = (ushort)((IPEndPoint)_tcp.LocalEndpoint).Port;

        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        UdpPort = (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        IsRunning = true;
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => UdpReceiveLoop(_cts.Token));
        _ = Task.Run(() => SweepUdpFlows(_cts.Token));
        Logger.Info($"[SPLIT-DIRECT] App forwarder listening on 127.0.0.1 tcp/{TcpPort} udp/{UdpPort} via VPN if {vpnInterfaceIndex}");
    }

    public void Register(
        byte protocol,
        uint clientIp,
        ushort clientPort,
        uint originalDestIp,
        ushort originalDestPort,
        uint forwardDestIp,
        ushort forwardDestPort)
    {
        _sessions.Register(
            protocol,
            clientIp,
            clientPort,
            originalDestIp,
            originalDestPort,
            forwardDestIp,
            forwardDestPort);
    }

    public void Dispose() => Stop();

    public void Stop()
    {
        if (!IsRunning && _tcp == null && _udp == null)
            return;

        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _tcp = null;
        _udp = null;

        foreach (var flow in _udpFlows.Values)
            flow.Dispose();
        _udpFlows.Clear();

        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _tcp;
        if (listener == null)
            return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Warning($"[SPLIT-DIRECT] TCP accept failed: {ex.Message}");
                break;
            }

            _ = Task.Run(() => HandleTcpAsync(client, ct));
        }
    }

    private async Task HandleTcpAsync(TcpClient client, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is not IPEndPoint remote)
                return;

            var clientIp = ToNbo(remote.Address);
            if (!_sessions.TryGet(6, clientIp, (ushort)remote.Port, out var session))
            {
                Logger.Warning($"[SPLIT-DIRECT] TCP from {remote} has no selected-app session");
                return;
            }

            using var outbound = ConnectVpn(session.ForwardDestIp, session.ForwardDestPort, out var error);
            if (outbound == null)
            {
                if (Interlocked.Increment(ref _connectLogs) <= 8)
                    Logger.Warning($"[SPLIT-DIRECT] TCP dial via VPN failed: {error}");
                return;
            }

            using var app = client.GetStream();
            using var vpn = new NetworkStream(outbound, ownsSocket: false);
            var inbound = PumpAsync(vpn, app, linked.Token);
            var outboundPump = PumpAsync(app, vpn, linked.Token);
            var finished = await Task.WhenAny(inbound, outboundPump);
            linked.Cancel();
            try { await Task.WhenAll(inbound, outboundPump); } catch { }
            _ = finished;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Interlocked.Increment(ref _connectLogs) <= 8)
                Logger.Warning($"[SPLIT-DIRECT] TCP relay ended: {ex.Message}");
        }
        finally
        {
            if (client.Client.RemoteEndPoint is IPEndPoint remote)
                _sessions.Remove(6, ToNbo(remote.Address), (ushort)remote.Port);
            try { client.Dispose(); } catch { }
        }
    }

    private async Task UdpReceiveLoop(CancellationToken ct)
    {
        var udp = _udp;
        if (udp == null)
            return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Warning($"[SPLIT-DIRECT] UDP receive failed: {ex.Message}");
                break;
            }

            var clientIp = ToNbo(result.RemoteEndPoint.Address);
            var clientPort = (ushort)result.RemoteEndPoint.Port;
            if (!_sessions.TryGet(17, clientIp, clientPort, out var session))
                continue;

            var flowKey = ((byte)17, clientIp, clientPort);
            if (!_udpFlows.TryGetValue(flowKey, out var flow))
            {
                flow = CreateUdpFlow(result.RemoteEndPoint, ct);
                if (flow == null)
                    continue;
                if (!_udpFlows.TryAdd(flowKey, flow))
                {
                    flow.Dispose();
                    if (!_udpFlows.TryGetValue(flowKey, out flow))
                        continue;
                }
            }

            flow.Touch();
            try
            {
                var dest = new IPEndPoint(new IPAddress(BitConverter.GetBytes(session.ForwardDestIp)), session.ForwardDestPort);
                flow.Socket.SendTo(result.Buffer, dest);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _connectLogs) <= 8)
                    Logger.Warning($"[SPLIT-DIRECT] UDP send via VPN failed: {ex.Message}");
            }
        }
    }

    private UdpFlow? CreateUdpFlow(IPEndPoint client, CancellationToken ct)
    {
        if (_vpnAddress == null)
            return null;

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Ipv4UnicastInterface.Bind(socket, _vpnAddress, _vpnInterfaceIndex);
            var flow = new UdpFlow(socket, client);
            _ = Task.Run(() => UdpReturnLoop(flow, ct));
            return flow;
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _connectLogs) <= 8)
                Logger.Warning($"[SPLIT-DIRECT] UDP VPN socket failed: {ex.Message}");
            return null;
        }
    }

    private async Task UdpReturnLoop(UdpFlow flow, CancellationToken ct)
    {
        var buffer = new byte[65535];
        while (!ct.IsCancellationRequested && IsRunning)
        {
            int read;
            try
            {
                read = flow.Socket.Receive(buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            if (read <= 0 || _udp == null)
                continue;

            flow.Touch();
            try
            {
                await _udp.SendAsync(buffer.AsMemory(0, read), flow.Client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _connectLogs) <= 8)
                    Logger.Warning($"[SPLIT-DIRECT] UDP return to app failed: {ex.Message}");
            }
        }
    }

    private async Task SweepUdpFlows(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException) { break; }

            var cutoff = Environment.TickCount64 - 45_000;
            foreach (var item in _udpFlows)
            {
                if (item.Value.LastSeenTick >= cutoff)
                    continue;
                if (_udpFlows.TryRemove(item.Key, out var removed))
                {
                    _sessions.Remove(item.Key.Proto, item.Key.ClientIp, item.Key.ClientPort);
                    removed.Dispose();
                }
            }
        }
    }

    private Socket? ConnectVpn(uint destIp, ushort destPort, out string error)
    {
        error = "";
        if (_vpnAddress == null)
        {
            error = "VPN address missing";
            return null;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            Ipv4UnicastInterface.Bind(socket, _vpnAddress, _vpnInterfaceIndex);
            var dest = new IPEndPoint(new IPAddress(BitConverter.GetBytes(destIp)), destPort);
            var result = socket.BeginConnect(dest, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
            {
                error = "timeout";
                socket.Dispose();
                return null;
            }

            socket.EndConnect(result);
            return socket;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            socket.Dispose();
            return null;
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            int read = await from.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static uint ToNbo(IPAddress address)
        => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? BitConverter.ToUInt32(address.GetAddressBytes(), 0)
            : 0;

    private sealed class UdpFlow : IDisposable
    {
        public UdpFlow(Socket socket, IPEndPoint client)
        {
            Socket = socket;
            Client = client;
            LastSeenTick = Environment.TickCount64;
        }

        public Socket Socket { get; }
        public IPEndPoint Client { get; }
        public long LastSeenTick;

        public void Touch() => LastSeenTick = Environment.TickCount64;

        public void Dispose()
        {
            try { Socket.Dispose(); } catch { }
        }
    }
}
