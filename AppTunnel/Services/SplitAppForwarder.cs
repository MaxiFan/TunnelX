using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO; // explicit: the WPF markup compile does not import System.IO
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Carries selected-app TCP/UDP without a destination /32. When a sing-box or
/// xray mixed proxy is up, the dial goes through that SOCKS5 inbound (the
/// same path CONN-VERIFY uses). Binding the dial to the TUN address does not
/// work for TCP: IP_UNICAST_IF loses to the physical default, and LEAK-GUARD
/// drops the SYN. Tunnels with no mixed proxy keep the VPN-bound dial.
/// </summary>
internal sealed class SplitAppForwarder : IDisposable
{
    private readonly SplitForwarderSessions _sessions = new();
    private readonly ConcurrentDictionary<(byte Proto, uint ClientIp, ushort ClientPort), UdpFlow> _udpFlows = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private IPAddress? _vpnAddress;
    private IPEndPoint? _upstream;
    private int _vpnInterfaceIndex;
    private int _detailLogs;
    private int _failLogs;
    private int _associateLogs;
    private long _accepts;
    private long _dialOk;
    private long _dialFail;
    private long _bytesToProxy;
    private long _bytesToApp;
    private Action<string, long, long>? _onAppBytes;

    public Action<string, long, long>? AppBytesObserved
    {
        get => _onAppBytes;
        set => _onAppBytes = value;
    }

    public SplitForwarderSessions Sessions => _sessions;
    public bool IsRunning { get; private set; }
    public ushort TcpPort { get; private set; }
    public ushort UdpPort { get; private set; }
    public bool UsesMixedProxy => _upstream != null;

    public string Diagnostics
    {
        get
        {
            var mode = _upstream == null
                ? $"vpn-bind/{_vpnInterfaceIndex}"
                : $"socks5/{_upstream.Port}";
            return $"{mode} tcp/{TcpPort} udp/{UdpPort} " +
                   $"acc={Interlocked.Read(ref _accepts)} ok={Interlocked.Read(ref _dialOk)} fail={Interlocked.Read(ref _dialFail)} " +
                   $"appToProxy={Interlocked.Read(ref _bytesToProxy)} proxyToApp={Interlocked.Read(ref _bytesToApp)}";
        }
    }

    public void Start(IPAddress vpnAddress, int vpnInterfaceIndex, IPEndPoint? upstreamProxy = null)
    {
        if (IsRunning)
            return;
        if (vpnInterfaceIndex <= 0)
            throw new InvalidOperationException("VPN interface index is not ready.");

        _vpnAddress = vpnAddress;
        _vpnInterfaceIndex = vpnInterfaceIndex;
        _upstream = upstreamProxy;
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
        if (_upstream != null)
        {
            Logger.Info($"[SPLIT-FWD] App forwarder listening on 127.0.0.1 tcp/{TcpPort} udp/{UdpPort} upstream=socks5://127.0.0.1:{_upstream.Port} (selected apps do not bind to the VPN address)");
        }
        else
        {
            Logger.Info($"[SPLIT-FWD] App forwarder listening on 127.0.0.1 tcp/{TcpPort} udp/{UdpPort} upstream=vpn-bind if {vpnInterfaceIndex} (no mixed proxy; relay route required)");
        }
    }

    public void Register(
        byte protocol,
        uint clientIp,
        ushort clientPort,
        uint originalDestIp,
        ushort originalDestPort,
        uint forwardDestIp,
        ushort forwardDestPort,
        uint originalIfIdx = 0,
        string? processName = null)
    {
        _sessions.Register(
            protocol,
            clientIp,
            clientPort,
            originalDestIp,
            originalDestPort,
            forwardDestIp,
            forwardDestPort,
            originalIfIdx,
            processName);
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
                    Logger.Warning($"[SPLIT-FWD] TCP accept failed: {ex.Message}");
                break;
            }

