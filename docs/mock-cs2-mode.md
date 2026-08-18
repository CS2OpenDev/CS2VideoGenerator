# Mock CS2 Mode

## Overview

With mock mode enabled, `MockCs2ProcessManager` stands in for the real `Cs2ProcessManager`. CS2 installation validation is bypassed, so there is no need to have CS2 installed. Plugin management is skipped entirely: no backup/restore operations, no gameinfo.gi modifications. The application runs the bundled or external mock_server instead of CS2, and it won't pass the CS2-specific arguments `-insecure -novid -allow_third_party_software`.

That covers integration tests in CI/CD, work on the plugin gRPC protocol, machines with no CS2 on them, and testing against a mock server of your own.

## Enabling Mock Mode

Three ways to turn it on: the `--mock=true` CLI flag, `MockMode: true` in configuration, or `--mock-server-path`, which implies mock mode.

### CLI Flags

```bash
# Enable mock mode (uses bundled mock_server)
csvg watch --demo match.dem --mock=true

# Enable mock mode with external mock server
csvg watch --demo match.dem --mock-server-path=/path/to/mock_server

# Generate in mock mode
csvg generate --file compilation.json --mock=true
```

### appsettings.json

```json
{
  "Cs2VideoGenerator": {
    "MockMode": true,
    "ExternalMockServerPath": "/path/to/your/mock_server",
    "VideoCaptureProvider": "OBS"
  }
}
```

### Environment Variables

```bash
export Cs2VideoGenerator__MockMode=true
export Cs2VideoGenerator__ExternalMockServerPath="/path/to/mock_server"
```

## Configuration Options

`MockMode` enables mock mode, using either the bundled or an external mock_server. `ExternalMockServerPath` overrides the bundled mock_server with an executable of your choosing. `ExternalPluginPath` is the same idea for the bundled server.dll/so plugin, and it only applies in real mode.

## Architecture

Mock mode selects the process manager implementation via dependency injection:

```
ICs2ProcessManager (interface)
        │
        ├─── Cs2ProcessManager (real CS2)
        │      - Validates CS2 installation
        │      - Manages plugin backup/install/restore
        │      - Passes CS2-specific launch arguments
        │
        └─── MockCs2ProcessManager (mock mode)
               - No validation required
               - No plugin management
               - Launches mock_server directly
```

Both process managers are registered — which one backs `ICs2ProcessManager` is decided when the service is first resolved, from the bound `Cs2VideoGeneratorOptions`, so mock mode configured in code (not just via configuration keys) is honored:

```csharp
services.AddSingleton<ICs2ProcessManager>(sp =>
{
    Cs2VideoGeneratorOptions options = sp.GetRequiredService<IOptions<Cs2VideoGeneratorOptions>>().Value;
    return options.IsMockModeEffective // MockMode, or ExternalMockServerPath set
        ? sp.GetRequiredService<MockCs2ProcessManager>()
        : sp.GetRequiredService<Cs2ProcessManager>();
});
```

So `services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true)` works the same as setting `Cs2VideoGenerator:MockMode` in configuration.

## Mock Server Discovery

`MockCs2ProcessManager` discovers the mock server in this order:

1. `ExternalMockServerPath` configuration (if provided)
2. Bundled mock_server from `runtimes/{rid}/native/`
3. Assembly directory fallback (development builds)

## Bundled Native Assets

The published NuGet package bundles mock_server executables for three RIDs:

```
runtimes/
  win-x64/native/mock_server.exe
  linux-x64/native/mock_server
  osx-arm64/native/mock_server
```

These are discovered automatically by `NativeAssetProvider`, based on the current runtime identifier.

On other platforms (e.g. linux-arm64), build the mock server locally, following the [plugin build guide](../plugin/Cs2VideoGenerator.Plugin.Grpc/docs/BUILD.md), then point `--mock-server-path` (or `Cs2VideoGenerator:ExternalMockServerPath`) at the resulting binary.

