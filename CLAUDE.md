# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

**Fase 4** (of 6 planned phases) is implemented: fase 0 (scaffold, peer identity, wire protocol,
UDP broadcast discovery, manual-connect fallback), fase 1 (TCP session with handshake/keepalive/
reconnect, 1:1 chat with SQLite history), fase 2 (DXGI screen capture, H.264 encode, capture
test window), fase 3 (fragmented UDP media transport, H.264 decode, 1:1 screen sharing wired
into the conversation window) and fase 4 (WASAPI capture, Opus, jitter buffer, push-to-talk
voice on the same media socket).

`LisoP2P.App/Docs/fase0-prompt.md` is the original Portuguese specification for the first phase
(`fase1-prompt.md` through `fase4-prompt.md` cover the later ones). It
names project prefixes as `P2PChat.*` and targets `net8.0` — this repo instead kept the
`LisoP2P.*` prefix and targets `net10.0`/`net10.0-windows` (explicit choices made when starting
implementation, since the scaffold already used net10.0). Everything else in the spec was
followed as written; read it before touching Core/Net if you need the full behavioral contract.

## Architecture

Five projects, referenced as `Media → Core`, `Net → Core, Media`, `App → Core, Net, Media`,
`Tests → Core, Net, Media, Storage`:

- **`LisoP2P.Core`** (classlib, `net10.0`, no `-windows`) — `PeerId`, `IIdentityStore` /
  `FileIdentityStore`, `DiscoveredPeer`, and the `Protocol/` namespace: `Envelope`, `MessageType`,
  `AnnouncePayload`, and their MessagePack codecs (`ProtocolCodec`, `AnnouncePayloadCodec`),
  plus the media wire format: `MediaPacketHeader` / `MediaPacketCodec` (fixed 13-byte binary
  header, no MessagePack), `ScreenSharePayload` / `ScreenSharePayloadCodec` and
  `VoicePayload` / `VoicePayloadCodec`.
- **`LisoP2P.Net`** — `NetworkOptions`, `IDiscoveryService` / `DiscoveryService` (UDP broadcast
  discovery), `BroadcastAddressCalculator`, `ManualPeerConnector` (unicast fallback),
  `PeerSession` / `SessionManager`, and the media path: `IMediaSender` / `UdpMediaSender`,
  `IMediaReceiver` / `UdpMediaReceiver`, `FrameReassembler`, `IJitterBuffer` / `JitterBuffer`,
  `IScreenShareSession` / `ScreenShareSession`, `IVoiceSession` / `VoiceSession`.
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
- **`LisoP2P.App`** (wpf, `net10.0-windows`) — WPF/MVVM UI (`CommunityToolkit.Mvvm`), composed via
  `Microsoft.Extensions.DependencyInjection` in `App.xaml.cs` (no separate DI framework).
- **`LisoP2P.Tests`** (xunit) — codec round-trip/malformed-input coverage, identity persistence,
  broadcast address calculation, fragment reassembly, screen-share orchestration, and a real
  encode/decode round trip through Media Foundation.

**Hard constraint:** Core, Net, and Media target plain `net10.0` (no `-windows`) and must never
reference `System.Windows`. Anything UI-facing needed from Net is exposed via an event/callback,
never a direct dependency.

Design points worth knowing before touching this code:
- `PeerId` is a random `Guid`, persisted via `FileIdentityStore` under
  `%APPDATA%/LisoP2P/identity.json` by default. It is never derived from IP, since IP changes
  when Radmin VPN is involved but identity must not.
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
- `--discovery-port` / `--session-port` / `--media-port` CLI args override `NetworkOptions`
  (defaults 47100 UDP / 47101 TCP / 47102 UDP) — required for running two local instances side
  by side. When `--session-port` is non-default and `--media-port` is absent, the media port is
  `session-port + 1`, so the second instance does not fight the first for the UDP socket.
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

## Commands

```
dotnet build
dotnet test
dotnet run --project LisoP2P.App -- --discovery-port 47100 --session-port 47101
```

Manual acceptance test (see README.md): run two instances with distinct `--session-port` values
and confirm they discover each other within a few seconds and drop each other within 8 seconds of
one closing. This has been manually verified working end-to-end.

The fase 3 and fase 4 acceptance lists (share appears within 2 s, movement stays fluid, recovery
after 5 s of network loss, joining an ongoing share, 10 minutes with stable memory; PTT audible
under 300 ms, voice plus screen without either degrading, recovery after a 2-3 s network cut,
device switch mid-call) need two machines on LAN and on Radmin VPN, and have **not** been run
yet — only a two-instance startup smoke check.
