# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

**All six phases are implemented**: fase 0 (scaffold, peer identity, wire protocol,
UDP broadcast discovery, manual-connect fallback), fase 1 (TCP session with handshake/keepalive/
reconnect, 1:1 chat with SQLite history), fase 2 (DXGI screen capture, H.264 encode, capture
test window), fase 3 (fragmented UDP media transport, H.264 decode, 1:1 screen sharing wired
into the conversation window), fase 4 (WASAPI capture, Opus, jitter buffer, push-to-talk
voice on the same media socket), fase 5 (mesh room: gossiped membership, room chat, screen
share and voice fanned out to every member, per-peer jitter buffers with mixing, bitrate ladder)
and fase 6 (Ed25519 identity with a visible fingerprint, persisted settings screen, error
presentation, single-file publish).

`LisoP2P.App/Docs/fase0-prompt.md` is the original Portuguese specification for the first phase
(`fase1-prompt.md` through `fase6-prompt.md` cover the later ones). It
names project prefixes as `P2PChat.*` and targets `net8.0` — this repo instead kept the
`LisoP2P.*` prefix and targets `net10.0`/`net10.0-windows` (explicit choices made when starting
implementation, since the scaffold already used net10.0). Everything else in the spec was
followed as written; read it before touching Core/Net if you need the full behavioral contract.

## Architecture

Six projects, referenced as `Media → Core`, `Net → Core, Media`, `Storage → Core`,
`App → Core, Net, Media, Storage`, `Tests → Core, Net, Media, Storage`:

- **`LisoP2P.Core`** (classlib, `net10.0`, no `-windows`) — `PeerId` (an Ed25519 public key),
  `PeerIdentity`, `PeerFingerprint`, `IIdentityStore` / `FileIdentityStore`, `AudioCaptureMode`,
  `DiscoveredPeer`, the `Settings/` namespace (`AppSettings` / `AppSettingsCodec`,
  `IAppSettingsStore` / `FileAppSettingsStore`, `NetworkPorts`, `PortResolver`, `AppPaths`),
  the `Diagnostics/` namespace (`IAppLogger` / `FileAppLogger`), and the `Protocol/` namespace: `Envelope`, `MessageType`,
  `AnnouncePayload`, and their MessagePack codecs (`ProtocolCodec`, `AnnouncePayloadCodec`),
  plus the media wire format: `MediaPacketHeader` / `MediaPacketCodec` (fixed 45-byte binary
  header carrying an explicit `SenderId`, no MessagePack), `ScreenSharePayload` /
  `ScreenSharePayloadCodec`, `VoicePayload` / `VoicePayloadCodec`, plus the room protocol:
  `RoomId`, `RoomMemberListPayload` / `RoomMemberInfo` / `RoomMemberListPayloadCodec` and
  `RoomSpeakingPayload` / `RoomSpeakingPayloadCodec`. Depends on `MessagePack`,
  `BouncyCastle.Cryptography` (Ed25519) and `System.Security.Cryptography.ProtectedData` (DPAPI).
- **`LisoP2P.Net`** — `NetworkOptions`, `IDiscoveryService` / `DiscoveryService` (UDP broadcast
  discovery), `BroadcastAddressCalculator`, `ManualPeerConnector` (unicast fallback),
  `PeerSession` / `SessionManager`, and the media path: `IMediaSender` / `UdpMediaSender`,
  `IMediaReceiver` / `UdpMediaReceiver`, `FrameReassembler`, `IJitterBuffer` / `JitterBuffer`,
  `IScreenShareSession` / `ScreenShareSession`, `IVoiceSession` / `VoiceSession`, and the room
  layer: `RoomMember`, `IRoomService` / `RoomService` (gossip + speaking), `IRoomChatRouter` /
  `RoomChatRouter`, `IAudioMixer` / `AudioMixer`, `BitrateLadder`, `MediaEndpoints`, plus
  `PortProbe` (bind-and-release check used by the settings screen and by startup).
