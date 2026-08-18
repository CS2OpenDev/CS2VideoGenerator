# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.2] - 2026-07-31

### Added

- `watch` now records at 30 fps by default. `--fps` (or
  `Cs2VideoGenerator:DefaultCaptureFrameRate`) changes it, and `--fps 60` is still the smoothest
  result. For the offline in-engine providers every output frame is a real rendered frame, so 30 fps
  renders half as many frames as 60 and captures markedly faster while staying smooth enough for
  review. Only the interactive `watch` path moved: the library `CaptureMetadata.FrameRate` and the
  compilation schema's `frameRate` keep their 60 default, so a committed compilation and any direct
  library caller render exactly as before.

- Slow motion, fast-forward and normalized copies reached the compilation JSON and `watch`. The
  offline speed features are no longer library-only: per-clip `playbackSpeed` and
  `produceNormalizedCopy` fields in the compilation JSON, plus `watch --speed <x> [--normalize]`.
  `playbackSpeed` renders real slow-motion/fast-forward frames (0.5 = 2x slow motion, 2 = 2x fast);
  `--normalize` / `produceNormalizedCopy` additionally emits a lossless real-time copy alongside.
  Both surface on `CaptureResult.AdditionalOutputs` and in `doctor`'s verbose InEngine config dump,
  which now includes `Capture:InEngine:KeepEngineAudio`.

- `CaptureMetadata.ProduceNormalizedCopy` builds a speed clip's real-time companion in the same
  capture. The video is retimed by timestamp rewrite (`-itsscale` + stream copy, no re-encode) and
  the audio is the engine's original game-time wav muxed untouched - zero tempo-processing passes,
  unlike a post-hoc renormalization (measured ~13 % transient/flux loss plus WSOLA phase scrambling,
  content-controlled). `Capture:InEngine:KeepEngineAudio` separately preserves the pristine wav next
  to the output for external post-processing. Both surface on `CaptureResult.AdditionalOutputs`.

- Offline slow motion: `CaptureMetadata.PlaybackSpeed` (library-level; 0.5 = 2x slow motion,
  0.25 = 4x). The in-engine providers render at `FrameRate / speed` - every slow-motion frame is
  a real rendered frame, not an interpolation - encode at `FrameRate`, and stretch the engine's
  game-time audio by the same factor (pitch-preserving `atempo`) so the tracks stay in step.
  Validated on real CS2 with the hooked provider at 0.5 (120 fps capture) and 0.25 (240 fps
  capture - the engine honors `host_framerate 240`); a 4x slow-mo clip of a 31 s moment renders
  in 33 s. Not `demo_timescale`, which is measurably inert for offline capture; the realtime OBS
  provider instead gets genuine speed capture by playing the demo at a timescale.

- `watch` outputs land somewhere predictable and say what they are. Ad-hoc recordings go to a
  configured directory (`Cs2VideoGenerator:OutputDirectory`, bundled default `captures/`,
  `--output-dir` to override) instead of wherever the command ran, and the default file name encodes
  the capture configuration and a timestamp:
  `<demo>_<start>-<end>.<provider>.<WxH>.<fps>fps[.<format>.<quality>[.<preset>]].<timestamp>.mp4`.
  `--output-name` restores full manual control.

- New `InEngineHooked` capture provider, experimental and Windows only. Same offline `startmovie`
  clock and audio as `InEngine`, but frames come off the swapchain through the plugin's new DXGI
  present hook and stream to ffmpeg as raw RGBA over a named pipe - no synchronous engine readback
  and no frame files. Measured **2.70x realtime at 1080p60** against `InEngine`'s 1.15x on the
  reference machine. Select with `--capture-provider InEngineHooked`; it shares the
  `Capture:InEngine` configuration section and requires a plugin build advertising the new
  `present-capture` capability. `InEngine` remains the default. See
  [`docs/in-engine-capture.md`](docs/in-engine-capture.md).

### Changed

- `IFfmpegService` gained `RemuxRetimedAsync` as a default interface method, so existing custom
  implementations keep compiling; override it to enable slow-motion/normalized capture on a custom
  service. `MuxAsync` gained an optional `double? audioTempo` parameter, which a custom
  implementation does have to add to its `MuxAsync` override. Callers are source-compatible.

## [1.1.1] - 2026-07-31

