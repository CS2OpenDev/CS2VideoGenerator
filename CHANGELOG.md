# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.0] - 2026-08-18

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
- **Core library (`Cs2VideoGenerator.Core`)** — the `ICsvgClient` orchestration API: CS2
  session lifecycle, demo control (load/play/pause/seek/tick ranges), spectator targeting,
  synchronized capture, and batch compilation capture.
- **Two capture backends** — an in-engine (`startmovie`) offline path where every output
  frame is a real rendered frame (the default), and an OBS Studio provider built on
  [Obs.NET](https://www.nuget.org/packages/Obs.NET) (obs-websocket v5) with automatic
  launch/shutdown, profile/scene-collection selection, a readiness gate, and a per-clip
  audio-capture toggle.
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

[0.9.0]: https://github.com/CS2OpenDev/CS2VideoGenerator/releases/tag/v0.9.0