- **`LisoP2P.Media`** — `IScreenCapture` / `DxgiScreenCapture` (Desktop Duplication),
  `TextureConverter` (BGRA→NV12, downscale to the target resolution and preview scaling on the
  D3D11 VideoProcessor), `IVideoEncoder` / `MediaFoundationH264Encoder` / `VideoEncoderFactory`
  (hardware MFT first, software MFT fallback), `ICapturePipeline` / `CapturePipeline`,
  `LatestFrameSlot`, `IEncodedFrameWriter` with `Mp4FileWriter` (default) and `AnnexBFileWriter`,
  `AnnexB`, plus the receive side: `IVideoDecoder` / `MediaFoundationH264Decoder` /
  `VideoDecoderFactory` and `Nv12Converter` (NV12→BGRA on the CPU), plus audio:
  `IAudioCapture` / `WasapiAudioCapture`, `IAudioPlayback` / `WasapiAudioPlayback`,
  `IAudioDeviceCatalog` / `WasapiAudioDeviceCatalog`, `IAudioEncoder` / `OpusAudioEncoder`,
  `IAudioDecoder` / `OpusAudioDecoder`, `AudioResampler`, `AudioFrameAccumulator`. Depends on
  `Vortice.Direct3D11`, `Vortice.MediaFoundation`, `NAudio.Wasapi` and `Concentus`.
- **`LisoP2P.Storage`** — SQLite chat history (`IChatStore` / `SqliteChatStore`, `StoredMessage`),
  migrated incrementally through `PRAGMA user_version`.
- **`LisoP2P.App`** (wpf, `net10.0-windows`) — WPF/MVVM UI (`CommunityToolkit.Mvvm`), composed via
  `Microsoft.Extensions.DependencyInjection` in `App.xaml.cs` (no separate DI framework).
  `MainWindow` is the Discord-style four-column shell (`Views/RoomRailView`,
  `RoomSidebarView`, `ChatView`, `MemberPanelView` over `Themes/DiscordDark.xaml`); room and 1:1
  share it, driven by `MainViewModel.ActiveConversation` (`RoomViewModel` / `ChatViewModel`, both
  `IConversationViewModel`). Fase 6 added `Services/` (`IAppShell`, `AppHost`, `IErrorPresenter` /
  `NotificationCenter`, `AppNotification`), `SettingsWindow` / `SettingsViewModel`,
  `WelcomeWindow` / `WelcomeViewModel` and `StartupErrorWindow`.
- **`LisoP2P.Tests`** (xunit) — codec round-trip/malformed-input coverage, identity persistence,
  broadcast address calculation, fragment reassembly, screen-share orchestration, a real
  encode/decode round trip through Media Foundation, and the fase 5 logic: member gossip
  convergence, speaking timeout against an injected clock, bitrate ladder, audio mixing, room chat
  routing and per-`SenderId` media demultiplexing, plus the fase 6 logic: `PeerId` equality and
  hash code, fingerprint formatting, identity persistence and legacy migration, `AppSettings`
  round-trip and normalization, and the flag > file > default port precedence.

The front-end refactor (`LisoP2P.App/Docs/frontend-wpf-discord-prompt.md`) is implemented: the
separate `RoomWindow` is gone, every colour and base style lives in `Themes/DiscordDark.xaml` (no
literal colour in any view), icons are `Segoe Fluent Icons` glyphs exposed as named theme resources
(no icon package — `MaterialDesignThemes` was dropped with it), and `ChatView` is one control for
both conversation kinds. Only the visual layer changed; Core/Net/Media/Storage were not touched.

The sidebar column opens at 220px and is resizable between 184 and 380 through a `GridSplitter`
sitting on the chat column's left edge; every row in it trims by column width rather than a fixed
`MaxWidth`, so it survives the drag. The width is session-only — persisting it would mean a new
field in `AppSettings`, which is Core. The fase 2 capture test moved off the rail into
Configurações → Vídeo → Diagnóstico (`SettingsViewModel.OpenCaptureTestCommand`); its window is
owned by `MainWindow`, not by the modal settings dialog, so closing Configurações does not close
it. `DiscordDark.xaml` also carries implicit styles for the stock WPF controls (`Button`, `TextBox`,
`CheckBox`, `ComboBox`, `ComboBoxItem`, `TabControl`, `TabItem`, `Separator`), because the settings
and capture-test screens use them directly and their default light chrome is unreadable on the dark
ground. **Never add an implicit `TextBlock` style here**: it also hits the `TextBlock` that every
`ContentPresenter` builds inside a control template, which is what once painted near-white text onto
a light button — the default text colour comes from `TextElement.Foreground` on each view root
instead. The themed `ComboBox` draws its closed box from `SelectionBoxItem`, which does not honour
`DisplayMemberPath`, so every `ComboBox` in the app sets an explicit `ItemTemplate`; with
`DisplayMemberPath` the field falls back to the item's `ToString()`. Each settings tab is wrapped in
a `ScrollViewer` — the tab content already overflowed the
620px window.