The orchestration layer is an object graph now instead of one 3,200-line client, the engine-thread
plugin work is finally in effect on real CS2, and the `InEngine` capture provider is the
**default** (`Tga` frame format, record length derived from the clip resolution; select `OBS` to
record through an OBS profile instead). It was validated end to end against a real CS2 build during
this cycle. Breaking release, no compatibility shims —
[`docs/migrating-to-2.0.md`](docs/migrating-to-2.0.md) carries the member-by-member mapping, the
construction paths and the behavior changes.

1.1.0 was published and then withdrawn; this is that release plus the three fixes below. It
shipped a macOS `mock_server` that predated the 2.0 wire protocol (so `--mock` mode was broken
on Apple Silicon) and a capture statistic that vanished on Windows. Neither was visible to the
release pipeline. Use 1.1.1: 1.1.0's GitHub Release has been deleted and its packages should
not be consumed. A published version can never be replaced in place, only deleted, which is why
these fixes ship as 1.1.1 rather than a re-cut 1.1.0.

### Removed

- `ICsvgClient` and `CsvgClient` are gone. The everyday entry point is `CsvgVideoSession`,
  composed by `CsvgVideoSessionBuilder` or resolved from DI after `AddCs2VideoGeneratorCore`.
  It owns the session lifecycle, the capture wiring and the events; demo control lives on
  `CsvgVideoSession.Engine`, the `ICs2EngineSession` for the current CS2 run.

  ```csharp
  // before
  ICsvgClient client = services.GetRequiredService<ICsvgClient>();
  await client.StartSessionAsync(1920, 1080, fullscreen: false);
  await client.LoadDemoAsync(path);
  client.SessionCaptureMetadata = metadata;
  await client.PlayDemoTickRangeAsync(start, end, record: true);
  Cs2CompilationResult result = await client.CaptureCompilationAsync(compilation);
  await client.StopSessionAsync();

  // after
  CsvgVideoSession session = services.GetRequiredService<CsvgVideoSession>();
  await session.StartAsync(new EngineSessionOptions { Width = 1920, Height = 1080 });
  await session.LoadDemoAsync(path);
  await session.CaptureClipAsync(new ClipCaptureContext
  {
      StartTick = start, EndTick = end, Metadata = metadata, SpectatorTarget = "s1mple"
  });
  Cs2CompilationResult result = await session.RunCompilationAsync(compilation);
  await session.StopAsync();
  ```

  Renames worth calling out: `StartSessionAsync`→`StartAsync`, `StartWatchSessionAsync`→
  `StartWatchAsync`, `StopSessionAsync`→`StopAsync`, `WaitForSessionEndAsync`→
  `WaitForEndAsync`, `CaptureCompilationAsync`→`RunCompilationAsync`,
  `PlayDemoTickRangeWithCaptureAsync`→`CaptureClipAsync`. Demo control
  (`PauseDemoAsync`, `SetDemoTickAsync`, `QueryDemoStateAsync`,
  `ScheduleCommandsAtTickAsync`, …) and the latched engine truth (`LastTick`, `LastDemoState`,
  `PluginCapabilities`, `Supports`) moved to `session.Engine` unchanged, and
  `CaptureProviderState` is now `CaptureState`. `PlayTickRangeAsync` exists on both objects:
  the one on `CsvgVideoSession` returns a `DemoPlaybackResult` and replaces
  `PlayDemoTickRangeAsync`, the one on `session.Engine` throws. The engine-truth events
  (`TickUpdated`, `DemoStateChanged`, `GameEventReceived`, …) stay on `CsvgVideoSession` and
  survive a Start→Stop→Start cycle.
- `IVideoCaptureProvider` is gone, replaced by the three-object capture model:
  `ICaptureSessionFactory` (one per backend) → `ICaptureSession` (one per CS2 run) →
  `IClipCapture` (one per clip). A host that registered its own provider registers an
  `ICaptureSessionFactory` instead.
- `CsvgClient.SessionCaptureMetadata` and `CsvgClient.ClipIndex`: output metadata now
  arrives with each clip, so there is no session-scoped naming state to configure, or to
  leak into the next run. Nothing numbers a one-off clip's output file any more.
  `CaptureMetadata.WithClipIndex(i)` is the one place an indexed name is formed and the
  caller applies it; `CaptureMetadata.GetFullOutputPath(int clipIndex)` is gone with it.
- `CaptureConfiguration.ConnectionTimeoutMs` and `CaptureConfiguration.AdditionalSettings`,
  which nothing read. Provider-specific settings belong to the provider's own configuration
  type, bound from `Capture:<name>`.

