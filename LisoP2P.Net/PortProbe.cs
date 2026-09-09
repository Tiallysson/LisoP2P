using System.Net;
using System.Net.Sockets;
using LisoP2P.Core.Settings;

namespace LisoP2P.Net;

public readonly record struct PortCheck(int Port, string Role, bool IsAvailable, string? Detail);

/// <summary>
/// Binds and immediately releases, purely to answer "would this port work?" before the settings
/// screen commits to it. It is a snapshot, not a reservation: another process can take the port
/// between the check and the real bind, which is why the startup path still handles the failure.
/// </summary>
public static class PortProbe
{
    public static PortCheck CheckTcp(int port, string role) => Check(port, role, tcp: true);

    public static PortCheck CheckUdp(int port, string role) => Check(port, role, tcp: false);

    public static IReadOnlyList<PortCheck> CheckAll(int discoveryPort, int sessionPort, int mediaPort) =>
    [
        CheckUdp(discoveryPort, "descoberta"),
        CheckTcp(sessionPort, "sessão"),
        CheckUdp(mediaPort, "mídia"),
    ];

    private static PortCheck Check(int port, string role, bool tcp)
    {
        if (!NetworkPorts.IsValid(port))
        {
            return new PortCheck(port, role, false, $"fora do intervalo {NetworkPorts.MinPort}-{NetworkPorts.MaxPort}");
        }

        try
        {
            if (tcp)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
            }
            else
            {
                // Discovery and media both set ReuseAddress, so the probe has to as well or it
                // would report a port the app itself can use as taken.
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }

            return new PortCheck(port, role, true, null);
        }
        catch (SocketException ex)
        {
            return new PortCheck(port, role, false, ex.SocketErrorCode.ToString());
        }
    }
}