**Hard constraint:** Core, Net, and Media target plain `net10.0` (no `-windows`) and must never
reference `System.Windows`. Anything UI-facing needed from Net is exposed via an event/callback,
never a direct dependency.

Design points worth knowing before touching this code:
- `PeerId` **is** the peer's 32-byte Ed25519 public key, persisted via `FileIdentityStore` under
  `%APPDATA%/LisoP2P/identity.json`. It is never derived from IP, since IP changes when Radmin VPN
  is involved but identity must not. Carrying the whole key rather than a hash of it is what lets
  any peer compute the fingerprint of anyone it hears about without a second round trip.
- `PeerId` overrides `Equals`/`GetHashCode` **by hand**. A positional record over a `byte[]`
  compares by reference, and `PeerId` has been a dictionary key in every layer since fase 1 — that
  would have broken lookups silently, not loudly. The constructor also copies the array so a buffer
  decoded off the wire can never mutate an id already in use as a key.
- The private key is generated locally and stored DPAPI-protected (`CurrentUser`); it never leaves
  `FileIdentityStore`. Nothing signs anything yet — the pair exists so the id is derived and
  verifiable, and fase 6 explicitly leaves per-message signatures to a future phase.
- `PeerFingerprint` is SHA-256 of the public key in 16 groups of 4 hex characters. It is shown in
  Configurações → Identidade and once per peer as a non-blocking toast when a session opens.
  Nothing is gated on it: this is verification by transparency, not an approval flow.
- A pre-fase-6 `identity.json` (Guid + nickname) cannot be converted, so it is replaced on the next
  boot. The nickname survives (it was never the identity), the id does not, and
  `FileIdentityStore.MigratedFromLegacyIdentity` is what the UI uses to say so once.
- The nickname is user-editable (`IIdentityStore.SetNickname`, "Seu nome" in the main window) and
  lives in the same `identity.json`, but it is **display only**: `PeerId` never changes with it,
  and discovery, sessions and chat history keep keying off the id. `FileIdentityStore` writes via
  temp file + `File.Move(overwrite)` and raises `NicknameChanged` only on a real change.
- A rename propagates two ways: `DiscoveryService` announces immediately instead of waiting for
  the timer tick, and `SessionManager` sends `MessageType.NicknameUpdate` (14, `HelloPayload`
  body) over every open session, which `PeerSession` turns into `RemoteNicknameChanged`.
- Every nickname coming off the wire goes through `NicknameRules.Sanitize` (24 chars, no control
  chars, no zero-width/BOM, whitespace collapsed) with `NicknameRules.FallbackFor(id)` when it
  sanitizes to empty — announces and Hello payloads are arbitrary bytes from an open socket.
- `AppPaths.ResolveDataDirectory` nests the data directory under the session port. Without this,
  two same-machine dev instances (differentiated only by `--session-port`) would load the same
  identity file, get an identical `PeerId`, and `DiscoveryService` would filter each other out as
  self — this was a real bug caught by manually running two instances side by side. It keys off the
  **flag**, never off the effective port: since fase 6 the port can also come from `settings.json`,
  and following the effective port would move `identity.json` and silently hand the user a new
  identity when they changed a port in the settings screen.
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
- `PortResolver` resolves ports as **command line > `settings.json` > compiled default** (47100
  UDP / 47101 TCP / 47102 UDP). The order is not a preference: the fase 0/1 acceptance test tells
  two same-machine instances apart by `--session-port` alone, so a settings file must never be able
  to override it. The `session-port + 1` media-port derivation applies **only** to the command
  line — a session port that came from the file keeps the media port that came from the file.
