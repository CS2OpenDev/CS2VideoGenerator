# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.10.1] - 2026-10-30

### Changed

- **License:** the project is now MIT-licensed (previously GPL-3.0-only). Releases up to and
  including 0.10.0 remain available under GPL-3.0-only as published.
- Added `THIRD-PARTY-NOTICES.md`, covering the Valve SDK code compiled into the plugin, the gRPC
  dependency stack it statically links, and the NuGet dependencies the `csvg` tool bundles. Both
  packages now include it.
- README: added a quickstart, documented the `InEngineHooked` provider, `--game-events` and the
  bundled configuration defaults, and removed references to versions that were never released.

### Fixed

- `csvg doctor` with `VideoCaptureProvider` set to `InEngineHooked` reported a failed "Unknown
  provider" check. It now runs the in-engine checks for it, plus the Windows-only platform check and
  the plugin's `present-capture` requirement, and leaves out the frame-file throughput row.

### Removed

- **Plugin:** the diagnostic console mirror that copied every engine console line into
  `csvg_console.log` next to `cs2.exe`. It was left over from debugging and reopened the file once
  per line. `csvg.log` is unchanged.

## [0.10.0] - 2026-10-21

### Fixed

- **Capture:** `PresentHookFrameConsumer.DisposeAsync` now waits for the run loop, so
  disposal no longer returns while the encoder's stdin is still open.
- **Native Windows plugin build:** a fresh clone builds again with the `debug`/`release`
  presets. vcpkg ports build under `<repo>/.vcpkg-bt`, which keeps gRPC's generated
  sources under `MAX_PATH` for clone paths up to about 68 characters, and the vcpkg
  submodule moved to 2026.06.24 because MSYS2 removed the `msys2-runtime` package the old
  checkout asked for. Dependency versions are unchanged (`builtin-baseline` stays put).
- **Path privacy on native MSVC:** `/pathmap:` was silently ignored without
  `/experimental:deterministic`, so locally built `server.dll`/`mock_server.exe`
  embedded the clone's absolute path. The flag is now passed and the maps are ordered for
  MSVC's first-match rule; the plugin's own sources compile with `/Z7`, because
  deterministic mode also remaps `/Zi`'s PDB path. OpenSSL's nmake build also compiled
  vcpkg's install paths in as `OPENSSLDIR`/`ENGINESDIR`/`MODULESDIR`; the overlay port now
  uses OpenSSL's stock Windows defaults. Released packages were not affected: they ship the clang-cl
  cross-build.

### Changed

- CI builds the native MSVC preset (a separate `native-windows.yml`, on pushes to `main`,
  weekly and on demand) and runs the path check on it.
- The embedded-path check also looks for the checkout's own root, so a clone outside a
  home directory can no longer pass it.
- CI bounds `apt-get`, `llvm.sh` and the vcpkg baseline fetch so a wedged mirror can't
  hang a build, keys the vcpkg binary cache on the submodule commit, and prunes ABIs the
  build no longer uses before saving it.
- The triplets add `path-privacy.cmake` to vcpkg's ABI hash, so changing the path maps
  rebuilds the ports instead of reusing archives built with the old flags.

## [0.9.0] - 2026-08-19

Initial public release.

CSVG (Cs2VideoGenerator) is a pure capture executor for Counter-Strike 2 demos: you
supply a compilation definition (demos, tick ranges, players) describing *what* to
capture, and CSVG drives CS2 to record the clips and mux them into video. Selecting the
clips is out of scope and is expected to be done by upstream tooling that emits the
compilation JSON.

### Added

- **CLI (`csvg`)** — `generate` (capture clips from a compilation JSON, with optional
  FFmpeg concatenation into a single output), `watch` (launch CS2 and play a demo,
  recording by default at 30 fps — `--fps` to change, `--speed <x>`/`--normalize` for
  slow-motion and normalized copies), `doctor` (diagnose Steam/CS2/OBS discovery, plugin
  config, versions, and the bundled plugin's SHA-256), and `backup` / `restore` /
  `cleanup` for the game files CSVG modifies.
- **Core library (`Cs2VideoGenerator.Core`)** — the `CsvgVideoSession` orchestration API: CS2
  session lifecycle, demo control (load/play/pause/seek/tick ranges), spectator targeting,
  synchronized capture, and batch compilation capture.
- **Two capture backends** — an in-engine (`startmovie`) offline path where every output
  frame is a real rendered frame (the default), and an OBS Studio provider built on
  [Obs.NET](https://www.nuget.org/packages/Obs.NET) (obs-websocket v5) with automatic
  launch/shutdown, profile/scene-collection selection, a readiness gate, and a per-clip
  audio-capture toggle. An experimental, Windows-only variant of the in-engine path
  (`InEngineHooked`) takes frames from the plugin's present hook instead of image files.
- **Offline speed & normalization** — per-clip `playbackSpeed` (real slow-motion /
  fast-forward frames, e.g. 0.5 = 2× slow motion) and `produceNormalizedCopy` in the
  compilation JSON, plus `watch --speed <x> [--normalize]`. `Capture:InEngine:KeepEngineAudio`
  preserves the engine's pristine game-time wav alongside the output.
- **C++ engine plugin (`server.dll` / `server.so`)** — interfaces with CS2 via HL2SDK over
  gRPC bidirectional streaming; shipped as NuGet native assets under `runtimes/<rid>/native/`
  for win-x64 and linux-x64. A capability-negotiated, backward-compatible demo-control
  protocol and a warn-not-block plugin/game version handshake keep older/newer pairings
  interoperable.
- **Engine-truth demo state** — read the engine's actual play/pause state, current tick, and
  loaded demo (rather than inferring from issued commands); arrival-verified seeks that
  report the tick the engine was observed at (`SeekResult`); `demo_timescale` control for
  captured slow/fast playback; and an interactive demo-UI session mode.
- **Mock CS2 mode (`--mock`)** — bundled `mock_server` binaries (win-x64, linux-x64,
  osx-arm64) model engine-truth demo state and expose a user-action injection channel, so
  the full pipeline runs and is testable without a CS2 install.
- **Backups** — `gameinfo.gi`, `game/core/cfg`, and Steam userdata settings are backed up
  before every launch and restored when the session ends (restore runs even after a failed
  capture).
- **OpenTelemetry** instrumentation (traces, metrics, logs), configured via the `Telemetry`
  configuration section.
- **Distribution** — the CLI is packaged as a dotnet tool
  (`dotnet tool install -g Cs2VideoGenerator.Cli` installs the `csvg` command) and as
  self-contained archives (`csvg-<version>-win-x64.zip`, `csvg-<version>-linux-x64.tar.gz`)
  attached to each GitHub Release. Tag-triggered release automation builds the native
  plugins, gates on native-asset completeness, runs the test suite, packs the packages and
  publishes them to **NuGet.org via Trusted Publishing** (OIDC — no stored API key), and
  creates the GitHub Release. See `docs/RELEASING.md`.

[Unreleased]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v0.10.1...HEAD
[0.10.1]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/CS2OpenDev/CS2VideoGenerator/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/CS2OpenDev/CS2VideoGenerator/releases/tag/v0.9.0
