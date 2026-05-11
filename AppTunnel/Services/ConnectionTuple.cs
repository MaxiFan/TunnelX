using System.Net;

namespace AppTunnel.Services;

internal record struct ConnectionTuple(byte Protocol, IPAddress LocalIp, ushort LocalPort,
    IPAddress RemoteIp, ushort RemotePort);