- Video never travels over the TCP session: TCP retransmits and head-of-line blocks, and a frame
  three frames late is useless live. Only control does (`ScreenShareStart` 40, `ScreenShareStop`
  41, `KeyframeRequest` 42 — the keyframe request goes over TCP precisely because it must
  arrive).
- `FrameReassembler` is the memory-safety core of fase 3: at most 8 frames in flight, 200 ms
  timeout per incomplete frame, fragments of an already decided `FrameId` dropped on arrival.
  Without that window, out-of-order or hostile packets grow the dictionary without bound. Its
  clock is injected so the timeout is testable without `Task.Delay`.
- `ScreenShareSession` decodes on its own thread behind a bounded drop-oldest channel — decoding
  on the receive loop costs packets. It refuses to decode P-frames before the first keyframe and
  debounces `KeyframeRequest` to 500 ms so a burst of loss is not a burst of IDR frames. A
  session opening while a share is running gets `ScreenShareStart` resent plus a forced keyframe,
  which is what makes joining an ongoing share show a correct picture.
- `VideoDecoderFactory` tries **software first**, the reverse of the encoder, on purpose: fase 2
  already depends on hardware encode and the fase 3 plan warns against debugging both at once.
  Async decode MFTs throw in `Configure` so the factory falls through.
- H.264 codes in 16-pixel macroblocks, so 1080 lines decode into a 1088-line surface. The coded
  height drives the chroma plane offset in `Nv12Converter`; the visible size stays the announced
  one and the padding rows are cropped. Using the visible height as the plane offset shifts the
  colors and the picture comes out green.
- The decoder rotates three BGRA output buffers: the UI thread copies one into the
  `WriteableBitmap` while the decode thread already writes the next.
- Audio shares the video UDP socket, split by `MediaPacketCodec` `StreamId` (0 video, 1 audio).
  Audio skips fragment reassembly entirely — an Opus voice frame fits in one datagram — and an
  audio packet claiming several fragments is dropped.
- Audio is always 48 kHz mono in 960-sample (20 ms) frames before the encoder, whatever the
  device delivers; `AudioFrameAccumulator` re-frames the driver's arbitrary block sizes.
- `JitterBuffer` rules, in order of importance: `Pull` is called every 20 ms unconditionally, a
  missing frame becomes Opus PLC instead of a wait, a packet later than its played slot is
  discarded, depth is capped at 7 frames (140 ms) with the oldest dropped so a recovered network
  does not leave a permanent delay, and 25 concealed frames with nothing arriving means going
  silent and re-buffering.
- Push-to-talk starts and stops the capture device, not just the sending — releasing the key must
  stop capture and encode, and the acceptance test checks that the encoder is not running.
- `NAudio.Wasapi` is referenced directly rather than the `NAudio` metapackage: the metapackage
  only ships WASAPI for `-windows` target frameworks, and Media must stay plain `net10.0`. The
  WASAPI classes carry `[SupportedOSPlatform("windows")]` to keep CA1416 quiet at their call
  sites.
- `DxgiScreenCapture` releases each duplication frame (`ReleaseFrame`) right after `CopyResource`
  into its own texture, before raising `FrameCaptured`. Holding the frame longer stalls the whole
  duplication API. `WaitTimeout` is a normal idle screen, not an error; `AccessLost` (Win+L, UAC,
  RDP session switch, GPU reset) recreates the duplication from scratch instead of reusing it.
- `CapturePipeline` converts on the capture thread and encodes on its own thread, joined by a
  single-slot `LatestFrameSlot` that drops the older frame instead of queueing — screen video must
  never accumulate latency. NV12 buffers come from `ArrayPool` and are returned on drop.
- `IMediaLogger` / `FileMediaLogger` writes the media log to `<identity dir>/logs/media.log`
  (registered in `App.xaml.cs`, injected into `CapturePipeline`). Every capture start dumps OS,
  adapters (vendor/device id, VRAM, outputs) and every registered H.264 MFT with its `async` flag;
  encoder and capture failures are logged with the full exception. This is the file to ask for
  when someone reports an encoder problem.
- `MediaFoundationH264Encoder` drives both sync and async MFTs. Async (all hardware encoders,
  NVENC included): set `MF_TRANSFORM_ASYNC_UNLOCK` before anything else, then pump
  `IMFMediaEventGenerator` on its own `LisoP2P.EncoderPump` thread. **Each `METransformNeedInput`
  is an input credit** — when it arrives with no frame in hand it must be saved in
  `_inputRequests` and spent on the next input; dropping it stalls the encoder after one frame.
