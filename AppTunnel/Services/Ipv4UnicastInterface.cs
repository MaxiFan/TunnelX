using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

/// <summary>
/// Binds an IPv4 socket to one interface. IP_UNICAST_IF selects the egress
/// adapter without a destination /32, which would also capture every other
/// process.
/// </summary>
internal static class Ipv4UnicastInterface
{
    public const int Option = 31;

    public static void Bind(Socket socket, IPAddress localAddress, int interfaceIndex)
    {
        socket.Bind(new IPEndPoint(localAddress, 0));
        Apply(socket, interfaceIndex);
    }

    public static void Apply(Socket socket, int interfaceIndex)
    {
        if (interfaceIndex <= 0)
            return;

        var networkOrder = IPAddress.HostToNetworkOrder(interfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)Option, networkOrder);
    }
}
