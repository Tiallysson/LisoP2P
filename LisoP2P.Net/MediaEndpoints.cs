using System.Net;
using LisoP2P.Core;

namespace LisoP2P.Net;

internal static class MediaEndpoints
{
    public static IPEndPoint? TryResolve(IDiscoveryService discovery, PeerId peer)
    {
        var found = discovery.Peers.FirstOrDefault(p => p.Id == peer);

        if (found is null)
        {
            return null;
        }

        var port = found.MediaPort is > 0 and <= 65535 ? found.MediaPort : NetworkOptions.DefaultMediaPort;
        return new IPEndPoint(found.Address, port);
    }

    public static List<IPEndPoint> ResolveAll(IDiscoveryService discovery, IEnumerable<PeerId> peers)
    {
        var endpoints = new List<IPEndPoint>();

        foreach (var peer in peers)
        {
            if (TryResolve(discovery, peer) is { } endpoint)
            {
                endpoints.Add(endpoint);
            }
        }

        return endpoints;
    }
}