- On the async path `Encode` is fire-and-forget: it queues the sample (max 2, oldest dropped) and
  returns null. Encoded frames arrive through the `FrameEncoded` event, which is what
  `CapturePipeline` subscribes to for stats, recording and `FrameReady`. Blocking inside `Encode`
  until the matching `METransformHaveOutput` turns the async MFT into a synchronous one and puts
  NVENC's 10-107 ms latency into every frame time — measured 8 fps out for 21 fps in.
- **Never attach `IMFDXGIDeviceManager` holding the capture device** while frames are fed as
  system-memory NV12. That device is `SetMultithreadProtected(true)` and busy with
  `VideoProcessorBlt` / `CopyResource` / `Map` every frame, so sharing it serializes NVENC against
  the capture thread: measured 8 fps encode with the manager, 60 fps without it. The parameter is
  still plumbed through `VideoEncoderFactory.Create` for a future zero-copy texture path;
  `CapturePipeline` passes null on purpose.
- `TextureConverter` ping-pongs two staging textures: frame N is copied into one while `Map(Read)`
  reads the other holding frame N-1. Mapping the texture that just received the `CopyResource`
  forces a GPU→CPU sync that stalls the whole capture thread. Cost is one frame of latency, so
  `TryConvert` hands back the timestamp of the frame that came out, not the one that went in.
- The MP4 recorder is created on the **first keyframe**, not at start: the MP4 sink builds `avcC`
  from `MF_MT_MPEG_SEQUENCE_HEADER` on the negotiated output type, which the MFT only fills in
  after producing its first output. `IVideoEncoder.CreateOutputMediaType()` is what exposes it.
- `CaptureSettings.TargetHeight` (default 1080, 0 = native) drives the encode size; width follows
  the monitor's aspect, aligned to even. Encoding at native resolution on a 1440p/4K monitor was
  the other half of the fps problem.
- Forced keyframes go through `ICodecAPI::SetValue(CODECAPI_AVEncVideoForceKeyFrame)`
  (`CodecApi.cs`, raw vtable call, slot 9) — NVENC ignores the
  `MFSampleExtension_VideoEncodePictureType` sample attribute that the software MFT honors. Both
  are set; the `ICodecAPI` call is guarded by `IsSupported`.
- `CaptureSettings.ForceSoftwareEncoder` (checkbox "Forçar software" in the capture window) skips
  hardware enumeration entirely — the first thing to try when a GPU driver misbehaves.
- Fase 5 turns "the peer" into "the room". `IRoomService` / `RoomService` sits *above*
  `ISessionManager`: it owns the member list, and chat/screen/voice all target the room rather than
  a `PeerId`. There is no room owner and no consensus — the list is replicated by additive gossip
  (`RoomInvite` 60, `RoomJoin` 61, `RoomMemberList` 62, `RoomLeave` 63, `RoomSpeaking` 64, all over
  the existing TCP sessions).
- The member merge is **additive on purpose**: a `RoomMemberList` never removes anyone. A peer that
  is slow to learn about a newcomer would otherwise evict live members every time it gossiped its
  stale view. Removal happens only via explicit `RoomLeave` or a session that reached `Closed` for
  good. Re-broadcast happens **only when the merge actually learned something**, which is what makes
  the gossip terminate instead of echoing between peers forever.
- Learning about a member is also the trigger to dial it (`ConnectToMember`) — mesh membership means
  everyone holds a session with everyone. A member that discovery has not seen yet is logged and
  skipped, not retried in a loop.
- Invitations are auto-accepted. This is a deliberate product call for a trusted LAN/Radmin network:
  a confirmation dialog would leave the inviter waiting with no feedback.
- "Who is speaking" comes from the explicit `RoomSpeaking` signal on push-to-talk press/release,
  **never inferred from arriving media packets**. The speaker renews every 1 s and a listener drops
  a speaker that has not renewed in 3 s (`RoomService.SpeakingTimeout`), so an app that dies with
  the key held does not leave the indicator stuck on. `RoomService.Tick()` does both the expiry and
  the renewal; its clock is injected so the timeout is testable without `Task.Delay`.