## Using a Custom Mock Server

A usable mock server must implement the CSVG gRPC protocol: connect back to the orchestrator's `Cs2GameService` stream and report `Ready`, respond to demo commands, and so on. A process that merely starts and sleeps will never make the session Ready. The reference implementation is the bundled `mock_server`, source in `plugin/Cs2VideoGenerator.Plugin.Grpc/src/mock/`. To run a locally built or custom binary, pass it via `--mock-server-path` (or `Cs2VideoGenerator:ExternalMockServerPath`).

## Mock Engine Behavior (v1.1)

The mock engine (`MockEngineInterface`, compiled into `mock_server`) simulates demo playback for the real, production `Cs2PluginClient` that mock_server links. The v1.1 capability release extends what it simulates.

### Playback speed (`demo_timescale`)

The engine parses `demo_timescale <v>`, sent by the host via `SetDemoTimescaleCommand` or injected (see below), and scales the tick thread's interval accordingly: interval = 1 s / (64 × timescale). A playback that is mid-run picks up the new rate immediately. Missing or non-positive values are ignored with a log line. The interval is clamped to [500 µs, 250 ms], so effective rates above ~31× or below ~1/16× flatten out. Fine for testing — not a simulation of extreme timescales.

### Natural demo end (`CSVG_MOCK_DEMO_TICKS`)

```bash
export CSVG_MOCK_DEMO_TICKS=6400   # demo ends naturally at tick 6400 (~100 s at 1x)
```

When set to a positive integer N, the mock demo naturally ends when the tick counter reaches N: tick advancement stops and `IsPlayingDemo()` flips to false, exactly as a real demo ending does, so the client's polling loop emits its stop-transition `DemoStateEvent`. Unset (or invalid) = previous behavior: ticks advance forever.

Notes:
- The limit applies to every demo loaded in that process (each `playdemo` restarts from tick 0 and ends at N again).
- Choose N comfortably larger than the load handshake needs; N ≥ 64 is safe. With a tiny N the demo can end while `LoadDemoCommand` is still polling for playback to start.
- After a natural end, the demo path remains readable via `GetDemoFilePath()` until the next `playdemo`/`stop`, which means you are reading the path of the demo that just ended. An explicit `stop` clears it.

### Spectator target (`spec_player`)

Production sends `spec_player "<display name>"`, a quoted display name rather than a slot number. The mock now parses the quoted-or-unquoted NAME and stores it as its current spectator target, readable via `MockEngineInterface::GetSpectatorTarget()` for tests. A purely numeric argument is still interpreted and logged as the legacy slot form. Previous versions did `atoi` on the argument and always logged slot 0 for named targets.

### Demo identity (`GetDemoFilePath`)

The mock overrides the engine's `GetDemoFilePath()` slot to return the stored `playdemo` path, empty string when no demo has been loaded. The plugin's demo-identity capability needs nothing further from the mock.

### Engine frame pump (`CSVG_MOCK_FRAME_HZ`)

The real plugin executes console commands from a `FrameStageNotify` hook on CS2's engine thread. mock_server stands in for that thread with a frame-pump thread that calls the plugin client's `OnEngineFrame()` at a fixed cadence, 250 Hz by default, overridable via `CSVG_MOCK_FRAME_HZ`. Setting it to `0` disables the pump entirely, which makes the client execute commands directly from the calling thread and read state from the polling thread.

That direct-execution leg is **mock-only**, and deliberately so. It is selected by a per-binary seam (`CsvgCommandsRequireEngineThread()`, defined in `mock_plugin.cpp` and `server_plugin.cpp`), not by runtime configuration. The real plugin returns `true` there and *always* queues: running `ExecuteClientCmd` off CS2's engine thread crashes the engine, so a command with no hook yet waits rather than taking a direct call. The mock returns `false`. It has no such hazard, and `CSVG_MOCK_FRAME_HZ=0` exists to exercise the polling-thread state-read path that a real plugin also uses before its hook installs.