            Interlocked.Increment(ref _accepts);
            _ = Task.Run(() => HandleTcpAsync(client, ct));
        }
    }

    private async Task HandleTcpAsync(TcpClient client, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var detail = ShouldDetail();
        long appToProxy = 0;
        long proxyToApp = 0;
        string dialTarget = "";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is not IPEndPoint remote)
            {
                Logger.Warning("[SPLIT-FWD] TCP accept has no remote endpoint");
                return;
            }

            var clientIp = ToNbo(remote.Address);
            if (!_sessions.TryGet(6, clientIp, (ushort)remote.Port, out var session))
            {
                Logger.Warning($"[SPLIT-FWD] TCP from {remote} has no selected-app session");
                return;
            }

            dialTarget = $"{FormatNbo(session.ForwardDestIp)}:{session.ForwardDestPort}";
            if (detail)
                Logger.Info($"[SPLIT-FWD] TCP accept {remote} dial {dialTarget} via {UpstreamLabel()} +{started.ElapsedMilliseconds}ms");

            Socket outbound;
            try
            {
                outbound = await ConnectUpstreamAsync(session.ForwardDestIp, session.ForwardDestPort, linked.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dialFail);
                if (ShouldLogFailure())
                    Logger.Warning($"[SPLIT-FWD] TCP dial {dialTarget} via {UpstreamLabel()} failed after {started.ElapsedMilliseconds}ms: {ex.Message}");
                return;
            }

            Interlocked.Increment(ref _dialOk);
            if (detail)
                Logger.Info($"[SPLIT-FWD] TCP dial {dialTarget} ok {started.ElapsedMilliseconds}ms via {UpstreamLabel()}");

            using (outbound)
            {
                using var app = client.GetStream();
                using var vpn = new NetworkStream(outbound, ownsSocket: false);
                var toProxy = PumpAsync(app, vpn, "app->proxy", dialTarget, detail, started, linked.Token,
                    n => NoteApp(session.ProcessName, n, 0));
                var toApp = PumpAsync(vpn, app, "proxy->app", dialTarget, detail, started, linked.Token,
                    n => NoteApp(session.ProcessName, 0, n));
                try
                {
                    await Task.WhenAny(toProxy, toApp);
                    linked.Cancel();
                    try { await Task.WhenAll(toProxy, toApp); } catch { }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (detail)
                        Logger.Warning($"[SPLIT-FWD] TCP relay {dialTarget} ended: {ex.Message}");
                }

                appToProxy = await ReadPumpCount(toProxy);
                proxyToApp = await ReadPumpCount(toApp);
            }

            if (detail)
                Logger.Info($"[SPLIT-FWD] TCP close {dialTarget} appToProxy={appToProxy} proxyToApp={proxyToApp} {started.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ShouldLogFailure())
                Logger.Warning($"[SPLIT-FWD] TCP relay {dialTarget} ended: {ex.Message}");
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
                    Logger.Warning($"[SPLIT-FWD] UDP receive failed: {ex.Message}");
                break;
            }

            var clientIp = ToNbo(result.RemoteEndPoint.Address);
            var clientPort = (ushort)result.RemoteEndPoint.Port;
            if (!_sessions.TryGet(17, clientIp, clientPort, out var session))
                continue;

            var flowKey = ((byte)17, clientIp, clientPort);
            if (!_udpFlows.TryGetValue(flowKey, out var flow))
            {
                var created = await CreateUdpFlowAsync(result.RemoteEndPoint, session.ProcessName, ct);
                if (created == null)
                    continue;
                if (!_udpFlows.TryAdd(flowKey, created))
                {
                    created.Dispose();
                    if (!_udpFlows.TryGetValue(flowKey, out created))
                        continue;
                }

                flow = created;
            }

            flow.Touch();
            try
            {
                flow.Send(session.ForwardDestIp, session.ForwardDestPort, result.Buffer, result.Buffer.Length);
                NoteApp(session.ProcessName, result.Buffer.Length, 0);
                if (flow.NoteSend())
                    Logger.Info($"[SPLIT-FWD] UDP {result.RemoteEndPoint} -> {FormatNbo(session.ForwardDestIp)}:{session.ForwardDestPort} via {UpstreamLabel()} ({result.Buffer.Length} bytes)");
            }
            catch (Exception ex)
            {
                if (ShouldLogFailure())
                    Logger.Warning($"[SPLIT-FWD] UDP send via {UpstreamLabel()} failed: {ex.Message}");
            }
        }
    }

    private async Task<UdpFlow?> CreateUdpFlowAsync(IPEndPoint client, string? processName, CancellationToken ct)
    {
        if (_upstream != null)
        {
            try
            {
                var started = Stopwatch.StartNew();
                var channel = await Socks5TunnelDial.AssociateUdpAsync(_upstream, ct);
                var flow = new UdpFlow(client, channel, processName);
                if (Interlocked.Increment(ref _associateLogs) <= 8)
                    Logger.Info($"[SPLIT-FWD] UDP associate ok {started.ElapsedMilliseconds}ms relay={channel.Relay} via socks5://127.0.0.1:{_upstream.Port}");
                _ = Task.Run(() => UdpReturnLoop(flow, ct));
                return flow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dialFail);
                if (ShouldLogFailure())
                    Logger.Warning($"[SPLIT-FWD] UDP associate via socks5://127.0.0.1:{_upstream.Port} failed: {ex.Message}");
                return null;
            }
        }

        if (_vpnAddress == null)
            return null;

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Ipv4UnicastInterface.Bind(socket, _vpnAddress, _vpnInterfaceIndex);
            var flow = new UdpFlow(client, socket, processName);
            _ = Task.Run(() => UdpReturnLoop(flow, ct));
            return flow;
        }
        catch (Exception ex)
        {
            if (ShouldLogFailure())
                Logger.Warning($"[SPLIT-FWD] UDP VPN socket failed: {ex.Message}");
            return null;
        }
    }

    private async Task UdpReturnLoop(UdpFlow flow, CancellationToken ct)
    {
        var buffer = new byte[65535];
        var started = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && IsRunning)
        {
            int read;
            try
            {
                read = flow.Receive(buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            if (read <= 0 || _udp == null)
                continue;

            flow.Touch();
            NoteApp(flow.ProcessName, 0, read);
            if (flow.NoteReceive())
                Logger.Info($"[SPLIT-FWD] UDP first reply -> {flow.Client} +{started.ElapsedMilliseconds}ms len={read}");
            try
            {
                await _udp.SendAsync(buffer.AsMemory(0, read), flow.Client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (ShouldLogFailure())
                    Logger.Warning($"[SPLIT-FWD] UDP return to app failed: {ex.Message}");
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

    private async Task<Socket> ConnectUpstreamAsync(uint destIp, ushort destPort, CancellationToken ct)
    {
        var dest = new IPAddress(BitConverter.GetBytes(destIp));
        if (_upstream != null)
            return await Socks5TunnelDial.ConnectTcpAsync(_upstream, dest, destPort, ct);

        var socket = ConnectVpn(destIp, destPort, out var error);
        if (socket == null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "VPN dial failed" : error);
        return socket;
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
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(12)))
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

    private async Task<long> PumpAsync(
        Stream from,
        Stream to,
        string direction,
        string dialTarget,
        bool detail,
        Stopwatch started,
        CancellationToken ct,
        Action<long>? onBytes = null)
    {
        var buffer = new byte[16384];
        long total = 0;
        var loggedFirst = false;
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await from.ReadAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
                break;

            if (!loggedFirst)
            {
                loggedFirst = true;
                if (detail)
                    Logger.Info($"[SPLIT-FWD] TCP first-byte {direction} {dialTarget} +{started.ElapsedMilliseconds}ms len={read}");
            }

            total += read;
            onBytes?.Invoke(read);
            try
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
        }

        return total;
    }

    private static async Task<long> ReadPumpCount(Task<long> pump)
    {
        try
        {
            return await pump;
        }
        catch
        {
            return 0;
        }
    }

    private void NoteApp(string? processName, long sent, long received)
    {
        if (sent > 0)
            Interlocked.Add(ref _bytesToProxy, sent);
        if (received > 0)
            Interlocked.Add(ref _bytesToApp, received);
        if (!string.IsNullOrWhiteSpace(processName))
            _onAppBytes?.Invoke(processName, sent, received);
    }

    private string UpstreamLabel()
        => _upstream == null ? $"vpn-bind if {_vpnInterfaceIndex}" : $"socks5://127.0.0.1:{_upstream.Port}";

    private bool ShouldDetail() => Interlocked.Increment(ref _detailLogs) <= 32;

    private bool ShouldLogFailure() => Interlocked.Increment(ref _failLogs) <= 32;

    private static string FormatNbo(uint nbo) => new IPAddress(BitConverter.GetBytes(nbo)).ToString();

    private static uint ToNbo(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork
            ? BitConverter.ToUInt32(address.GetAddressBytes(), 0)
            : 0;

    private sealed class UdpFlow : IDisposable
    {
        private readonly Socket? _vpnSocket;
        private readonly Socks5UdpChannel? _socks;
        private int _sends;
        private int _receives;

        public UdpFlow(IPEndPoint client, Socket vpnSocket, string? processName)
        {
            Client = client;
            ProcessName = processName;
            _vpnSocket = vpnSocket;
            LastSeenTick = Environment.TickCount64;
        }

        public UdpFlow(IPEndPoint client, Socks5UdpChannel socks, string? processName)
        {
            Client = client;
            ProcessName = processName;
            _socks = socks;
            LastSeenTick = Environment.TickCount64;
        }

        public IPEndPoint Client { get; }
        public string? ProcessName { get; }
        public long LastSeenTick;

        public void Touch() => LastSeenTick = Environment.TickCount64;

        public bool NoteSend() => Interlocked.Increment(ref _sends) <= 4;

        public bool NoteReceive() => Interlocked.Increment(ref _receives) == 1;

        public void Send(uint destIp, ushort destPort, byte[] data, int length)
        {
            var dest = new IPAddress(BitConverter.GetBytes(destIp));
            if (_socks != null)
            {
                _socks.SendPayload(dest, destPort, data, length);
                return;
            }

            _vpnSocket!.SendTo(data.AsSpan(0, length), SocketFlags.None, new IPEndPoint(dest, destPort));
        }

        public int Receive(byte[] buffer)
            => _socks != null ? _socks.ReceivePayload(buffer) : _vpnSocket!.Receive(buffer);

        public void Dispose()
        {
            try { _vpnSocket?.Dispose(); } catch { }
            try { _socks?.Dispose(); } catch { }
        }
    }
}