- `MediaPacketCodec` is at **version 3**: the header grew from 13 to 29 bytes in fase 5 to carry an
  explicit `SenderId`, and to 45 bytes in fase 6 when that field became the 32-byte public key.
  In a mesh the receive socket takes datagrams from several senders at once, so
  the sender can no longer be "the only peer on the other end", and inferring it from the source
  endpoint breaks when NAT or Radmin remaps the port. `UdpMediaReceiver` keeps one `FrameReassembler`
  **per sender** (capped at 8, since the socket accepts bytes from anyone) and drops its own packets.
  The old `IMediaReceiver.ExpectedSource` IP filter is gone.
- `UdpMediaSender` takes `IIdentityStore` and stamps `SenderId` itself, so no send path can forget
  it. `SendFrame` fragments once and sends each fragment to every destination: **one encode, N
  sends**. Encode CPU does not scale with the audience; upload bandwidth does.
- Voice is the opposite of video — everyone to everyone at once. Each receiver keeps **one
  `IJitterBuffer` per sending peer**; sequence numbers are per sender, so folding sources into one
  buffer would make every packet look out of order relative to the previous one. `AudioMixer` pulls
  one frame from every active buffer each 20 ms tick (even when a buffer is empty — an undrained
  jitter buffer drifts) and sums, scaling by `1/sqrt(contributors)` and clamping.
- `IAudioMixer.MixNextFrame()` returns `float[]?`, and **null is how silence is spelled** at this
  boundary: `PullWaveProvider.Read` breaks on a null pull and zero-fills, exactly as it already did
  for `JitterBuffer.Pull`. A single contributing source is handed through **untouched** — it cannot
  overflow on its own, and `Clamp` writes in place, which would corrupt the decoder's own buffer.
- `BitrateLadder.SelectFor(receiverCount)` drives quality by member count (3000/2200/1600/1000 kbps).
  Because a step changes **fps as well as bitrate**, and both are negotiated when the encoder opens,
  `ScreenShareSession.UpdateTargetsAsync` restarts the pipeline and re-announces `ScreenShareStart`
  rather than reconfiguring in place — the restart also produces the IDR receivers need. The numbers
  are a starting point, not a measurement; calibrating them needs the four-peer test.
- Only one member shares a screen at a time. That is a **product decision** for this phase (the mesh
  does not carry N video streams well), not a protocol limit — `ScreenShareSession` ignores a second
  sender instead of opening a decoder per sender, and the UI disables the button with a tooltip.
- Room chat reuses `ChatMessage`; `ChatMessagePayload` gained `RoomId` at `[Key(2)]` (empty = 1:1) so
  `RoomChatRouter` and the 1:1 `ChatViewModel` can tell the two apart. "Broadcast" is literally a
  loop sending the same envelope once per session — there is no multicast between direct P2P
  connections. `MessageId` dedupes. Room messages are **not** acked: there is no single recipient to
  confirm, and N acks for one id would make "delivered" meaningless.
- `IRoomChatRouter.BroadcastAsync` returns the `Guid` MessageId (the spec sketched `Task`). Net does
  not reference Storage, so the caller has to persist the message under the id every recipient sees.
- `messages` has a nullable `room_id` column (migration 2 via `PRAGMA user_version`). `NULL` is the
  old 1:1 history; `GetHistoryAsync` and `GetUndeliveredAsync` filter on `room_id IS NULL` so room
  chat never leaks into a 1:1 window. For a room row, `StoredMessage.PeerId` is the **sender** (not
  the other party) so a bubble can show who wrote it.
- Fase 6 wire bumps: `Envelope.SenderId` went `Guid` → `byte[32]` (`ProtocolCodec.CurrentVersion`
  1 → 2, and `TryDecode` now rejects a sender that is not a valid key and normalizes a nil
  payload); `MediaPacketCodec` went 29 → 45 bytes and version 2 → 3; `RoomMemberInfo.PeerId` is a
  `byte[]` with malformed ids dropped on decode; SQLite stores the id as hex, and a pre-fase-6 row
  decodes to the all-zero id so old history stays readable instead of throwing.