## User-Action Injection Channel

mock_server reads commands from stdin, one per line, and drives the mock engine state directly, bypassing the gRPC command path. The production `Cs2PluginClient` only learns about these changes through its per-frame engine state reads (the frame pump above, or the ~120 Hz polling loop when the pump is disabled), so injected actions are observed exactly like real user actions in the CS2 console/demo UI: no command echo is sent, no host-command suppression stamp is set, so `DemoStateEvent` transitions they cause are attributed `ORIGIN_USER`. Without a real CS2, this is the only way to integration-test the game→viewer direction.

| Command | Effect |
|--------|---------|
| `user-pause` | `demo_pause` as the user; tick advancement stops |
| `user-resume` | `demo_resume` as the user; tick advancement resumes |
| `user-seek <tick>` | `demo_gototick <tick>` as the user (non-negative integer) |
| `user-playdemo <path>` | `playdemo <path>` as the user. The path need **not** exist — console use bypasses the host client's exists-check by design |
| `user-stop` | `stop` as the user: `IsPlayingDemo()` flips to false, demo state resets |
| `user-timescale <v>` | `demo_timescale <v>` as the user (engine ignores `v <= 0`) |
| `end-demo` | Immediate natural demo end, the same effect as reaching `CSVG_MOCK_DEMO_TICKS` |
| `game-event <name> [json]` | Synthetic client-side game event, offered to the plugin client's telemetry pipeline exactly as the real plugin's `FireEventClientSide` hook does (the mock's stand-in for that hook). Forwarded to the host only while a `SetGameEventSubscription` subscription is active; `[json]` defaults to `{}` and is passed through unvalidated |

```
user-pause
user-seek 5000
user-resume
user-timescale 4
end-demo
```

To drive it, write lines to mock_server's stdin. From a .NET test harness that means launching the process with a redirected standard input and calling `StandardInput.WriteLine("user-pause")`. From a terminal it means running mock_server directly, with an orchestrator listening on port 50051, and typing commands.

> **Orchestrator access:** the orchestrator pipes mock_server's stdin and exposes the channel as
> `IMockUserActionInjector` (resolve it from DI after `AddCs2VideoGeneratorCore`; in mock mode it is
> backed by `MockCs2ProcessManager`, otherwise `IsAvailable` is `false`). Use the `MockUserActions`
> helpers instead of raw strings:
>
> ```csharp
> IMockUserActionInjector injector = app.Services.GetRequiredService<IMockUserActionInjector>();
> if (injector.IsAvailable)
> {
>     await injector.InjectAsync(MockUserActions.Pause);
>     await injector.InjectAsync(MockUserActions.Seek(5000));
> }
> ```

Semantics and safety notes:

- Nothing is replied on stdout. The `[MOCK INJECT]`/`[MOCK ENGINE]` lines are ordinary mock log output, same stream as today.
- Unknown lines are logged (`[MOCK INJECT] Unknown injection command`) and ignored; blank lines are skipped; CRLF line endings are tolerated.
- EOF on stdin does not crash or exit the mock. Headless and test runners may close stdin; the reader thread just stops (logged) and the session continues.
- The injection thread and the gRPC reader thread are serialized inside the mock engine (same command mutex), so interleaving injected and host commands is safe.

Since the engine-pause-detection capability (this release), an injected `user-pause` **is** reflected in `TickUpdateEvent.is_paused`: the polling thread reads engine pause truth (`IDemoFile::IsDemoPaused`, which the mock overrides), force-sends a tick update carrying the new `is_paused`, and emits an `ORIGIN_USER` `DemoStateEvent` for the flip. The tick-stall inference — pause observable only as tick updates ceasing to advance — now applies only to pre-v1.1 plugins.

## Telemetry

Mock mode shows up in telemetry twice: as the `cli.mockMode` activity tag on the CLI's `watch` activity, and as the `mock_mode` dimension on the process-launch metrics recorded by `CsvgMetrics`.
