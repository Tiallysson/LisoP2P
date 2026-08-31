# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

This repository is currently a bare WPF project scaffold generated from the Visual Studio
default template (`App.xaml`, `MainWindow.xaml` are unmodified boilerplate). No application
code, no git repository, and no README exist yet.

`LisoP2P/Docs/fase0-prompt.md` is a **specification document** (in Portuguese) describing the
first of six planned phases for a P2P LAN chat application. It has NOT been implemented — treat
it as the design brief for upcoming work, not as documentation of existing code. Read it in full
before starting any implementation work in this repo, since it defines the target architecture,
naming, and behavioral contracts in detail.

## Target architecture (per fase0-prompt.md)

The spec calls for restructuring the single `LisoP2P` WPF project into a four-project solution
(project names in the spec use a `P2PChat.*` prefix — confirm with the user whether to rename or
keep the `LisoP2P` name when scaffolding):

- **`*.Core`** (classlib, `net8.0`, no `-windows`) — peer identity (`PeerId`, `IIdentityStore`),
  the wire protocol (`Envelope`, `MessageType`, `ProtocolCodec`, MessagePack-based), and
  `DiscoveredPeer`.
- **`*.Net`** (classlib, `net8.0`, no `-windows`) — `NetworkOptions`, `IDiscoveryService` (UDP
  broadcast-based peer discovery), `ManualPeerConnector` (direct-unicast fallback for when
  broadcast doesn't traverse virtual adapters like Radmin VPN).
- **`*.Media`** (classlib, `net8.0`, no `-windows`) — reserved for a future phase, empty for now.
- **`*.App`** (wpf, `net8.0-windows`) — the WPF/MVVM UI, using `CommunityToolkit.Mvvm` and
  `Microsoft.Extensions.DependencyInjection`.

**Hard constraint from the spec:** Core, Net, and Media must target plain `net8.0` (no
`-windows` suffix) and must never reference `System.Windows`. Any UI-facing need from within Net
must be exposed via an event/callback, not a direct dependency — this boundary is explicitly
called out as non-negotiable in the spec.

Key design points worth knowing before touching this code:
- `PeerId` is a random `Guid`, persisted in `%APPDATA%/P2PChat/identity.json`. It is deliberately
  never derived from IP, since IP changes when Radmin VPN is involved but identity must not.
- `ProtocolCodec.TryDecode` must never throw — it returns `false` on malformed/unknown input,
  since the socket accepts arbitrary bytes from the open network.
- Discovery must broadcast to the directed-broadcast address of *every* active, non-loopback
  network interface (computed as `ip | ~mask`), not just `255.255.255.255` — this is required
  for the Radmin virtual adapter to receive announcements.
- Peer collections are mutated from socket-pool threads and must be thread-safe
  (`ConcurrentDictionary`); marshaling discovery events to the UI thread is the ViewModel's job
  (`Dispatcher.Invoke`), not the service's.
- Discovery/session ports must be overridable via `--discovery-port` / `--session-port` CLI args
  (defaults 47100 UDP / 47101 TCP), because running two local instances side-by-side for manual
  testing is a hard requirement.

## Commands

No test project, README, or build scripts exist yet. Once the solution is scaffolded per the
spec, the standard dotnet CLI applies:

```
dotnet build
dotnet run --project <App-project> -- --discovery-port 47100 --session-port 47101
dotnet test
```

The spec's acceptance test is running two instances with distinct `--session-port` values and
confirming they discover each other within 4 seconds and drop each other within 8 seconds of one
closing.