- `AppHost` splits the composition root in two. Everything bound to a port — discovery, sessions,
  media sockets, screen share, voice, room, `MainViewModel` — is torn down and rebuilt when the
  user applies a new port; identity, settings, chat history, the capture pipeline and the
  notification banner live in the root provider and survive. `IAppShell` is the narrow surface the
  windows use, and `MainWindow` rebinds on `Rebuilt`. This is what makes "change the session port
  and apply" work without restarting the process.
- `AppSettings` is applied **only on Salvar**, never field by field: a half-typed port would
  rebuild the network stack on every keystroke. Audio devices are the deliberate exception and go
  straight to `IVoiceSession.UpdateDevices`, because switching output mid-call is an acceptance
  item. A malformed value in `settings.json` means "use the default" (`AppSettings.Normalized`),
  never "refuse to start" — the file is hand-editable.
- `IErrorPresenter` has exactly one rule: a network error is never a modal that blocks the UI
  thread. `ShowTransient` for what resolves itself, `ShowPersistent` (keyed, so a repeated report
  replaces rather than stacks) for what needs the user. Everything logged to `app.log` on the way
  through. `PortProbe` is a snapshot, not a reservation — the startup path still has to survive
  losing the race between the check and the real bind.
- Publish is single-file, self-contained, **untrimmed on purpose**: WPF does not support trimming,
  and Vortice/NAudio/Concentus reach for types through interop and reflection, so a trimmed build
  breaks at runtime on exactly the clean machine that has no SDK to debug it with. The publish
  properties live in `Properties/PublishProfiles/win-x64.pubxml` so `dotnet build` / `dotnet run`
  stay RID-agnostic; `Directory.Build.props` embeds Release symbols so the output really is one
  file.
- `RoomMemberListPayloadCodec.TryDecode` normalizes as it decodes: nicknames sanitized, empty and
  duplicate ids dropped, oversized lists rejected. MessagePack maps a bare nil onto a **null payload
  and null reference properties**, so the null guards there are load-bearing — a fuzz test caught a
  real `NullReferenceException` on random bytes.

## Commands

```
dotnet build
dotnet test
dotnet run --project LisoP2P.App -- --discovery-port 47100 --session-port 47101
dotnet publish LisoP2P.App -c Release -p:PublishProfile=win-x64
```

Manual acceptance test (see README.md): run two instances with distinct `--session-port` values
and confirm they discover each other within a few seconds and drop each other within 8 seconds of
one closing. This has been manually verified working end-to-end.

The fase 3 and fase 4 acceptance lists (share appears within 2 s, movement stays fluid, recovery
after 5 s of network loss, joining an ongoing share, 10 minutes with stable memory; PTT audible
under 300 ms, voice plus screen without either degrading, recovery after a 2-3 s network cut,
device switch mid-call) need two machines on LAN and on Radmin VPN, and have **not** been run
yet — only a two-instance startup smoke check.

The fase 6 acceptance list is partly verified. Confirmed by running the app here: the published
single-file exe starts and creates `%APPDATA%/LisoP2P/<port>/` with `identity.json`,
`settings.json` and `app.log` on a first boot; the welcome screen leads into a working network; the
settings window opens and all five tabs render; a second instance on the same `--session-port`
shows the port-conflict screen instead of crashing and logs a readable line. **Not** yet run: the
clean-machine test (a Windows box with no .NET SDK or runtime, where the native interop of Vortice,
NAudio and Concentus actually proves itself), applying a port change and confirming a new
connection uses the new port, switching the audio output mid-call, comparing fingerprints across
two instances, and pulling the network during a screen share to see the overlay. The README carries
this as a checklist.

The fase 5 acceptance list needs **three** instances (A, B, C) on LAN and on Radmin VPN: members
converge within 5 s, chat shows the right sender, A shares and both B and C receive at the 3-member
step, simultaneous speakers mix without stalling, a peer that closes drops out within ~15 s, and a
fourth peer joining from one invitation connects to all three. The four-peer run that documents
where the mesh stops scaling (bitrate step, CPU while sharing and receiving, any stutter and in
which role) is an explicit **deliverable for the README, not a pass/fail gate** — and has **not**
been run yet. Do not tune the ladder to make it look good; the measurement is the point.
