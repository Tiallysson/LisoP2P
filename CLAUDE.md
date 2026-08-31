# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

**Fase 0** (of 6 planned phases) is implemented: solution scaffold, peer identity, the wire
protocol, UDP broadcast discovery, and a manual-connect fallback, with a WPF UI showing the
discovered-peer list. No chat, audio, video, or screen capture yet — those are later phases.

`LisoP2P.App/Docs/fase0-prompt.md` is the original Portuguese specification for this phase. It
names project prefixes as `P2PChat.*` and targets `net8.0` — this repo instead kept the
`LisoP2P.*` prefix and targets `net10.0`/`net10.0-windows` (explicit choices made when starting
implementation, since the scaffold already used net10.0). Everything else in the spec was
followed as written; read it before touching Core/Net if you need the full behavioral contract.

## Architecture

Five projects, referenced as `Net → Core`, `Media → Core`, `App → Core, Net, Media`,
`Tests → Core, Net`:

- **`LisoP2P.Core`** (classlib, `net10.0`, no `-windows`) — `PeerId`, `IIdentityStore` /
  `FileIdentityStore`, `DiscoveredPeer`, and the `Protocol/` namespace: `Envelope`, `MessageType`,
  `AnnouncePayload`, and their MessagePack codecs (`ProtocolCodec`, `AnnouncePayloadCodec`).
- **`LisoP2P.Net`** — `NetworkOptions`, `IDiscoveryService` / `DiscoveryService` (UDP broadcast
  discovery), `BroadcastAddressCalculator`, `ManualPeerConnector` (unicast fallback).
- **`LisoP2P.Media`** — empty, reserved for a future phase.
- **`LisoP2P.App`** (wpf, `net10.0-windows`) — WPF/MVVM UI (`CommunityToolkit.Mvvm`), composed via
  `Microsoft.Extensions.DependencyInjection` in `App.xaml.cs` (no separate DI framework).
- **`LisoP2P.Tests`** (xunit) — codec round-trip/malformed-input coverage, identity persistence,
  broadcast address calculation.

**Hard constraint:** Core, Net, and Media target plain `net10.0` (no `-windows`) and must never
reference `System.Windows`. Anything UI-facing needed from Net is exposed via an event/callback,
never a direct dependency.

Design points worth knowing before touching this code:
- `PeerId` is a random `Guid`, persisted via `FileIdentityStore` under
  `%APPDATA%/LisoP2P/identity.json` by default. It is never derived from IP, since IP changes
  when Radmin VPN is involved but identity must not.
- `App.xaml.cs` nests the identity directory under the session port when it differs from
  `NetworkOptions.DefaultSessionPort` (`GetIdentityDirectory`). Without this, two same-machine
  dev instances (differentiated only by `--session-port`) would load the same identity file, get
  an identical `PeerId`, and `DiscoveryService` would filter each other out as self — this was a
  real bug caught by manually running two instances side by side.
- `ProtocolCodec.TryDecode` / `AnnouncePayloadCodec.TryDecode` must never throw — they return
  `false` on malformed/unknown input, since the socket accepts arbitrary bytes from the open
  network.
- `DiscoveryService` broadcasts to the directed-broadcast address of *every* active, non-loopback
  network interface (`BroadcastAddressCalculator`, `ip | ~mask`) plus `255.255.255.255` — required
  for the Radmin virtual adapter to receive announcements, since a plain global broadcast often
  doesn't traverse it.
- On receiving an Announce from a peer it doesn't yet know, `DiscoveryService` replies with a
  single unicast Announce back to the sender (only on first sight, not on every update) — this is
  what makes `ManualPeerConnector`'s one-shot unicast turn into mutual discovery without a
  reply-loop between peers.
- The peer dictionary is a `ConcurrentDictionary` (socket callbacks run on pool threads);
  `MainViewModel` marshals `IDiscoveryService` events to the UI thread via
  `Application.Current.Dispatcher.Invoke` — the events do not arrive on the UI thread on their
  own.
- `--discovery-port` / `--session-port` CLI args override `NetworkOptions` (defaults 47100 UDP /
  47101 TCP) — required for running two local instances side by side.

## Commands

```
dotnet build
dotnet test
dotnet run --project LisoP2P.App -- --discovery-port 47100 --session-port 47101
```

Manual acceptance test (see README.md): run two instances with distinct `--session-port` values
and confirm they discover each other within a few seconds and drop each other within 8 seconds of
one closing. This has been manually verified working end-to-end.
