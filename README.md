# CS2 Video Generator

Automated Counter-Strike 2 video generation from demo files. CS2 Video Generator (CSVG) drives demo playback and video capture to produce clips and highlight compilations.

## Overview

A C++ plugin loaded by the game talks directly to CS2's engine through HL2SDK. On the .NET side, a Core library manages the CS2 process lifecycle, plugin communication and video capture, and a CLI orchestrates the video generation workflow on top of it. OBS Studio recording is driven over obs-websocket by [Obs.NET](https://www.nuget.org/packages/Obs.NET).

The .NET orchestrator and the plugin talk to each other in real time over a gRPC bidirectional stream. That is what makes precise control over demo playback and synchronized video capture possible.

CSVG is a **pure capture executor**: you supply a compilation definition describing *what* to capture (demos, tick ranges, players), and CSVG owns *how* it is captured. Selecting clips (e.g., finding kills or highlights in a demo) is out of scope and is expected to be done by upstream tooling that emits the compilation JSON. See the [Roadmap](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/docs/ROADMAP.md) for planned features.

## Requirements

- .NET 10.0 SDK
- Counter-Strike 2
- OBS Studio 28 or newer, only for the `OBS` capture provider (obs-websocket v5 is bundled with OBS 28+; see [OBS Studio Setup](#obs-studio-setup))
- FFmpeg (for clip concatenation)
- Windows or Linux

## Important: How CSVG Modifies Your CS2 Installation

To control demo playback, CSVG temporarily modifies your CS2 installation for the duration of each session:

- **Modifies `gameinfo.gi`.** The CSVG plugin directory is added to CS2's search paths so the engine loads the plugin.
- **Installs an engine plugin.** The CSVG plugin binary (`server.dll` / `server.so`) is copied into a `csvg` directory inside the CS2 install, and the `game/core/cfg` directory is modified.
- **Launches CS2 with `-insecure`** — the game is started with `-insecure -novid -allow_third_party_software`, which disables VAC for that game session.

**Safety mechanism:** Before every launch, CSVG backs up each file it is about to modify (`gameinfo.gi`, the `game/core/cfg` directory, and your user settings under Steam `userdata`) to a central backup directory. When the session ends, the plugin files are removed and the backups are restored automatically. You can also manage backups manually with the [`backup`](#backup---backup-game-files), [`restore`](#restore---restore-game-files), and [`cleanup`](#cleanup---clean-up-backups) commands, and inspect state with `doctor`.

**Warning:** Do not join VAC-secured servers from a modified installation. If CSVG (or CS2) crashed and the automatic restore did not run, run `csvg restore` to put your installation back to its original state before playing online. When in doubt, verify game file integrity through Steam.

## Installation

CSVG is distributed from this repository. Its NuGet packages go to this repo's GitHub
Packages feed, not nuget.org, and every release also attaches ready-to-run archives to its
GitHub Release.

### Self-contained archive (recommended, no .NET runtime required)

Download `csvg-<version>-win-x64.zip` or `csvg-<version>-linux-x64.tar.gz` from the
[GitHub Releases page](https://github.com/CS2OpenDev/CS2VideoGenerator/releases), extract it, and run the bundled `csvg` executable. No authentication or .NET install required.

### As a dotnet tool (from the GitHub Packages feed)

The tool package lives in this repo's **private** GitHub Packages feed, so installing it
requires a GitHub Personal Access Token (classic) with the `read:packages` scope
(GitHub Packages requires auth even for reads):

```bash
dotnet tool install -g Cs2VideoGenerator.Cli \
  --add-source "https://nuget.pkg.github.com/CS2OpenDev/index.json"
# When prompted (or via a configured source) authenticate with your GitHub username
# and a PAT that has read:packages. See docs/RELEASING.md for adding the source.
csvg <command> [options]
```

### From source

```bash
# Clone the repository
git clone https://github.com/CS2OpenDev/CS2VideoGenerator.git
cd Cs2VideoGenerator

# Build the solution
dotnet build

# Run the CLI
dotnet run --project src/Cs2VideoGenerator.Cli -- <command> [options]
```

## CLI Commands

### `doctor` - Diagnose Configuration

Validates the configuration and checks that the dependencies are actually there.

```bash
csvg doctor              # Run diagnostics
csvg doctor --verbose    # Show detailed configuration values
csvg doctor --no-cache   # Force fresh path discovery
```

Checks performed:
- Steam installation and logged-in users
- CS2 installation and executable paths, including the installed CS2 game build (read from `game/csgo/steam.inf`)
- OBS Studio installation
- Plugin configuration: the CSVG package version, and the bundled plugin's presence and SHA-256 content hash
- Backup directory status

See [CS2 Updates and Plugin Compatibility](#cs2-updates-and-plugin-compatibility) for how the reported versions are used at session start.

### `generate` - Generate Video Clips

```bash
csvg generate --file compilation.json
csvg generate --compilation '{"settings": {...}, "clips": [...]}'
csvg generate --file compilation.json --mock=true
```

Options:
- `-f, --file` - Path to a JSON file containing the compilation definition
- `-c, --compilation` - JSON string containing the compilation definition (alternative to `--file`; provide exactly one of the two)

See [Compilation JSON Reference](#compilation-json-reference) for the definition format.

When `concatenateClips` is true (default), FFmpeg combines all captured clips into a single video file: losslessly stream-copied by default, or re-encoded when the optional [`settings.encoding`](#settingsencoding-fields) block is provided. The output file is named `{baseFileName}.{containerFormat}` (e.g., `highlights.mp4`). Individual clip files are named `{baseFileName}_000.mp4`, `{baseFileName}_001.mp4`, etc.

### `watch` - Play Demo with Recording

Launch CS2 and play a demo file. Video capture is initialized by default and the playback is recorded at **30 fps**; `--fps 60` gives the smoothest result, see below. The recording is saved to the configured output directory (`Cs2VideoGenerator:OutputDirectory`, out of the box `captures/` under the working directory; `--output-dir` overrides) under a descriptive name carrying the capture provider, resolution, fps, capture settings and a timestamp, and its path is printed when playback completes. `--output-name` sets an exact name instead. Pass `--no-capture` to just watch the demo without recording.

```bash
csvg watch --demo match.dem
csvg watch --demo match.dem --start-tick 5000 --end-tick 15000
csvg watch --demo match.dem --fps 60                       # smoothest
csvg watch --demo match.dem --speed 0.5 --normalize        # 2x slow-mo + a real-time copy
csvg watch --demo match.dem --width 3840 --height 2160 --fullscreen
csvg watch --demo match.dem --no-capture
```

Options:
- `-d, --demo` - Path to the .dem file (required)
- `-s, --start-tick` - Demo tick to start playback from (default: 0)
- `-e, --end-tick` - Demo tick to stop playback at (default: 0 = play 10000 ticks from `--start-tick`; the plugin protocol has no play-to-end command)
- `-w, --width` - Game window width (default: 1920)
- `-h, --height` - Game window height (default: 1080)
- `-f, --fullscreen` - Launch in fullscreen mode
- `--fps` - Output frame rate (default: 30, or `Cs2VideoGenerator:DefaultCaptureFrameRate`). For the offline in-engine providers every output frame is a real rendered frame, so a lower rate renders fewer frames and captures faster; pass `--fps 60` for the smoothest result. The library and compilation-JSON defaults stay 60.
- `--speed` - Playback speed: `1` real time (default), `0.5` = 2x slow motion, `2` = 2x fast-forward. Offline providers only; every slow-motion frame is really rendered (not interpolated).
- `--normalize` - With `--speed`, also emit a real-time copy of the clip (lossless retime, original audio) next to the speed clip
- `--output-dir` - Directory to write into (default: `Cs2VideoGenerator:OutputDirectory`, out of the box `captures/`)
- `--output-name` - Exact output file name without extension (default: the descriptive auto name)
- `--no-capture` - Disable video capture (watch only, no recording)

### `backup` - Backup Game Files

Copies CS2 game files and user settings to the central backup directory. Sessions do this for you; the command is for doing it by hand.

```bash
csvg backup                 # Backup game files and user settings
csvg backup --dry-run       # Preview without backing up
csvg backup --game-files    # Only backup game files (gameinfo.gi, etc.)
csvg backup --user-settings # Only backup user settings (cfg directory)
```

### `restore` - Restore Game Files

With no arguments, `restore` lists the available backups and opens an **interactive multi-select prompt** to choose what to restore, followed by a confirmation prompt.

```bash
csvg restore                        # Interactively select backups to restore
csvg restore --list                 # List available backups without restoring
csvg restore --all                  # Restore all available backups
csvg restore gameinfo.gi_abc123     # Restore a specific backup by name
csvg restore --adjacent             # Restore from adjacent .backup files instead of the central directory
csvg restore --all --dry-run        # Preview what a full restore would do
```

Options:
- `--list` - List available backups (name, original path, backup time, status) and exit
- `--all` - Restore all available backups
- `--adjacent` - Restore from adjacent `.backup` files next to the originals instead of the central backup directory
- `--dry-run` - Show what would be restored without restoring. Note: in interactive mode the selection prompt still runs first; combine with `--all` (or a backup name) for a fully non-interactive preview
- `<backup-name>` - Positional argument: restore a single specific backup from the central backup directory

### `cleanup` - Clean Up Backups

Delete backups from the central backup directory. With no arguments, `cleanup` lists all backups and opens an **interactive multi-select prompt** to choose which to delete, followed by a confirmation prompt (defaulting to No).

```bash
csvg cleanup               # Interactively select backups to delete
csvg cleanup --list        # List all backups without deleting
csvg cleanup --all         # Delete all backups
csvg cleanup --max-age 7d  # Delete backups older than 7 days (supports h, d, w)
csvg cleanup --all --dry-run  # Preview what would be deleted
```

Options:
- `--list` - List all backups and exit
- `--all` - Delete all backups
- `--max-age` - Delete backups older than the given duration (e.g. `24h`, `7d`, `2w`)
- `--dry-run` - Show what would be deleted without deleting. Note: in interactive mode the selection prompt still runs first; combine with `--all` or `--max-age` for a fully non-interactive preview

### Common Options (`generate`, `watch`)

- `--mock` - Enable mock mode for testing without CS2 (see [Mock Mode](#mock-mode))
- `--mock-server-path` - Override the bundled `mock_server` executable with an external path (implies `--mock`)
- `--plugin-path` - Override the bundled `server.dll`/`server.so` plugin with an external path (real CS2 mode only)
- `--grpc-port` - Port for the gRPC server the CS2 plugin connects to (default: 50051). Note: the bundled plugin and mock_server connect to port 50051 (fixed at plugin build time), so a non-default port requires a custom plugin build targeting the same port
- `--force` - Proceed even if the plugin/game version pair is on the known-incompatible list (see [CS2 Updates and Plugin Compatibility](#cs2-updates-and-plugin-compatibility))
- `--capture-provider` - Capture backend to record with: `InEngine` or `OBS` (default: `InEngine`; names are matched ignoring case). Same as the `Cs2VideoGenerator:VideoCaptureProvider` configuration key. See [Capture providers](#capture-providers)

## Compilation JSON Reference

A settings block plus the clips to capture. Property names are case-insensitive. A minimal valid example:

```json
{
  "settings": {
    "outputDirectory": "C:/Videos/CS2",
    "baseFileName": "highlights"
  },
  "clips": [
    {
      "playerSteamId": 76561198000000000,
      "matchChecksum": "9f2a1c3d",
      "demoFilePath": "C:/demos/match1.dem",
      "startTick": 15000,
      "endTick": 16500,
      "playerNameToSpectate": "PlayerName"
    }
  ]
}
```

### `settings` fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `outputDirectory` | string | **yes** | — | Directory where output files are saved |
| `baseFileName` | string | **yes** | — | Base filename for outputs (`highlight` → `highlight_000.mp4`, `highlight_001.mp4`, and `highlight.mp4` for the concatenated file) |
| `containerFormat` | string | no | `"mp4"` | Video container format (e.g. `mp4`, `mkv`) |
| `frameRate` | int | no | `60` | Frame rate for the output video |
| `width` | int? | no | `null` | Output width in pixels; when omitted, the capture default is used |
| `height` | int? | no | `null` | Output height in pixels; when omitted, the capture default is used |
| `fullscreen` | bool | no | `false` | Launch CS2 in fullscreen mode instead of windowed |
| `captureAudio` | bool | no | `true` | Capture audio in the recordings. When `false`, CSVG mutes the OBS audio inputs for the duration of each clip and restores each input's prior mute state afterwards (see [OBS Studio Setup](#obs-studio-setup)) |
| `concatenateClips` | bool | no | `true` | Concatenate all clips into a single output file with FFmpeg after capture |
| `deleteClipsAfterConcat` | bool | no | `false` | Delete individual clip files after successful concatenation (only applies when `concatenateClips` is true) |
| `encoding` | object | no | `null` | FFmpeg encode controls for the final concatenated file; see [`settings.encoding` fields](#settingsencoding-fields). When omitted, clips are concatenated with lossless stream copy |

### `settings.encoding` fields

> **Note:** these settings govern **only the final concatenated output file**. Per-clip recording quality (encoder, bitrate, format of the individual clips) is governed by your OBS profile, see [OBS Studio Setup](#obs-studio-setup). When the `encoding` block is omitted (or all fields are null), the concatenated file is produced with lossless stream copy (`-c:v copy -c:a copy`), which is fast and preserves the OBS output exactly. Setting any video field re-encodes the video stream; setting any audio field re-encodes the audio stream; a stream without settings remains stream-copied.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `videoCodec` | string | no | `null` | FFmpeg video encoder for the concatenated output (e.g. `libx264`, `libx265`), emitted as `-c:v <codec>` |
| `crf` | int | no | `null` | Constant rate factor for quality-based encoding (0–51 for x264/x265, lower = higher quality), emitted as `-crf <value>`. Mutually exclusive with `videoBitrate` |
| `videoBitrate` | string | no | `null` | Target video bitrate in FFmpeg notation (e.g. `8M`, `4000k`), emitted as `-b:v <value>`. Mutually exclusive with `crf` |
| `audioCodec` | string | no | `null` | FFmpeg audio encoder (e.g. `aac`, `libopus`), emitted as `-c:a <codec>` |
| `audioBitrate` | string | no | `null` | Target audio bitrate in FFmpeg notation (e.g. `192k`), emitted as `-b:a <value>` |
| `extraInputArgs` | string | no | `null` | **Power-user escape hatch.** Raw FFmpeg arguments inserted before the concat input (e.g. `-hwaccel auto`), passed as-is with no validation or escaping. Does not by itself trigger re-encoding |
| `extraOutputArgs` | string | no | `null` | **Power-user escape hatch.** Raw FFmpeg arguments appended to the output options (e.g. `-movflags +faststart`), passed as-is with no validation or escaping. Does not by itself trigger re-encoding |

Example: re-encode the final file with x264 at CRF 23, leaving the OBS-recorded audio untouched.

```json
"settings": {
  "outputDirectory": "C:/Videos/CS2",
  "baseFileName": "highlights",
  "encoding": {
    "videoCodec": "libx264",
    "crf": 23,
    "extraOutputArgs": "-movflags +faststart"
  }
}
```

Re-encoding runs at encode speed rather than disk speed; for long compilations with slow encoders, raise `Ffmpeg:ConcatTimeout` (default 30 minutes) in the configuration.

### `clips[]` fields

Clips are processed sequentially, in order.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `playerSteamId` | long | **yes** | — | SteamID64 of the player featured in this clip (metadata; see [Known Limitations](#known-limitations)) |
| `matchChecksum` | string | **yes** | — | Checksum identifying the match this clip is from (metadata) |
| `demoFilePath` | string | **yes** | — | Path to the `.dem` file |
| `startTick` | int | **yes** | — | Demo tick the clip starts at (must be >= 0) |
| `endTick` | int | **yes** | — | Demo tick the clip ends at (must be greater than `startTick`) |
| `playerNameToSpectate` | string | **yes** | — | Display name of the player to spectate; must match the name in the demo (see [Known Limitations](#known-limitations)) |
| `clipOptions` | string (flags) | no | `"Default"` | Demo playback display options; see below |
| `captureAudio` | bool? | no | `null` | Per-clip override of `settings.captureAudio`; when omitted, the settings-level value applies |
| `playbackSpeed` | double | no | `1.0` | Playback speed of this clip: `1` real time, `0.5` = 2x slow motion, `2` = 2x fast-forward. Offline in-engine providers only (every slow-motion frame is really rendered); the realtime OBS provider ignores it. Must be > 0 |
| `produceNormalizedCopy` | bool | no | `false` | With `playbackSpeed` != 1, also emit a lossless real-time copy of this clip next to it (retimed by stream copy, original game-time audio untouched). A side artifact — the concatenated reel uses the speed clip. Reported on `CaptureResult.AdditionalOutputs` |

### `clipOptions` flags

`clipOptions` is a flags enum serialized as a string. Combine individual flags with commas (e.g. `"XrayEnabled, HudEnabled"`), or use one of the presets.

| Flag | Meaning |
|------|---------|
| `None` | No options enabled |
| `XrayEnabled` | Show X-ray (see players through walls) |
| `TrueViewEnabled` | Use the player's true first-person view |
| `HudEnabled` | Show the in-game HUD |
| `ShowAssistsInFeed` | Show assists in the kill feed |
| `ShowOnlyDeathNotices` | Hide HUD elements other than death notices |
| `Default` | `XrayEnabled, TrueViewEnabled, HudEnabled, ShowAssistsInFeed` |
| `NoHudDefault` | `Default` with the HUD disabled and only death notices shown (`XrayEnabled, TrueViewEnabled, ShowAssistsInFeed, ShowOnlyDeathNotices`) |

## Architecture

```
CLI (Cs2VideoGenerator.Cli)
    |
    +--- CsvgVideoSession (composition entry point)
    |        |
    |        +--- Cs2EngineSession  <-- one per CS2 run
    |        |        |
    |        |        +--- gRPC bidirectional stream <--> Cs2GameService
    |        |                                                 |
    |        |                                       CS2 Plugin (C++ gRPC client)
    |        |                                                 |
    |        |                                       HL2SDK engine interface
    |        |
    |        +--- ICaptureSession   <-- one per CS2 run, from the selected
    |        |        |                 ICaptureSessionFactory ("OBS", ...)
    |        |        +--- IClipCapture  <-- one per clip
    |        |
    |        +--- Cs2CompilationRunner (whole-compilation pipeline)
    |                 |
    |                 +--- ClipCaptureCoordinator (per-clip capture recipe)
    |
    +--- BackupManager / PluginInstaller
```

Object lifetimes are the point of the shape: the singletons (`ICs2EngineHost`,
`ICaptureSessionFactory`, `ClipCaptureCoordinator`, `Cs2CompilationRunner`) hold no
per-run state, one engine session and one capture session exist per CS2 run, and one clip
capture exists per clip. Nothing from a finished run can be reused by the next one.

### Core Components

**CsvgVideoSession** - the everyday entry point. Composition only: it wires the object
graph and owns nothing but the current session objects, the `CsvgSessionState` machine, the
events, and the ordering (capture up before CS2 launches, capture down before CS2 is
killed).
- `StartAsync` / `StartWatchAsync` / `StopAsync` / `WaitForEndAsync`
- `CaptureClipAsync(ClipCaptureContext)` for a single clip, `RunCompilationAsync` for a batch
- `PlayTickRangeAsync` to watch a range without recording
- `LoadDemoAsync`, which also tells the capture backend a demo is open
- `Engine` — the `ICs2EngineSession` for the current run, where demo control lives

**Cs2EngineSession** (`ICs2EngineSession`, from `ICs2EngineHost`) - one CS2 run:
- The plugin command channel, acknowledged commands, and the latched engine truth
  (last tick, demo state, timescale, plugin capabilities)
- All demo control: load/close, play/pause, seeks, spectator target, demo options,
  tick-scheduled commands, game-event subscription
- `PlayTickRangeAsync` — pure playback, no capture coordination

**ICaptureSessionFactory / ICaptureSession / IClipCapture** - the capture contract, one
implementation set per backend. Providers see only `IEngineConsole` (capabilities, observed
ticks, console execution): scheduling and playback stay with the coordinator.

**Cs2GameService** - the gRPC service on the .NET side of the plugin connection: bidirectional
streaming for commands and events, demo playback status tracking, tick-accurate position
reporting.

**Cs2CompilationRunner** - the whole-compilation pipeline:
- Groups clips by demo file so each demo is loaded once, and plays a group's clips in tick order
- Numbers output files by each clip's original position in the compilation
- Stops at the first failed clip, then concatenates the successful ones through `IFfmpegService`

**ClipCaptureCoordinator** - the single per-clip capture recipe:
- Arms a backend's tick-scheduled command brackets before playback starts
- Drives playback and the clip's start/stop/finalize hooks
- Turns any capture failure into a failed result rather than an exception

**ObsCaptureSessionFactory / ObsCaptureSession / ObsClipCapture** - the OBS Studio integration:
recording control over the WebSocket, profile and scene collection management, and automatic
process management.

### gRPC Protocol

The plugin communicates via protocol buffers defined in `protos/cs2-video-generator.proto`:

**Commands (CLI to Plugin):**
- `LoadDemoCommand` - Load a demo file
- `ResumeDemoCommand` / `PauseDemoCommand` - Control playback
- `SetDemoTickCommand` - Seek to specific tick
- `PlayDemoTickRangeCommand` - Play between tick ranges
- `SetSpectatorTargetCommand` - Set camera target player
- `SetDemoOptionsCommand` - Configure xray, HUD, etc.

**Events (Plugin to CLI):**
- `TickUpdateEvent` - Current playback position
- `DemoPlaybackStatusChange` - Loading, playing, paused, etc.
- `Cs2ProcessStatus` - Plugin initialization state

## Configuration

Configuration is loaded from `appsettings.json` and can be overridden via command-line arguments:

```json
{
  "Cs2VideoGenerator": {
    "MockMode": false,
    "ExternalMockServerPath": null,
    "ExternalPluginPath": null,
    "SteamInstallationDirectory": null,
    "Cs2RootInstallationDirectory": null,
    "VideoCaptureProvider": "OBS",
    "GrpcPort": 50051,
    "ForceIncompatiblePlugin": false
  },
  "Capture": {
    "OBS": {
      "Host": "localhost",
      "Port": 4455,
      "Password": null,
      "ProfileName": null,
      "SceneCollectionName": null,
      "AutoStart": true,
      "StartMinimized": true,
      "MaxRetryAttempts": 3
    }
  },
  "Ffmpeg": {
    "BinaryDirectory": null,
    "ConcatTimeout": "00:30:00"
  }
}
```

### Configuration Options

| Option | Description |
|--------|-------------|
| `MockMode` | Enable mock mode for testing without CS2 installed |
| `ExternalMockServerPath` | Override bundled mock_server executable (mock mode) |
| `ExternalPluginPath` | Override bundled server.dll/so plugin (real mode) |
| `SteamInstallationDirectory` | Override auto-discovered Steam path |
| `Cs2RootInstallationDirectory` | Override auto-discovered CS2 path |
| `VideoCaptureProvider` | Video capture backend (`OBS` or `InEngine`), matched ignoring case; its own settings live in `Capture:<name>` (same as `--capture-provider`). See [Capture providers](#capture-providers) |
| `ForceIncompatiblePlugin` | Proceed even if the plugin/game version pair is on the known-incompatible list (same as `--force`) |
| `CompatibilityOverridePath` | Path to a JSON file replacing the embedded plugin compatibility data (see [CS2 Updates and Plugin Compatibility](#cs2-updates-and-plugin-compatibility)) |

| `Capture:OBS` option | Description |
|----------------------|-------------|
| `Host` / `Port` | obs-websocket server address (default `localhost:4455`) |
| `Password` | obs-websocket password; required if authentication is enabled in OBS |
| `ProfileName` | OBS profile to switch to before capture (optional) |
| `SceneCollectionName` | OBS scene collection to switch to before capture (optional) |
| `AutoStart` | When `true` (default), CSVG launches OBS automatically if it is not already running, and shuts it down again when the application exits |
| `StartMinimized` | Start the auto-launched OBS minimized (default `true`) |
| `MaxRetryAttempts` | Maximum connection retry attempts (default `3`) |

`Capture:InEngine` is documented in [In-engine capture](docs/in-engine-capture.md); it is omitted from the example above because its defaults work without configuration.

Most paths are auto-discovered from standard Steam/OBS/FFmpeg installation locations. FFmpeg is expected to be in the system PATH unless `Ffmpeg:BinaryDirectory` is specified.

`Ffmpeg:ConcatTimeout` (default 30 minutes) bounds the final clip concatenation: if FFmpeg hangs (e.g. on a corrupt clip), the process is killed, the partial output is deleted, and the individual clips are preserved. Set it to `00:00:00` to disable the timeout.

### Capture providers

CSVG records through one of two backends, selected with `--capture-provider` or
`Cs2VideoGenerator:VideoCaptureProvider`. Each has its own settings under `Capture:<name>`.

| Provider | How it records | Timing | Frame rate | Status |
|---|---|---|---|---|
| `OBS` | OBS Studio screen-records the CS2 window over obs-websocket | Wall clock — the output is as long as playback took | From the OBS profile; `frameRate` is advisory | Supported |
| `InEngine` (default) | CS2 renders offline via `startmovie` + `host_framerate`; frames stream into FFmpeg | Tick-exact — the output length is frames divided by frame rate | Honored, per clip, from `frameRate` | **Validated on build 2000879** — frames, audio and both formats observed end to end; cvar restore values still assumed |

`InEngine` is the default as of 1.1.1: frame-exact, lossless (Targa) output with no dropped frames,
verified end to end against a real CS2 install (build 2000879) including game-time wav audio. It
needs no OBS installation, only FFmpeg and fast local storage. [In-engine capture](docs/in-engine-capture.md)
covers how it works and which facts remain open. Select `OBS` when you want the recording to go
through your OBS profile instead.

### OBS Studio Setup

CSVG controls OBS through the **obs-websocket v5** protocol, which ships bundled with OBS Studio 28 and newer (older OBS versions require installing the obs-websocket 5.x plugin separately).

To enable the WebSocket server in OBS:

1. Open OBS Studio and go to **Tools → WebSocket Server Settings**.
2. Check **Enable WebSocket server**. The default port is `4455`.
3. If **Enable Authentication** is checked, click **Show Connect Info** and copy the server password into the `Capture:OBS:Password` configuration key.

With `Capture:OBS:AutoStart` enabled (the default), you do not need to start OBS yourself: CSVG discovers the OBS executable, launches it (minimized when `StartMinimized` is true), connects, and shuts it down when finished. If OBS is already running, CSVG connects to the existing instance and leaves it running afterwards.

Recording quality (encoder, bitrate, per-clip format) is governed by your OBS profile settings. The final concatenated compilation file can optionally be re-encoded independently via the compilation JSON's [`settings.encoding`](#settingsencoding-fields) block.

When `captureAudio` is `false` in a compilation, CSVG disables audio by muting the OBS audio inputs (desktop audio, mic/aux, and audio-producing sources such as the game capture) over obs-websocket for the duration of the clip. Muting is runtime-only: each input's prior mute state is recorded and restored when the clip finishes (or when the session shuts down, even after a failed capture). Inputs you had muted yourself stay muted, and your OBS profile is never permanently modified.

### Mock Mode

For testing without CS2 installed:

```bash
csvg watch --demo match.dem --mock=true
csvg generate --file compilation.json --mock=true
```

The mock server is bundled for `win-x64`, `linux-x64`, and `osx-arm64` (Apple Silicon), so mock mode works out of the box on those platforms with no CS2 install. On other platforms, build the mock server locally and point `--mock-server-path` (`Cs2VideoGenerator:ExternalMockServerPath`) at it.

See [Mock CS2 Mode](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/docs/mock-cs2-mode.md) for details.

## Using Core as a Library

`Cs2VideoGenerator.Core` can be consumed as a NuGet package without the CLI, either from this repo's GitHub Packages feed (see [Installation](#installation) for the feed source + `read:packages` PAT) or by referencing the `.nupkg` attached to a [GitHub Release](https://github.com/CS2OpenDev/CS2VideoGenerator/releases) from a local source.

One piece of wiring is mandatory. The CS2 plugin (and the mock server) connect **back** to a gRPC server hosted by *your* process, so your host must map `Cs2GameService` on an HTTP/2 endpoint at the configured port (`Cs2VideoGenerator:GrpcPort`, default `50051`). A plain console host without this wiring cannot run sessions: `StartAsync` detects the missing endpoint before launching CS2 and fails fast with an explanatory `InvalidOperationException`.

Coming from the 1.x `ICsvgClient` API, start with [Migrating to 2.0](docs/migrating-to-2.0.md) — the break is hard and there is no compatibility shim.

Minimal working host:

```csharp
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Grpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddCs2VideoGeneratorCore();

// Host the gRPC endpoint the CS2 plugin / mock server connects to (required)
builder.WebHost.ConfigureKestrel(o =>
    o.ListenLocalhost(50051, l => l.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc();

WebApplication app = builder.Build();
app.MapGrpcService<Cs2GameService>();
await app.StartAsync();

CsvgVideoSession session = app.Services.GetRequiredService<CsvgVideoSession>();
await session.StartAsync();

await session.LoadDemoAsync(@"C:\demos\match.dem");
await session.CaptureClipAsync(new ClipCaptureContext
{
    StartTick = 12000,
    EndTick = 12600,
    SpectatorTarget = "s1mple",
    Metadata = new CaptureMetadata { OutputDirectory = @"C:\clips", OutputFileName = "ace" }
});
// ... or session.RunCompilationAsync(compilation) for a batch

await session.StopAsync();
await app.StopAsync();
```

Without a DI container, `CsvgVideoSessionBuilder` composes the same graph — and exposes the
engine host it built, which the `Cs2GameService` you map must be constructed over:

```csharp
CsvgVideoSession session = new CsvgVideoSessionBuilder()
    .WithLoggerFactory(loggerFactory)
    .WithOptions(new Cs2VideoGeneratorOptions { MockMode = true })
    .WithProcessManager(processManager)
    .Build();

var grpcService = new Cs2GameService(loggerFactory, session.EngineHost);
```

Options can be supplied from configuration (the `Cs2VideoGenerator` section) or from code; both are honored, including mock mode:

```csharp
builder.Services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true);
```

### Capability-aware demo control

Beyond the core capture flow, `session.Engine` (`ICs2EngineSession`) exposes finer demo-control APIs, and which of them work depends on the connected plugin build. The plugin advertises a set of **capability tokens** when it connects; the orchestrator adapts rather than assuming. That is what keeps an older plugin and a newer library (and vice versa) interoperable, the same warn-not-block philosophy as the plugin/game version handshake.

Query capabilities via `PluginCapabilities` (an `IReadOnlySet<string>`) or `Supports(token)`, using the constants on `CsvgCapabilities`:

```csharp
if (session.Engine.Supports(CsvgCapabilities.TimescaleSet))
    await session.Engine.SetDemoTimescaleAsync(0.25f);   // quarter-speed slow-motion
```

Each capability-gated API documents whether it **degrades** or **throws** when the plugin lacks the capability, and the rule (spelled out on `CsvgCapabilities`) has two halves. Latched telemetry and work the orchestrator started on its own initiative degrade: read-only accessors stay `null`/empty, and optional work driven from configuration rather than from a call — the startup game-event subscription — is skipped with a warning. An operation a caller explicitly asked for that has no v1.0 equivalent throws `NotSupportedException` naming the capability. Where a legacy path does exist the call takes it instead of throwing.

- **Engine-truth demo state** — `QueryDemoStateAsync()` returns a `DemoState` snapshot read from the engine (playing/paused, current tick, loaded demo path, change origin), not inferred from issued commands. `LastDemoState`, `LastTickIsPaused`, and the `DemoStateChanged` event surface change-driven updates. `CsvgVideoSession.StartWatchAsync(...)` is the canonical live-sync bootstrap (playback-only, no capture backend, densest tick stream). Requires `demo-state-events` / `engine-pause-detection`; stays `null`/silent otherwise.
- **Arrival-verified seeks.** `SetDemoTickAsync(demoTick, pauseAfterSeek, waitForCompletion)` returns a `SeekResult`. With `waitForCompletion: true` it completes only when the plugin acknowledges the seek and reports the tick the engine was *actually observed* at on arrival (instead of the pre-echoed target); `pauseAfterSeek` forces and reports the post-seek play/pause state. Requires `command-ack` (throws `NotSupportedException` without it); the legacy fire-and-forget `SetDemoTickAsync(demoTick)` still works on every plugin.
- `SetDemoTimescaleAsync(timescale)` (finite, `0 < timescale ≤ 8.0`) sets `demo_timescale` for real-rendered slow-motion or speed-up. `LastKnownTimescale` tracks the last host-set value (no engine readback). Requires `timescale-set`.
- **Interactive demo UI** — `LoadDemoAsync(demoPath, interactiveDemoUi: true)` leaves CS2's in-game demo UI available so a user can pause/seek in-game (the prerequisite for a host that mirrors in-game actions). Requires `user-demo-ui`; older plugins ignore the flag and keep the UI hidden.
- **Tick-scheduled commands** — `ScheduleCommandsAtTickAsync(demoTick, commands)` registers console commands the plugin executes on CS2's engine thread the first time the observed demo tick crosses the target (a forward seek that skips past the target still fires it — but the crossing is half-open, so a schedule at or below the tick already observed is never reached, and backward movement fires nothing); `ClearScheduledCommandsAsync()` drops unfired schedules, and loading a demo clears them all. Requires `tick-scheduled-commands` (throws `NotSupportedException` without it).
- **Immediate console execution.** `ExecuteConsoleCommandsAsync(commands)` hands console commands to CS2's engine thread with no tick condition, for work that has no tick of its own or cannot wait for one that may never arrive (a capture backend's cvar setup before recording; a clip aborting mid-playback restoring them). Blind: the engine reports nothing back, so the returned task means the plugin queued the batch, not that CS2 accepted it. Requires `console-exec` (throws `NotSupportedException` without it).
- **Game-event telemetry (off by default)** — `SetGameEventSubscriptionAsync(true, eventNames)` makes the plugin forward client-side game events (kill feed, round transitions, …) as `GameEventReceived` events carrying the game's own KV3-as-JSON payload; nothing is hooked in CS2 until the first enable. Best-effort observability, not a control channel (bounded queue, drop-oldest). Requires `game-event-telemetry`, advertised only by builds with a game-event hook — currently the mock server and the Windows plugin (throws `NotSupportedException` elsewhere, including against the Linux plugin). CLI/config opt-in: `--game-events` / `Cs2VideoGenerator:EnableGameEventTelemetry`.

## Obs.NET

OBS control in CSVG is built on [Obs.NET](https://www.nuget.org/packages/Obs.NET), a standalone OBS WebSocket v5 client library for .NET maintained by this project's author. It was originally developed inside this solution and has since been extracted into its own public NuGet package. The API is async/await throughout, with request types covering scenes, sources, filters, recording and streaming, strongly-typed events for OBS state changes, SHA256 authentication, and automatic reconnection with configurable retry logic.

CSVG currently tracks a prerelease Obs.NET version (1.0.1-beta), so its API may still change between releases.

## Building

```bash
# Build entire solution
dotnet build

# Build in Release mode
dotnet build -c Release

# Run tests
dotnet test

# Run specific test project
dotnet test test/Cs2VideoGenerator.Core.Tests
```

### C++ Plugin (CMake)

```bash
# From plugin/Cs2VideoGenerator.Plugin.Grpc/
cmake --preset release-unix            # Linux/macOS (use `release` on Windows)
cmake --build --preset release-unix --parallel
```

See [plugin/Cs2VideoGenerator.Plugin.Grpc/README.md](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/plugin/Cs2VideoGenerator.Plugin.Grpc/README.md) for full native build and cross-compilation instructions, and [CONTRIBUTING.md](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/CONTRIBUTING.md) for contributor setup.

## Telemetry

OpenTelemetry instrumentation for tracing, metrics and logging. Configuration lives exclusively in the `Telemetry` section; there are no telemetry CLI flags.

```json
{
  "Telemetry": {
    "ServiceName": "Cs2VideoGenerator.Cli",
    "OpenObserveEndpoint": "http://127.0.0.1:4317",
    "OpenObserveAccessKey": null,
    "OpenObserveLogPath": null,
    "OpenObserveMetricsPath": null,
    "OpenObserveTracePath": null,
    "OtlpExportProtocol": "Grpc"
  }
}
```

## CS2 Updates and Plugin Compatibility

CS2 game updates usually do **not** break the CSVG plugin, so the compatibility check is deliberately
forward-compatible: it warns, it does not block.

At session start the plugin reports its own version, the HL2SDK commit it was built against, and the
running CS2 build number (read from `game/csgo/steam.inf`). CSVG compares these against reference data
and reacts as follows:

- **Match / known-good** - nothing is shown (debug log only).
- **Game build newer than (or different from) the plugin's build target, or any version unknown** - a
  single warning is logged ("plugin built for CS2 build X, game is Y - usually fine; if the session
  fails to connect or behaves oddly, check for a CSVG update") and the session proceeds normally.
  An older plugin that predates the version handshake is treated the same way.
- **Version pair on the known-broken list** - session start fails with an explanation, unless you pass
  `--force` (config: `Cs2VideoGenerator:ForceIncompatiblePlugin`) to try anyway.

The known-broken list ships as a small JSON resource inside `Cs2VideoGenerator.Core` (currently empty)
and is updated via package updates. You can replace it without updating the package by pointing
`Cs2VideoGenerator:CompatibilityOverridePath` at a JSON file of the form:

```json
{
  "builtForCs2Build": "14093",
  "knownBrokenVersions": [
    { "pluginVersionRange": "1.0.*", "gameBuild": "14200", "reason": "engine interface changed" }
  ]
}
```

`pluginVersionRange` is optional (`null` = all plugin versions) and supports an exact version or a
prefix wildcard (`"1.0.*"`). `csvg doctor` reports the CSVG package version, the bundled plugin's
presence/hash, and the installed CS2 game build, which is useful when reporting compatibility issues.

## Known Limitations

- **Spectating targets players by display name, not SteamID64.** Due to CS2 convar/command issues with SteamID-based spectator targeting, the camera is set with the `spec_player` console command using the player's name. `playerNameToSpectate` must exactly match the player's name as it appears in the demo; `playerSteamId` is carried as metadata and is not used for camera targeting by default. An **experimental**, opt-in SteamID64 path exists (the plugin's `spectate-by-steamid` capability attempts `spec_lock_to_accountid` and falls back to name-based targeting), but it is known-unreliable on some CS2 builds and off by default. See the [Roadmap](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/docs/ROADMAP.md) for status.
- **One continuous tick range per clip, one spectator target per clip.** Mid-clip camera switching is a planned feature; split into multiple clips for now.

## Roadmap

Planned and under-exploration features (HLAE capture backend, mid-clip camera switching, dropped-frame detection, and more) are documented in [docs/ROADMAP.md](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/docs/ROADMAP.md). Release history is tracked in [CHANGELOG.md](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/CHANGELOG.md).

## License

GPL-3.0-only. See [LICENSE](https://github.com/CS2OpenDev/CS2VideoGenerator/blob/main/LICENSE) for details.
