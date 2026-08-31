using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;

namespace LisoP2P.Net;

public sealed class SessionManager : ISessionManager
{
    private readonly NetworkOptions _options;
    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ConcurrentDictionary<Guid, (IPeerSession Session, bool IsOutbound)> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public IReadOnlyDictionary<PeerId, IPeerSession> Sessions =>
        _sessions.ToDictionary(kv => new PeerId(kv.Key), kv => kv.Value.Session);

    public event Action<IPeerSession>? SessionOpened;
    public event Action<PeerId>? SessionClosed;

    public SessionManager(NetworkOptions options, IIdentityStore identity, IDiscoveryService discovery)
    {
        _options = options;
        _identity = identity;
        _discovery = discovery;
    }

    public Task StartListeningAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _options.SessionPort);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<IPeerSession> ConnectAsync(DiscoveredPeer peer, CancellationToken ct)
    {
        PeerSession.Connector connector = async innerCt =>
        {
            var current = _discovery.Peers.FirstOrDefault(p => p.Id == peer.Id) ?? peer;
            var client = new TcpClient();
            await client.ConnectAsync(current.Address, current.SessionPort, innerCt).ConfigureAwait(false);
            return client.GetStream();
        };

        var session = PeerSession.CreateOutbound(peer.Id, connector, _identity, _discovery);
        await session.WaitForHandshakeAsync().ConfigureAwait(false);

        if (session.State == SessionState.Closed)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Handshake with peer {peer.Id.Value} failed.");
        }

        await RegisterSessionAsync(session, isOutbound: true).ConfigureAwait(false);
        return session;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleInboundAsync(client, ct);
        }
    }

    private async Task HandleInboundAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var session = PeerSession.CreateInbound(stream, _identity);
        await session.WaitForHandshakeAsync().ConfigureAwait(false);

        if (session.State == SessionState.Closed)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await RegisterSessionAsync(session, isOutbound: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a simultaneous-connect race deterministically: the side whose PeerId is
    /// smaller keeps the connection it initiated; the other side closes its. Both peers
    /// apply the same rule, so they converge without negotiation.
    /// </summary>
    private async Task RegisterSessionAsync(IPeerSession session, bool isOutbound)
    {
        var key = session.RemoteId.Value;
        var keepOutbound = _identity.Id.Value.CompareTo(key) < 0;

        var accepted = false;
        var replacedExisting = false;
        IPeerSession? rejected = null;

        _sessions.AddOrUpdate(
            key,
            _ =>
            {
                accepted = true;
                return (session, isOutbound);
            },
            (_, existing) =>
            {
                var existingMatchesRule = existing.IsOutbound == keepOutbound;
                var newMatchesRule = isOutbound == keepOutbound;

                if (newMatchesRule && !existingMatchesRule)
                {
                    accepted = true;
                    replacedExisting = true;
                    rejected = existing.Session;
                    return (session, isOutbound);
                }

                rejected = session;
                return existing;
            });

        if (rejected is not null)
        {
            await rejected.DisposeAsync().ConfigureAwait(false);
        }

        if (!accepted)
        {
            return;
        }

        if (replacedExisting)
        {
            SessionClosed?.Invoke(new PeerId(key));
        }

        session.StateChanged += state => OnSessionStateChanged(key, session, state);
        SessionOpened?.Invoke(session);
    }

    private void OnSessionStateChanged(Guid key, IPeerSession session, SessionState state)
    {
        if (state != SessionState.Closed)
        {
            return;
        }

        if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current.Session, session))
        {
            var removed = ((ICollection<KeyValuePair<Guid, (IPeerSession Session, bool IsOutbound)>>)_sessions)
                .Remove(new KeyValuePair<Guid, (IPeerSession, bool)>(key, current));

            if (removed)
            {
                SessionClosed?.Invoke(new PeerId(key));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var (session, _) in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _cts?.Dispose();
    }
}
