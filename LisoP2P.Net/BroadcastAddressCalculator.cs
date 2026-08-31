using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LisoP2P.Net;

public static class BroadcastAddressCalculator
{
    public static IPAddress Calculate(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != maskBytes.Length)
        {
            throw new ArgumentException("Address and mask must be the same address family.", nameof(mask));
        }

        var broadcastBytes = new byte[addressBytes.Length];
        for (var i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | (byte)~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }

    public static IEnumerable<IPAddress> GetActiveBroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                yield return Calculate(unicast.Address, unicast.IPv4Mask);
            }
        }
    }
}
