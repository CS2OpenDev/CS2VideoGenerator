# CSVG Roadmap

Items under "Planned" are ideas under exploration. Nothing there is a commitment to a specific version or date.

## Scope statement

CSVG is a **pure capture executor**. The caller supplies a compilation JSON describing *what* to capture: demos, tick ranges, spectator targets. CSVG owns *how* it is captured, and owns that it is captured correctly. Features that *generate* clip definitions (event-driven or kill-driven clip selection, highlight detection, demo analysis) are permanently out of scope. They are expected to live in upstream tooling that analyzes demos and emits compilation JSON for CSVG to execute.

## Planned / under exploration

### Hooked capture: vendor encoder ladder

Encoder selection today prefers NVENC and falls back to x264. The full ladder is
NVENC → AMF → QSV → x264 on Windows and VAAPI → NVENC → x264 on Linux, and a rung is chosen by
test-encoding a black frame rather than by parsing `ffmpeg -encoders`. A compiled-in encoder is not
an openable one — the driver decides. Probe-by-encoding is validated on real hardware at ~0.5–1.5 s
per rung, cheap enough for `doctor` and for probe-until-first-success at session start.

Left to do: the per-encoder quality table, one "near-lossless intermediate" knob translated into each
encoder's rate-control vocabulary (NVENC `-cq`, AMF `-qp_i`/`-qp_p`, QSV `-global_quality`, VAAPI
`-qp`, x264 `-crf`). Also `doctor` reporting the detected rung and why higher rungs failed.

### Hooked capture: Linux

The Windows provider hooks `IDXGISwapChain::Present`. The Linux analog is an implicit Vulkan layer on
`vkQueuePresentKHR`, enabled by env var at launch (obs-vkcapture is the working precedent); CSVG owns
the launch environment, so nothing needs patching. Blocked on a Linux machine with a GPU. Before any
of the Vulkan work gets designed there is an inventory to run: that the gameinfo.gi plugin chain
loads in Linux CS2 (`libserver.so`), that gRPC connects and demo control works, that `startmovie`
behaves the way it does on Windows (output layout, tokens, wav), and that the render path really is
the assumed Vulkan swapchain (rendersystemvulkan).

### Hooked capture: zero-copy encode (contingent)

Build this only if the hooked provider ever measures readback-bound. The shape: export the backbuffer
copy as a shareable GPU object (DXGI shared NT handle on Windows, DMA-BUF on Linux) and feed a
hardware encoder directly, so pixels never cross to the CPU. The encoder stays ladder-selected, never
NVENC-only.

There is no pressure today. The plain anonymous pipe moves worst-case raw frames at 7.9 GiB/s, 4x
headroom over a 1080p 256 fps target, so CPU transport is not the bottleneck. Vulkan Video encode is
the watch-list item beyond that, worth adopting only once its encode extensions are stable in
mainline drivers across all three vendors and ffmpeg's Vulkan encode path is production-ready.

### Mid-clip camera switching

A clip has exactly one spectator target. Following the action across players inside one continuous fight therefore means splitting it into several clips, which costs extra seek/settle time and produces visible cuts at the file level. Rough scope: extend the compilation clip schema to accept multiple spectator targets keyed by tick within a single clip's range, and have the orchestrator issue `SetSpectatorTargetCommand` at the right ticks during playback.

### Dropped-frame detection and reporting

With realtime OBS capture, dropped/skipped frames can silently degrade output quality and the caller currently has no way to know a clip came out imperfect. Rough scope: query OBS output statistics (skipped/dropped frame counters) around each clip capture and surface them per clip in `CaptureResult`, so callers can decide to re-capture or flag the clip.

### Per-player voice audio policy

Deferred from the OBS audio capture toggle work. A global audio on/off is straightforward, but users producing highlight content often want finer control: include the featured player's voice comms but mute others, or exclude voice entirely while keeping game audio. This interacts with demo voice-data availability, in-game voice playback convars, and how OBS captures the audio mix, which is why it was split out. Rough scope: define a per-clip (or per-compilation) voice policy in the JSON schema and drive game/OBS audio state from it.

### Death-notice duration control

CS2's kill feed entries fade after a fixed time. A kill early in a clip's tick range can be gone from the feed by the moment of interest, or clutter from earlier kills can still be sitting there. Rough scope: expose the relevant convar(s) for death-notice display duration as a clip option, so callers can pin the feed for the length of a clip.

### FFmpeg auto-download

CSVG requires FFmpeg on the PATH (or `Ffmpeg:BinaryDirectory`) for in-engine capture and concatenation. That is a manual setup step, and a common first-run failure. Rough scope: on first use, offer to download a pinned, checksum-verified FFmpeg build into CSVG's data directory and use it automatically; `doctor` reports which FFmpeg is in use.

### Post-run hook

CSVG is designed to sit inside larger pipelines (upload, notification, library ingestion), which currently must poll for output files or wrap the CLI themselves. Rough scope: an optional shell command, set in config and/or the compilation, run on completion whether the run succeeded or failed. Context such as output path, clip results and exit status goes in via arguments or environment variables.

### Dynamic gRPC port negotiation

The bundled plugin and mock_server hardcode `127.0.0.1:50051`, so `--grpc-port` requires a custom plugin build and concurrent CSVG instances on one machine are not possible. Rough scope: a plugin-side channel for the port (launch argument, environment variable, or a config file written at plugin install time), then `--grpc-port 0` (dynamic) support in the CLI and library.

## Considered and not planned

### HLAE as a third capture provider

The reason to integrate HLAE was its async swapchain capture. CSVG has that now: the
experimental `InEngineHooked` provider does HLAE's core performance trick - async present-hook
capture with a delayed-map staging ring - inside CSVG's own plugin, measured at 2.70x realtime at
1080p60 against `InEngine`'s 1.15x. See [in-engine-capture.md](in-engine-capture.md) for the
design; the remaining hooked-capture work is listed above. What HLAE still has that CSVG does not
is the auxiliary streams, depth and mattes. If those are ever wanted, that becomes its own roadmap
item rather than a reason to integrate HLAE wholesale.

## Shipped

- **In-engine `startmovie` capture** (`InEngine`, the default provider): offline, frame-exact
  rendering at an arbitrary fps, with frame-sequence consumption and game-time audio muxing. `OBS`
  remains selectable with `--capture-provider`. See [in-engine-capture.md](in-engine-capture.md).
- **Offline slow motion and fast-forward**: the per-clip `playbackSpeed` field
  (`CaptureMetadata.PlaybackSpeed`) and `watch --speed`, with `--normalize` /
  `produceNormalizedCopy` for a lossless real-time companion. Implemented as a capture/encode rate
  split; demo timescale is measurably inert for offline capture. Mid-clip speed ramps remain out of
  scope.
- **Event-driven plugin readiness**: the plugin hooks `Source2Client002::FrameStageNotify` and
  executes console commands on the engine thread, so the fixed sleeps around connect and demo setup
  are gone. The engine offers no positive "demo loaded" or "seek complete" signal, so those remain
  bounded polls at engine-frame granularity.

## Known limitation

**Spectating by SteamID64 is blocked by CS2 convar/command issues; spectating is by player name.** The compilation schema carries `playerSteamId` as metadata, but the camera is targeted with the `spec_player` console command using `playerNameToSpectate`, which must exactly match the player's display name in the demo. If CS2 gains a reliable SteamID-based spectator command, CSVG will switch to (or additionally support) SteamID targeting.