### Fixed

- `captureFps` and `realtimeFactor` are no longer dropped from a fast clip's statistics. Both were
  emitted only when the measured render window was greater than zero, and `DateTime.UtcNow` ticks
  about every 15.6 ms on Windows, so a short clip out of a fast encoder finished between two reads
  and measured as zero. The two rates then silently went missing from `CaptureResult.Statistics`
  and from the CLI's capture summary. The elapsed divisor is now floored at the clock's resolution
  rather than used as a gate, so anything that rendered at all reports both rates.

- The bundled macOS (`osx-arm64`) `mock_server` is rebuilt against the current protocol. The
  committed prebuilt still predated wire fields 12-15, so `--mock` mode on Apple Silicon ran against
  a mock that could not speak the 2.0 wire format. Nothing catches this automatically: the test suite
  builds its own mock rather than running the committed one. See
  [`docs/RELEASING.md`](docs/RELEASING.md#the-macos-mock-is-a-committed-prebuilt-not-a-ci-build-product).

- `server_plugin.cpp` formatting. Two clang-format violations were blocking the lint job.

- Console commands now execute on CS2's engine thread. The plugin used to call
  `ISource2EngineToClient::ExecuteClientCmd` straight from its background gRPC and polling
  threads, which is not thread-safe and crashed demo playback deterministically
  (`engine2.dll` access violation during the load window). It now patches
  `Source2Client002::FrameStageNotify` (slot 36, the same technique CS Demo Manager's plugin
  uses) and drains a command queue at `FRAME_START`. Once the hook is up, engine state is read
  on the engine thread too, where demo teardown cannot race the reads.

  Interface availability drives the hook install, not game state. `client.dll` is not loaded
  when the plugin's `Connect` runs, and `Source2GameClients001::ClientFullyConnect` does not
  fire at the main menu; reaching it requires executing `playdemo`, the very command that is
  unsafe unhooked. So the polling loop retries the install every cycle until `Source2Client002`
  resolves, with `Connect` and `ClientFullyConnect` left in as backstops. Until the hook is up,
  commands wait in the queue rather than degrading to a direct call: there is no thread-safe
  direct path to degrade to. A command waiting past 10 seconds is named in the plugin log
  (`[gRPC] STRANDED command: …`), and the host's existing command timeouts surface the failure
  to the caller.
- `startmovie` is dispatchable again. CS2 has shipped it (and `spec_lock_to_accountid`) with
  `FCVAR_HIDDEN` since the Armory update, and a hidden command fails silently under
  `ExecuteClientCmd`. The plugin's `Connect` now clears `FCVAR_HIDDEN | FCVAR_DEVELOPMENTONLY`
  from every registered command and cvar before the gRPC client starts, and logs how many it
  unhid. The `InEngine` capture provider depends on this.
- Start→Stop→Start recorded through a disposed capture provider. The "capture is initialized"
  flag survived a stop, so the second run skipped capture bring-up and drove the previous run's
  torn-down backend. Capture sessions are now created per CS2 run, and the stopped one is
  disposed when it is replaced.
- A second run reused the first run's clip numbering and output metadata, silently overwriting
  the first run's files. Both were session-scoped mutable state; both are gone.
- Two concurrent session starts could both proceed. The "am I disconnected?" check and the claim
  on the session were separate lock acquisitions, so a caller arriving in that window started a
  second session on top of the first. The claim now happens under the same lock acquisition as
  the check.
- Two concurrent session stops could both proceed, for the same reason: `StopAsync` read the
  state outside the gate and only flipped it to `Disconnecting` afterwards. The duplicate
  teardown was survivable, but the losing stop announced `Disconnected` from its `finally`
  while the winner was still killing CS2 — enough for a concurrent start to be accepted
  against a dying process. A stop arriving while one is in flight now returns instead of
  running a second teardown.
- `InitializeCaptureAsync` leaked the capture session it replaced. The documented manual
  re-initialization path swapped in a new capture session without disposing the old one,
  leaking its process manager and leaving its event handlers attached. Related: OBS now drops
  its audio mute controller with the connection (a re-initialized session used to issue mute
  calls against a dead socket), and `CaptureSessionContext.SessionToken` is a token tied to
  the capture session's lifetime rather than the caller's bring-up token, which could be
  cancelled or disposed while the session was still alive.
- Playing a tick range could time out waiting for a pause the plugin had already reported.
  The auto-pause `PAUSED` status change follows the forced end-tick update within microseconds,
  and the wait-for-pause phase checked the latched status *before* subscribing to the
  status-change event. A status dispatched inside that gap was lost, nothing later re-signals
  it, and the call failed with a `WaitForPause` timeout despite playback having paused on cue.
  Observed as a roughly-every-other-run flake in the mock-mode E2E smoke test's fallback leg,
  and reachable with the real plugin too. The wait scaffolding now re-probes latched state
  immediately after the subscription is installed, so the check can no longer race the event
  dispatch.

### Added

- Tick-scheduled console commands (`tick-scheduled-commands` capability):
  `ScheduleCommandsAtTickCommand` registers commands against a demo tick, and the plugin
  executes them on the engine thread the first time the observed tick crosses the target
  (half-open `(lastTick, currentTick]` interval, HLAE's `mirv_cmd` semantics, so seeks and
  jumps cannot step over a schedule). One-shot; cleared by `LoadDemoCommand` or
  `ClearScheduledCommandsCommand`. The `PlayDemoTickRangeCommand` auto-pause now rides the
  same scheduler.
- `ExecuteConsoleCommandsCommand` queues a console command batch for the engine thread with no
  tick condition at all (`console-exec` capability), which is what a failing clip needs — a
  restore scheduled at a tick that will never be played restores nothing. The commands share the
  plugin's one engine-thread queue with everything else, so ordering against tick-scheduled
  batches and demo control is preserved. Blind by nature (the engine reports nothing back about
  a console command), so the ack means "queued for the engine thread". Surfaced as
  `IEngineConsole.ExecuteConsoleCommandsAsync`, and used by
  `ClipCaptureCoordinator` to run a capture backend's `TickCommandPlan.AbortCommands` when a
  clip fails; previously those could only be logged as a warning.
- Opt-in game-event telemetry, off by default (`game-event-telemetry` capability):
  `SetGameEventSubscriptionCommand { enabled, event_names }` lazily installs a
  `CGameEventManager::FireEventClientSide` hook (never at plugin load) and forwards each
  matching event as `GameEventTelemetryEvent { event_name, json_payload }`, serialized by
  the game's own tier0 `SaveKV3AsJSON`. Events are queued (bounded, drop-oldest) and sent
  off the engine thread. The hook is available on the mock server and the Windows plugin;
  the Linux plugin withholds the capability for now, since the vtable lookup is MSVC-RTTI
  specific. Toggles: `--game-events` / `Cs2VideoGenerator:EnableGameEventTelemetry`
  (+ `GameEventNames` filter).
- The `InEngine` capture provider: offline rendering through CS2's own `startmovie` and
  `host_framerate`. The engine advances game time by exactly `1/fps` per rendered frame
  regardless of wall clock and dumps one image per frame; CSVG streams those into FFmpeg and
  unlinks them as it goes, then muxes on the wav the engine wrote. Output is frame-exact — no
  dropped frames, no timing jitter — and it works headless. `CaptureResult.Duration` is game
  time (`frames / fps`); wall clock, achieved fps and the realtime factor go in `Statistics`.
  `CaptureMetadata.FrameRate` is honored per clip, which is what the new
  `CaptureTimingModel.TickExact` trait means. It requires a plugin advertising **both**
  `tick-scheduled-commands` (for `startmovie`, which genuinely needs a tick) and `console-exec`
  (for the cvar setup, which does not), and says which one is missing.

  Settings live under `Capture:InEngine`, with the shape of CS2's `startmovie` output gathered
  under `Capture:InEngine:EngineOutput`. The frame pipeline has been validated against a real
  CS2 build (2000879, 2026-07-30). `startmovie` writes each movie into a timestamped
  subdirectory (`movie/<yyyy_MM_dd_HH_mm_ss>/`) of the movie root under the csvg search path,
  numbers frames with 8 digits from 0, and `jpg` mode works. The defaults now encode those
  observed facts: the frame consumer discovers the timestamped directory, audio is requested
  with the `wav` startmovie token, and jpeg quality rides the `startmovie` line
  (`jpeg_quality <q>` is an argument, not a cvar; `JpegQualityCvar` became
  `JpegQualityArgument`). The wav lands under `csgo/movie/<timestamp>/`, a different root than
  the frames, at game-time length. Targa mode is uncompressed 32-bit (`TgaBitsPerPixel`
  corrected from 24; 1080p is 8,294,418 bytes per frame), with the format token mandatory
  (`TgaFormatToken`, new — a token-less startmovie is the engine's AVI path and writes no
  frames). Both formats were validated end to end: 1080p60 h264 with AAC audio from a real CS2.
  Still unverified: the cvar restore values and the tick-96 clamp. `csvg doctor` and
  `CheckAvailabilityAsync` lead with exactly what is and is not confirmed. See
  [`docs/in-engine-capture.md`](docs/in-engine-capture.md).

  Audio carries a duration guard: the engine's wav is compared against the video's game-time
  length and a disagreement beyond 2% is reported. CSGO shipped for three years writing movie
  audio at wall-clock length against movie-length video, which produces a file that plays
  perfectly and drifts further out of sync the longer it runs. Targa's record length is derived
  from the clip resolution (`Capture:InEngine:EngineOutput:TgaFrameSizeBytes` overrides it),
  because FFmpeg has no Targa parser
  and can only split a Targa pipe at a fixed record length; every frame is checked against it and
  the first disagreement fails the clip, since compressed frames would otherwise produce a video
  of noise that decodes without complaint.

  The intermediate encode is tuned for an offline pipeline rather than a live one. The frame pile
  in staging decouples the render from the encoder, so a slower encoder costs wall clock and
  nothing else, and for a single-clip capture that encode *is* the delivered quality, because
  nothing re-encodes after it. NVENC now defaults to `-preset p7 -tune hq` and x264 to
  `-preset veryfast` (not `ultrafast`, whose quality loss at a given CRF is real), both overridable
  with `Capture:InEngine:IntermediateEncoderPreset`. A failed encode now logs ffmpeg's own stderr
  alongside the exit code, and `csvg watch` prints what the render achieved
  (`Rendered 564 frames in 8.3 s wall (68 fps, 1.13x realtime), encoder h264_nvenc p7`).
- A keyed capture-provider registry. Each backend registers an `ICaptureSessionFactory` under
  its provider name (`"OBS"` and `"InEngine"` today; `"Hlae"` is next), and the selected one is
  resolved from `Cs2VideoGenerator:VideoCaptureProvider`. Provider names are matched
  case-insensitively and normalized to their canonical spelling, an unknown name is rejected
  when the host starts (naming the valid values), and a backend that cannot run on the current
  OS fails there too rather than halfway through capture bring-up. The CLI gained
  `--capture-provider`, and `csvg doctor` reports the selected provider and runs that provider's
  own checks.
- `CsvgVideoSessionBuilder`, for hosts without a DI container. It exposes the engine host it
  builds (`session.EngineHost`) because the `Cs2GameService` your host maps has to be built over
  the same one.
- The C# surface for those two plugin features, on `ICs2EngineSession`:
  `ScheduleCommandsAtTickAsync`, `ClearScheduledCommandsAsync`,
  `SetGameEventSubscriptionAsync`, and the `GameEventReceived` event (re-exposed on
  `CsvgVideoSession`). They follow the existing capability-gating rules
  (`NotSupportedException` when the token is absent) and wait for the plugin's ack where
  `command-ack` is available.
- The mock server grew an engine-frame pump standing in for CS2's engine thread
  (`CSVG_MOCK_FRAME_HZ`, default 250 Hz; `0` exercises the no-hook fallback path) and a
  `game-event <name> [json]` stdin injection command (`MockUserActions.GameEvent`).

### Changed

- A single clip can now carry a spectator target. `CaptureClipAsync` takes a
  `ClipCaptureContext`, so a one-off clip gets the same `spec_player` re-assert scheduled just
  before its recording tick that a compilation's clips have always had — the workaround for
  seeking into a clip dropping the spectator lock. It can carry the clip's demo options too.
- A clip is refused when OBS is already recording. OBS readiness is verified once at session
  bring-up (which is where the 500 ms game-capture stabilization delay belongs), so a recording
  started afterwards used to surface as an opaque `StartRecord` failure, or fold the clip into
  somebody else's file. Each clip now checks first and fails with a clear message.
- Capture metrics are tagged `"OBS"` instead of `"ObsCapture"`. The `provider` tag on
  `cs2.capture.provider.status` and on `cs2.capture.session.count` now carries the provider's
  selection name, which is the same string `--capture-provider` and
  `Cs2VideoGenerator:VideoCaptureProvider` take. Dashboards and alerts filtering on
  `ObsCapture` need updating.
- Tracing span names follow the types that own the work: the demo-control and playback
  spans are `Cs2EngineSession.*` (they were `CsvgClient.*`), and the session lifecycle spans
  are `CsvgVideoSession.Start` / `.Stop` / `.CaptureClip` / `.PlayTickRange`.
- The plugin reports READY when the engine is demonstrably alive: first successful engine state
  read plus first observed engine frame, instead of after a blind 5-second sleep (which remains
  only as a fallback cap for configurations where the frame signal cannot arrive yet). The 50 ms
  sleeps between demo-setup console commands are gone, since the batch executes within a single
  engine frame, and the demo-load/seek-settle waits poll at engine-frame granularity. Mock-mode
  sessions connect and load demos in well under a second; on real CS2 the pre-demo path still
  depends on when the frame hook can be installed.

## [1.1.0] - 2026-07-30 [YANKED]

Published, then withdrawn the same day: the bundled macOS mock could not speak the 2.0
wire protocol it shipped alongside, and `captureFps`/`realtimeFactor` were dropped from
fast clips on Windows. Superseded by 1.1.1, which contains everything 1.1.0 did plus
those fixes. The GitHub Release was deleted; the tag is kept for provenance.

## [1.0.2] - 2026-07-28

### Fixed

- Shipped binaries no longer embed the build machine's filesystem paths. `__FILE__` expansion in
  gRPC/abseil/OpenSSL baked the absolute path of every compiled source file into the native assets:
  the 1.0.0 `osx-arm64` `mock_server` carried 404 occurrences of the maintainer's home directory
  (including a scratch worktree path), and the CI-built `win-x64`/`linux-x64` assets carried the
  runner's. `cmake/path-privacy.cmake` now maps those prefixes away for first-party sources and
  every vcpkg dependency (`-ffile-prefix-map`, `/clang:`-forwarded for `clang-cl`, `/pathmap:` for
  MSVC); an overlay-port patch decouples OpenSSL's compiled-in `MODULESDIR`/`ENGINESDIR` from
  vcpkg's build-tree prefix and stops `buildinf.h` recording the build command line, which contains
  the prefix-map flags and so re-introduced the paths they remove; and `/PDBALTPATH:%_PDB%` keeps
  the Windows PDB path out of `server.dll`/`mock_server.exe`. `server.so` also recorded the build
  tree's HL2SDK stub directory as `DT_RUNPATH`, a linker artifact no compiler flag touches; the
  build-tree RPATH is now disabled, which nothing needs, since CS2 has already loaded tier0 by the
  time the plugin is loaded. Managed assemblies set `ContinuousIntegrationBuild` in CI, normalizing
  the recorded PDB and source-root paths. Reported by DemoViewer.NET; see
  `docs/build-path-privacy.md`.
- The macOS vcpkg port build was broken on current toolchains. `arm64-osx-static.cmake` tested
  `CMAKE_HOST_SYSTEM_PROCESSOR`, which is empty in CMake script mode (how vcpkg evaluates
  triplets), so every Apple Silicon build silently took the cross-compilation branch. And because
  `VCPKG_CMAKE_SYSTEM_NAME Darwin` marks ports as cross-compiling, CMake 4 stopped auto-detecting
  the macOS SDK, leaving a bare `-isysroot` that swallowed the next flag and broke every
  `#include`. Host arch now comes from `uname -m`, and `cmake/osx-sysroot.cmake` resolves the SDK
  with `xcrun --show-sdk-path` — including for vcpkg's built-in `arm64-osx` host triplet, which
  `cmake/triplets/arm64-osx.cmake` now overlays for that single addition.
- The OpenSSL overlay port now applies to every preset, not only the Windows cross build,
  so all RIDs link the same OpenSSL version (3.6.0) rather than macOS/Linux silently
  using the registry's 3.4.0.

### Added

- `.github/scripts/check-embedded-paths.py` and the gates that run it: CI scans the
  committed macOS prebuilt on every push, the native build scans each RID's output, and
  the release workflow scans the packed `.nupkg`s and CLI archives before anything is
  published. `refresh-macos-mock.sh` additionally refuses to build from a linked git
  worktree and rejects a binary that fails the scan.

## [1.0.1] - 2026-07-27 [YANKED]

Tagged but never published: the release workflow could not start (Actions budget), and
the fix shipped as 1.0.2 instead. Nothing in 1.0.2 differs from what 1.0.1 would have
contained.

## [1.0.0] - 2026-07-21

Initial public release.

### Added

- CLI (`csvg`) with commands:
  - `generate` — capture video clips from CS2 demos based on a compilation JSON definition (`--file` or inline `--compilation`), with optional FFmpeg concatenation of clips into a single output file.
  - `watch` — launch CS2 and play a demo, recording by default (`--no-capture` to opt out), with tick-range, resolution, and fullscreen options.
  - `doctor` — diagnose configuration: Steam/CS2/OBS discovery, plugin configuration, and backup directory status; reports the CSVG package version, the bundled plugin's presence and SHA-256 content hash, and the installed CS2 game build read from `game/csgo/steam.inf`.
  - `backup` / `restore` / `cleanup` — manage backups of the game files CSVG modifies, with interactive multi-select prompts, `--list`, `--all`, `--dry-run`, `restore --adjacent`, and `cleanup --max-age`.
- An optional `settings.encoding` block in the compilation JSON (`videoCodec`, `crf`/`videoBitrate`, `audioCodec`, `audioBitrate`, plus raw `extraInputArgs`/`extraOutputArgs` escape hatches) re-encodes the final concatenated output. Default remains lossless stream copy. Per-clip recording quality stays governed by the OBS profile.
- Core library (`Cs2VideoGenerator.Core`) with the `ICsvgClient` orchestration API: CS2 session lifecycle, demo control (load/play/pause/seek/tick ranges), spectator targeting, synchronized capture, and batch compilation capture.
- C++ engine plugin (`server.dll` / `server.so`) interfacing with CS2 via HL2SDK, communicating with the orchestrator over gRPC bidirectional streaming; packaged as NuGet native assets under `runtimes/<rid>/native/` for win-x64 and linux-x64.
- Plugin/game version handshake with a warn-not-block compatibility check. The plugin embeds its CSVG version and the HL2SDK commit it was built against at build time, and reports both plus the running CS2 build (steam.inf `ClientVersion`) when it connects. At session start CSVG compares the versions: matching/known-good versions are silent, newer or unknown versions log a single warning and proceed (game updates usually do not break the plugin), and only version pairs on an explicit known-broken list fail session start — overridable with the `--force` flag (`Cs2VideoGenerator:ForceIncompatiblePlugin`) on `generate`/`watch`. The known-broken list ships as an embedded JSON resource in `Cs2VideoGenerator.Core` (starts empty, updatable via package update) and can be replaced via `Cs2VideoGenerator:CompatibilityOverridePath`. The proto fields are optional and backward-compatible: an older plugin connecting to a newer orchestrator is treated as "unknown version" (warning), never an error.
- The plugin-connect timeout error hints that a CS2 update / plugin version mismatch may be the cause.
- OBS Studio capture provider built on the [Obs.NET](https://www.nuget.org/packages/Obs.NET) obs-websocket v5 client (1.0.1-beta, which exposes the obs-websocket `RequestStatus` code/comment on `ObsRequestException`), with automatic OBS launch/shutdown (`AutoStart`), profile/scene-collection selection, and authentication support.
- OBS readiness gate. The obs-websocket server accepts connections before OBS finishes initializing, so the first capture request could fail with `OBS is not ready to perform the request` (RequestStatus 207) when CSVG launched OBS itself. After connecting, CSVG probes readiness with a lightweight `GetVersion` request and, on a 207 response, retries until OBS is ready or a timeout elapses; configurable under `Capture:OBS` via `ReadinessRetryDelayMs` (default 5000) and `ReadinessTimeoutMs` (default 30000).
- Audio capture toggle: `captureAudio` compilation setting (default `true`, per-clip overridable). When disabled, CSVG mutes the OBS audio inputs for the duration of each clip via obs-websocket and restores each input's prior mute state afterwards (runtime-only; the OBS profile is never permanently modified, and restore runs even after a failed capture).
- `gameinfo.gi`, `game/core/cfg`, and Steam userdata settings are backed up before every launch and restored when the session ends.
- Mock CS2 mode (`--mock`) with bundled `mock_server` binaries (win-x64, linux-x64) for testing without a CS2 installation; other platforms can build the mock server locally and point `--mock-server-path` (`Cs2VideoGenerator:ExternalMockServerPath`) at it.
- OpenTelemetry instrumentation (traces, metrics, logs), configured via the `Telemetry` configuration section.
- Tag-triggered release automation: pushing a `vX.Y.Z` tag builds the native plugins (shared reusable workflow with CI), gates on native-asset completeness, runs the test suite, packs the NuGet packages and pushes them to this repo's GitHub Packages feed (using the built-in `GITHUB_TOKEN`, no nuget.org key), publishes self-contained CLI archives, and creates a GitHub Release with the nupkg/snupkg + CLI archives attached and the matching changelog section as its body. See `docs/RELEASING.md`.
- CLI distribution: the CLI is packaged as a dotnet tool (`dotnet tool install -g Cs2VideoGenerator.Cli --add-source <GitHub Packages feed>` installs the `csvg` command) and as self-contained archives (`csvg-<version>-win-x64.zip`, `csvg-<version>-linux-x64.tar.gz`) attached to each GitHub Release that run without a .NET runtime, with the native plugin assets bundled. The CLI loads its bundled `appsettings.json` relative to the binary instead of the working directory, so `csvg` works from any directory when installed as a tool or unpacked from an archive; an `appsettings.json` in the current working directory (when different) overrides the bundled defaults.
- Capability-negotiated demo-control protocol: the plugin advertises a set of capability tokens when it connects, and the orchestrator queries them via `ICsvgClient.Supports(capability)` and adapts. Every new protocol field below is optional and backward-compatible: an older plugin that lacks a capability makes the orchestrator degrade gracefully (a single warning, never an error; the same warn-not-block philosophy as the plugin/game version handshake), and an older plugin and a newer orchestrator (or vice versa) interoperate.
- CSVG can read the engine's *actual* demo state (playing/paused, current tick, loaded demo path) instead of inferring it from the commands it issued. `ICsvgClient.QueryDemoStateAsync()` fetches a snapshot; `LastDemoState`, `LastTickIsPaused`, and the `DemoStateChanged` event surface change-driven updates. Pause status comes from engine truth (`IDemoFile::IsDemoPaused`) and each change is tagged with its origin (a CSVG command vs. a user action in-game). Requires the plugin's `demo-state-events`, `engine-pause-detection`, and `demo-identity` capabilities.
- Arrival-verified seeks: `SetDemoTickAsync` can wait for the seek to actually complete and returns a `SeekResult` carrying the tick the engine was *observed* at on arrival, rather than the pre-echoed target tick; an optional `pauseAfterSeek` enforces and reports the post-seek play/pause state. Backed by correlation-id command acknowledgements (`command-ack` / `seek-ack` capabilities).
- `SetDemoTimescaleAsync(timescale)` sets `demo_timescale`, so a demo can be played (and captured) at reduced or increased speed with real rendered frames (`timescale-set` capability).
- `LoadDemoAsync(demoPath, interactiveDemoUi: true)` leaves CS2's in-game demo playback UI available to the user for the session instead of hiding it as the automated pipeline normally does (`user-demo-ui` capability).
- Experimental spectate-by-SteamID64: `SetSpectatorTargetCommand` can target a player by SteamID64 (`spectate-by-steamid` capability). Opt-in and off by default, it attempts `spec_lock_to_accountid` and falls back to name-based `spec_player`. Known-unreliable on some CS2 builds, so name-based spectating remains the default — see [Known Limitations](README.md#known-limitations).
- The bundled mock server models engine-truth demo state and exposes a user-action injection channel (`IMockUserActionInjector`), so library consumers can simulate a user pausing or seeking in the demo UI and assert the orchestrator's engine-truth handling. No CS2 install needed.
- macOS (Apple Silicon) mock server bundled, so `--mock` mode works out of the box on `osx-arm64`: the mock server ships in the package at `runtimes/osx-arm64/native/` (previously macOS users had to build it themselves and point `--mock-server-path` at it). Because CS2 does not run on macOS, this binary is built locally and committed rather than produced on an expensive macOS CI runner; see `docs/RELEASING.md`.

### Fixed

- Library consumers get fast, actionable failures instead of silent timeouts: `StartSessionAsync` probes the configured gRPC port (`Cs2VideoGenerator:GrpcPort`, default 50051) before launching CS2 and explains the required `AddGrpc()`/`MapGrpcService<Cs2GameService>()` wiring; mock-mode and capture-provider selection now honor code-configured options instead of only raw configuration keys.

[1.1.2]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v1.0.2...v1.1.1
[1.1.0]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v1.0.0...v1.0.2
[1.0.1]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/CS2OpenDev/CS2VideoGenerator/releases/tag/v1.0.0
