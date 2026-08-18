# Migrating to 2.0

2.0 replaces `ICsvgClient` with an object graph. There is **no compatibility shim**. The old
interface, its implementation and the old capture-provider abstraction were deleted rather than
deprecated, so a 1.x consumer will not compile against 2.0 until every call site has been moved.

The short version, for the impatient:

| 1.x | 2.0 |
|---|---|
| `ICsvgClient` / `CsvgClient` | `CsvgVideoSession`, composed by `CsvgVideoSessionBuilder` or resolved from DI |
| demo control on the client | `session.Engine` (`ICs2EngineSession`), a fresh instance per CS2 run |
| `IVideoCaptureProvider` | `ICaptureSessionFactory` → `ICaptureSession` → `IClipCapture` |
| session-scoped capture metadata + clip counter | a `ClipCaptureContext` per clip |

The [CHANGELOG](../CHANGELOG.md) entry for `[1.1.1]` summarizes the same break.

## Why the break

`CsvgClient` was one 3,200-line type holding six concerns behind one lock: the gRPC session
lifecycle, demo control, latched engine truth, capture bring-up, per-clip capture choreography and
the compilation pipeline. Its state was session-scoped and mutable. A second `StartAsync` on the
same instance inherited whatever the first left behind: a disposed capture provider still flagged
as initialized, a clip counter that restarted output numbering on top of the previous run's files,
and check-then-act windows in start and stop that two callers could both pass through. Those were
not independent bugs. They were the same bug wearing different hats, and the only fix that removes
the category is to make the stale state unrepresentable.

So the shape of 2.0 is one object per lifetime. Something that lives as long as the process is a
different type from something that lives for one CS2 run, which is a different type from something
that lives for one clip. A stale reference is then a compile-time or dispose-time problem rather
than a field nobody remembered to reset.

## What was deleted

- **`ICsvgClient`** and **`CsvgClient`**. Replaced by `CsvgVideoSession` plus `ICs2EngineSession`.
- **`IVideoCaptureProvider`**, replaced by the three capture interfaces. A host that registered
  its own capture backend now registers an `ICaptureSessionFactory`.
- **`CsvgClient.SessionCaptureMetadata`** and **`CsvgClient.ClipIndex`** — output naming travels
  with each clip instead of being session state.
- `CaptureMetadata.GetFullOutputPath(int clipIndex)`. Use
  `metadata.WithClipIndex(i).GetFullOutputPath()` instead; it is now the one place an indexed clip
  filename is formed.
- `CaptureConfiguration.ConnectionTimeoutMs` and `CaptureConfiguration.AdditionalSettings`, which
  nothing read. Provider-specific settings live in the provider's own configuration type, bound
  from `Capture:<provider>`.

`CsvgVideoSessionBuilder.FromServices` existed briefly on the development branch and was removed
before release; it never shipped. Under a container the session is resolved, not rebuilt (see
[Construction](#construction)).

## The new object graph

```
ICs2EngineHost              one per process — creates engine sessions, tracks the current one
  └── Cs2EngineSession      one per CS2 run — demo control, latched engine truth, capabilities
        └── IEngineConsole  the narrow view a capture backend gets: exec, tick, capabilities

ICaptureSessionFactory      one per backend, per process — availability, platform gate
  └── ICaptureSession       one per CS2 run — backend bring-up, per-run resources
        └── IClipCapture    one per clip — start, stop, finalize, abort

ClipCaptureCoordinator      stateless — the single per-clip recipe, shared by every backend
Cs2CompilationRunner        stateless — demo grouping, per-clip capture, concatenation
CsvgVideoSession            the entry point that composes all of the above
```

`ICs2EngineHost` is a singleton because two of them would mean the gRPC layer pushing plugin events
at a session nobody is holding. `Cs2GameService` routes the plugin's event stream through
`ICs2EngineHost.Current`, so the host the service is built over and the host the video session
creates sessions from must be the same instance. Under DI that happens automatically. Without a
container it is your responsibility (`session.EngineHost`).

`ICs2EngineSession` is created fresh by every `CsvgVideoSession.StartAsync`. Hold it for one run
and no longer. Read it from `session.Engine` at the point of use rather than caching it in a
field. `IEngineConsole` is the subset of it a capture backend sees: capability queries, the observed
tick, and `ExecuteConsoleCommandsAsync`. A backend gets that and nothing else, which is why a
backend cannot load a demo or seek behind the coordinator's back.

`ClipCaptureCoordinator` owns the one per-clip recipe — apply demo options, arm the backend's tick
plan, seek, start, play, stop, finalize, and on failure run the backend's abort commands. It is
stateless apart from the capture state machine it exposes, so a single instance serves every
session, and a compilation's clips go through exactly the same path as a one-off
`CaptureClipAsync`.

`ICs2EngineWire` is internal. It is how `Cs2GameService` pushes wire events into the engine session;
it is not part of the public surface and consumers should not look for it.

## Construction

### With a container

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddCs2VideoGeneratorCore();

// Host the gRPC endpoint the CS2 plugin / mock server connects back to (required)
builder.WebHost.ConfigureKestrel(o =>
    o.ListenLocalhost(50051, l => l.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc();

WebApplication app = builder.Build();
app.MapGrpcService<Cs2GameService>();
await app.StartAsync();

CsvgVideoSession session = app.Services.GetRequiredService<CsvgVideoSession>();
await session.StartAsync();
```

`AddCs2VideoGeneratorCore` registers `Cs2GameService` but does **not** host a gRPC server. Your
process must map the service on an HTTP/2 endpoint at `Cs2VideoGenerator:GrpcPort` (default
`50051`); the plugin connects *back* to you. This requirement is unchanged from 1.x. `StartAsync`
still probes the port before launching CS2, so a host that forgot fails fast with an explanatory
`InvalidOperationException` instead of burning the connect timeout.

`CsvgVideoSession` is registered as a singleton. Resolve it; do not compose a second one from the
same container, which is what `FromServices` used to invite. The container's `ICs2EngineHost` is a
singleton, so a second session would create engine sessions on the same host and quietly steal
`Current` from the first.

### Without a container

```csharp
CsvgVideoSession session = new CsvgVideoSessionBuilder()
    .WithLoggerFactory(loggerFactory)
    .WithOptions(new Cs2VideoGeneratorOptions { MockMode = true })
    .WithProcessManager(processManager)
    .Build();

// The Cs2GameService you map must be built over the SAME engine host.
var grpcService = new Cs2GameService(loggerFactory, session.EngineHost);
```

Exactly one of `WithProcessManager` or `WithEngineHost` is required. Without either, `Build()`
throws an `InvalidOperationException` naming both. When both are supplied the engine host wins and
the process manager is ignored, because a second host is precisely the failure the builder exists to
prevent. Everything else has a working default:

| Collaborator | Method | Default when omitted |
|---|---|---|
| `ILoggerFactory` | `WithLoggerFactory` | `NullLoggerFactory.Instance` |
| `Cs2VideoGeneratorOptions` | `WithOptions` | a default-constructed instance |
| `ICs2ProcessManager` | `WithProcessManager` | none — required unless a host is supplied |
| `ICs2EngineHost` | `WithEngineHost` | built from the process manager |
| `ICaptureSessionFactory` | `WithCaptureProvider` | none — the session runs playback-only |
| `ClipCaptureCoordinator` | `WithClipCaptureCoordinator` | a new one |
| `Cs2CompilationRunner` | `WithCompilationRunner` | a new one over *this* coordinator |
| `IFfmpegService` | `WithFfmpegService` | none — concatenation unavailable |
| `IGrpcEndpointProbe` | `WithGrpcEndpointProbe` | none — no pre-launch endpoint probe |

Supplying a `Cs2CompilationRunner` built over a different `ClipCaptureCoordinator` than the session
uses is refused with an `ArgumentException`. It would build and start perfectly and then report a
compilation's capture state to a state machine nobody is subscribed to.

## Member-by-member mapping

`session` below is a `CsvgVideoSession`. "Engine" means `session.Engine`, the `ICs2EngineSession`
for the current run.

### `ICsvgClient` state

| 1.x member | 2.0 |
|---|---|
| `State` | `session.State` — unchanged, still `CsvgSessionState` |
| `SessionId` | `session.Engine.SessionId` |
| `LastTick` | `session.Engine.LastTick` (backends see `IEngineConsole.LastObservedTick`) |
| `LastProcessStatus` | `session.Engine.LastProcessStatus` |
| `LastDemoPlaybackStatusChange` | `session.Engine.LastDemoPlaybackStatusChange` |
| `PluginCapabilities` | `session.Engine.PluginCapabilities` |
| `Supports(token)` | `session.Engine.Supports(token)` |
| `LastDemoState` | `session.Engine.LastDemoState` |
| `LastKnownTimescale` | `session.Engine.LastKnownTimescale` |
| `LastTickIsPaused` | `session.Engine.LastTickIsPaused` |

`ICs2EngineSession.PluginCapabilities` and `IEngineConsole.Capabilities` are the same set under two
names: the first is what a consumer reads, the second is what the narrow backend-facing view
exposes. Neither replaced the other.

### Capture state

| 1.x member | 2.0 |
|---|---|
| `HasCaptureProvider` | `session.HasCaptureProvider` — unchanged |
| `CaptureProviderState` | **renamed** to `session.CaptureState` (still `CaptureState?`) |
| `LastCaptureResult` | `session.LastCaptureResult` — unchanged |
| — | **new:** `session.CaptureProviderName`, the selected backend's canonical name |

### Events

Every event a 1.x consumer subscribed to still exists on `CsvgVideoSession` with the same name and
the same delegate signature. See [Events](#events) for which object to prefer.

| 1.x member | 2.0 |
|---|---|
| `SessionStarted` | `session.SessionStarted` (per-run form: `session.Engine.Started`) |
| `SessionEnded` | `session.SessionEnded` (per-run form: `session.Engine.Ended`) |
| `TickUpdated` | `session.TickUpdated` |
| `DemoPlaybackStatusChanged` | `session.DemoPlaybackStatusChanged` |
| `DemoStateChanged` | `session.DemoStateChanged` |
| `GameEventReceived` | `session.GameEventReceived` |
| `CommandRejected` | `session.CommandRejected` |
| `ProcessStatusChanged` | `session.ProcessStatusChanged` |
| `StateChanged` | `session.StateChanged` |
| `CaptureStateChanged` | `session.CaptureStateChanged` |
| `CaptureProgressUpdated` | `session.CaptureProgressUpdated` |
| `CaptureErrorOccurred` | `session.CaptureErrorOccurred` |
| `CompilationClipStarted` | `session.CompilationClipStarted`, or `Cs2CompilationRunContext.ClipStarted` |
| `CompilationClipCompleted` | `session.CompilationClipCompleted`, or `Cs2CompilationRunContext.ClipCompleted` |

### Session capture configuration

| 1.x member | 2.0 |
|---|---|
| `SessionCaptureMetadata` | **removed.** Pass `CaptureMetadata` on each `ClipCaptureContext` |
| `ClipIndex` | **removed.** See below |

Nothing auto-increments a clip counter any more, and `CaptureClipAsync` does **not** append an index
to your output filename. `ClipCaptureContext.ClipIndex` is carried for logs and traces only. If you
relied on 1.x producing `clip_000.mp4`, `clip_001.mp4`, … from one `SessionCaptureMetadata`, you now
own the numbering:

```csharp
CaptureMetadata baseMetadata = new() { OutputDirectory = outputDir, OutputFileName = "clip" };

for (int i = 0; i < ranges.Count; i++)
{
    await session.CaptureClipAsync(new ClipCaptureContext
    {
        StartTick = ranges[i].Start,
        EndTick = ranges[i].End,
        ClipIndex = i,
        Metadata = baseMetadata.WithClipIndex(i)   // clip_000, clip_001, ...
    });
}
```

The compilation pipeline still numbers its own clips: `Cs2CompilationSettings.CreateClipMetadata`
runs the same `WithClipIndex` off the compilation's `BaseFileName`.

### Session lifecycle

| 1.x member | 2.0 |
|---|---|
| `StartSessionAsync(ct)` | `session.StartAsync(ct)` |
| `StartSessionAsync(w, h, fullscreen, ct)` | `session.StartAsync(new EngineSessionOptions { Width = w, Height = h, Fullscreen = fullscreen }, initializeCapture: true, ct)` |
| `StartSessionAsync(w, h, fullscreen, initializeCapture, ct)` | `session.StartAsync(EngineSessionOptions, bool, ct)` |
| `StartWatchSessionAsync(w, h, fullscreen, ct)` | `session.StartWatchAsync(EngineSessionOptions?, ct)` |
| `StopSessionAsync(ct)` | `session.StopAsync(ct)` |
| `WaitForSessionEndAsync(ct)` | `session.WaitForEndAsync(ct)` |
| `IDisposable` / `IAsyncDisposable` | `CsvgVideoSession` implements both — `await using` still works |

The `Faulted` recovery rule is unchanged: a session can only start from `Disconnected`, and
`StopAsync` is the reset path out of `Faulted`.

### Demo control

All of these moved to `session.Engine` with identical signatures.

| 1.x member | 2.0 |
|---|---|
| `SetSpectatorTargetAsync(name, ct)` | `session.Engine.SetSpectatorTargetAsync` — but prefer `ClipCaptureContext.SpectatorTarget` for a clip |
| `SetDemoOptionsAsync(options, ct)` | `session.Engine.SetDemoOptionsAsync` — but prefer `ClipCaptureContext.ClipOptions` for a clip |
| `ResumeDemoAsync` | `session.Engine.ResumeDemoAsync` |
| `PauseDemoAsync` | `session.Engine.PauseDemoAsync` |
| `CloseDemoAsync` | `session.Engine.CloseDemoAsync` |
| `SetDemoTickAsync(tick, ct)` | `session.Engine.SetDemoTickAsync` |
| `SetDemoTickAsync(tick, pauseAfterSeek, waitForCompletion, ct)` | `session.Engine.SetDemoTickAsync` |
| `SetTickUpdateFrequencyAsync` | `session.Engine.SetTickUpdateFrequencyAsync` |
| `SetDemoTimescaleAsync` | `session.Engine.SetDemoTimescaleAsync` |
| `QueryDemoStateAsync` | `session.Engine.QueryDemoStateAsync` |
| `ScheduleCommandsAtTickAsync` | `session.Engine.ScheduleCommandsAtTickAsync` |
| `ClearScheduledCommandsAsync` | `session.Engine.ClearScheduledCommandsAsync` |
| `SetGameEventSubscriptionAsync` | `session.Engine.SetGameEventSubscriptionAsync` |

Loading a demo is the exception. It stays on the video session:

| 1.x member | 2.0 |
|---|---|
| `LoadDemoAsync(path, ct)` | `session.LoadDemoAsync(path, cancellationToken: ct)` |
| `LoadDemoAsync(path, interactiveDemoUi, ct)` | `session.LoadDemoAsync(path, interactiveDemoUi, ct)` |

`session.LoadDemoAsync` loads through the engine *and then* tells the capture backend about it
(`ICaptureSession.OnDemoLoadedAsync`). A backend that has to register against the loaded demo would
never hear about a demo loaded straight through `session.Engine.LoadDemoAsync`. Prefer the
video-session form on the single-clip path. The compilation pipeline performs the same pair itself.

### Playback and capture

| 1.x member | 2.0 |
|---|---|
| `PlayDemoTickRangeAsync(start, end, record: false, timeout, ct)` | `session.PlayTickRangeAsync(start, end, timeout, ct)` |
| `PlayDemoTickRangeAsync(start, end, record: true, timeout, ct)` | `session.CaptureClipAsync(ClipCaptureContext)` |
| `PlayDemoTickRangeWithCaptureAsync(start, end, metadata, timeout, ct)` | `session.CaptureClipAsync(ClipCaptureContext)` |
| `InitializeCaptureAsync(ct)` | `session.InitializeCaptureAsync(ct)` — unchanged |
| `CaptureCompilationAsync(compilation, ct)` | `session.RunCompilationAsync(compilation, ct)` |

`PlayTickRangeAsync` exists on **both** objects with the same name and different behavior, which is
easy to get wrong:

- `CsvgVideoSession.PlayTickRangeAsync(...)` returns `Task<DemoPlaybackResult>` and converts
  playback problems (bad demo state, timeout, send failure) into a failed result. This is the
  1.x `PlayDemoTickRangeAsync` replacement.
- `ICs2EngineSession.PlayTickRangeAsync(...)` returns `Task` and throws. Use it when you want the
  exception.

A recording call becomes:

```csharp
CaptureResult result = await session.CaptureClipAsync(new ClipCaptureContext
{
    StartTick = 12000,
    EndTick = 12600,
    Metadata = new CaptureMetadata { OutputDirectory = @"C:\clips", OutputFileName = "ace" },
    SpectatorTarget = "s1mple"
});
```

`ClipCaptureContext` also carries `ClipOptions`, `PlaybackTimeout` (null estimates from the tick
range and last known timescale) and `ClipIndex`. It exposes `FailClip(Exception)` as well, for a
caller that learns out-of-band that the clip is worthless and wants the coordinator to abort it
rather than finish and finalize. The coordinator links that token into the clip's cancellation and
reports the supplied exception as the failure, with `CaptureAbortReason.BackendFault`.

### `IVideoCaptureProvider`

If you implemented your own capture backend, the single provider interface split three ways.

| 1.x member | 2.0 |
|---|---|
| `State` | not on the contract — the coordinator owns the capture state machine (`ClipCaptureCoordinator.State`, surfaced as `CsvgVideoSession.CaptureState`) |
| `StateChanged` | `ClipCaptureCoordinator.StateChanged` / `CsvgVideoSession.CaptureStateChanged` |
| `ProgressUpdated` | `ICaptureSession.ProgressUpdated` |
| `ErrorOccurred` | `ICaptureSession.ErrorOccurred` |
| `CheckAvailabilityAsync(configuration, ct)` | `ICaptureSessionFactory.CheckAvailabilityAsync(ct)` — no configuration parameter; the factory holds its own `IOptions<T>` bound from `Capture:<name>` |
| `PrepareCaptureEnvironmentAsync(configuration, metadata, ct)` | splits into `ICaptureSessionFactory.Create(CaptureSessionContext)` + `ICaptureSession.InitializeAsync(ct)` (once per CS2 run) and `ICaptureSession.BeginClipAsync(ClipCaptureContext, ct)` (once per clip) |
| `StartCaptureAsync(ct)` | `IClipCapture.OnPlaybackStartingAsync(ct)` |
| `StopCaptureAsync(ct)` | `IClipCapture.OnPlaybackEndedAsync(ct)` |
| `FinalizeCaptureAsync(ct)` | `IClipCapture.FinalizeAsync(ct)` |
| `PauseCaptureAsync` / `ResumeCaptureAsync` | **not on the shared contract** — see [Behavior changes](#behavior-changes) |
| `IAsyncDisposable` | on both `ICaptureSession` and `IClipCapture`; a session also gets `ShutdownAsync(ct)` for orderly teardown |
| — | **new:** `ICaptureSessionFactory.ProviderName` / `IsSupportedOnCurrentPlatform` |
| — | **new:** `ICaptureSession.Traits` (`CaptureTraits`) and `OnDemoLoadedAsync` |
| — | **new:** `IClipCapture.TickPlan` (`TickCommandPlan`) and `AbortAsync(CaptureAbortReason, ct)` |

Registration is keyed by provider name. The built-in registrations use `TryAddKeyedSingleton`, so a
factory registered under the same key **before** `AddCs2VideoGeneratorCore` wins. That is how you
replace the bundled OBS backend with your own:

```csharp
services.AddKeyedSingleton<ICaptureSessionFactory>(
    CaptureProviderNames.Obs, (_, _) => new MyObsCaptureSessionFactory());
services.AddCs2VideoGeneratorCore(configuration);
```

A backend under a *new* name needs one more thing: `Cs2VideoGeneratorOptions.Validate` rejects any
`VideoCaptureProvider` not in `CaptureProviderNames.All`, so an unknown name is refused at startup
even when a factory is registered for it. Either add the name to `CaptureProviderNames` (which is
what shipping a new backend in the library means) or register your factory **unkeyed**. The
selection forwarder is also a `TryAddSingleton`, so a host-supplied unkeyed
`ICaptureSessionFactory` is what `CsvgVideoSession` resolves and name resolution never runs. On that
path leave `VideoCaptureProvider` at a known value, because options validation runs either way.

`TickCommandPlan` is how a backend asks for engine-side brackets around a clip without touching the
engine itself. `SetupCommands` run immediately before playback through `console-exec`,
`Batches` are armed against demo ticks through the plugin's tick scheduler, and `AbortCommands` run
through `console-exec` if the clip fails. Which channel a command belongs in is decided by whether
it needs a tick. Setup that only has to be in place *before* the recording tick has nothing to gain
from the scheduler, and everything to lose to its half-open `(lastTick, currentTick]` interval.

## Events

Every 1.x event still exists on `CsvgVideoSession` under its old name and signature, so the simplest
migration is to keep subscribing exactly where you did. That is also the right answer.
`CsvgVideoSession` owns the subscriptions and forwards from whichever engine session
is current, so a handler registered while run A was live keeps firing after a Start→Stop→Start
cycle replaces it with run B. Subscribing to `session.Engine.Started` / `.Ended` / `.TickUpdated`
instead binds you to *that run's* session object and goes silent when the next start replaces it.

Subscribe on `session.Engine` only when you genuinely want one run's events and are re-subscribing
per run.

Three tick signals exist; pick by intent:

| Signal | Signature | Use for |
|---|---|---|
| `CsvgVideoSession.TickUpdated` | `Action<string, long>` | the 1.x replacement — survives Start→Stop→Start |
| `ICs2EngineSession.TickUpdated` | `Action<string, long>` | one run's ticks |
| `IEngineConsole.TickObserved` | `Action<long>` | capture backends, which have no session id to care about |

The compilation events are the one place with a genuinely new option. `Cs2CompilationRunner` takes
its per-clip callbacks on the run context rather than as events on the runner, because the runner is
a stateless singleton and events on it would be shared by every concurrent run:

```csharp
Cs2CompilationResult result = await runner.RunAsync(compilation, new Cs2CompilationRunContext
{
    Engine = session.Engine,
    Capture = captureSession,
    ClipStarted = (index, clip) => { /* ... */ return Task.CompletedTask; },
    ClipCompleted = (index, clipResult) => { /* ... */ return Task.CompletedTask; }
}, cancellationToken);
```

You only need that form if you drive `Cs2CompilationRunner` yourself. `session.RunCompilationAsync`
builds the context for you and wires the callbacks to the `CompilationClipStarted` /
`CompilationClipCompleted` events, so a 1.x consumer keeps its event handlers unchanged.

## Behavior changes

| Change | What to do |
|---|---|
| Capture metrics are tagged `"OBS"`, not `"ObsCapture"` | Update dashboards and alerts filtering the `provider` tag on `cs2.capture.provider.status` and `cs2.capture.session.count`. The tag now carries the provider's selection name — the same string `--capture-provider` takes |
| `CaptureResult.Duration` means different things per backend | Read `session`-level traits before interpreting it (below) |
| Provider selection is a keyed registry | `Cs2VideoGenerator:VideoCaptureProvider` or `--capture-provider`; names match case-insensitively and normalize to canonical spelling |
| Per-provider configuration sections | OBS settings moved under `Capture:OBS`, in-engine settings under `Capture:InEngine` |
| `CaptureConfiguration.ConnectionTimeoutMs` / `AdditionalSettings` deleted | Remove them from configuration files; nothing read them |
| `Pause`/`Resume` are not on the capture contract | Check `CaptureTraits.SupportsPauseResume` and use the backend's concrete type |
| A clip is refused when OBS is already recording | Stop any manual OBS recording before capturing; the failure is now explicit rather than an opaque `StartRecord` error |
| Tracing span names follow the owning type | Demo-control and playback spans are `Cs2EngineSession.*` (were `CsvgClient.*`); lifecycle spans are `CsvgVideoSession.Start` / `.Stop` / `.CaptureClip` / `.PlayTickRange` |

### `CaptureResult.Duration`

For OBS, `Duration` is wall clock: how long the recording ran. For the in-engine provider it is
game time, computed as frames divided by frame rate, because the engine advances game time by
exactly `1/fps` per rendered frame regardless of how long the frame took to draw. The wall-clock
figure, achieved fps and realtime factor go into `Statistics` instead. Do not compare the two
across providers without checking which you have:

```csharp
bool durationIsGameTime = captureSession.Traits.TimingModel == CaptureTimingModel.TickExact;
```

`CaptureTraits` also tells you whether the backend honors `CaptureMetadata.FrameRate`
(`HonorsFrameRate`; OBS takes its frame rate from the OBS profile and treats the metadata value as
advisory), whether it drives CS2 itself (`UsesEngine`), and whether pause/resume exist.

### Pause and resume

`IVideoCaptureProvider.PauseCaptureAsync` / `ResumeCaptureAsync` were on the shared contract even
though only OBS could implement them, which meant every other backend had to throw or lie. They are
now on the concrete type, advertised through a trait:

```csharp
if (captureSession.Traits.SupportsPauseResume && clipCapture is ObsClipCapture obs)
{
    await obs.PauseAsync(cancellationToken);
}
```

In practice this is only reachable if you drive `ICaptureSession`/`IClipCapture` directly.
`CsvgVideoSession.CaptureClipAsync` owns the whole clip lifetime and never exposes the
`IClipCapture`. There is no supported way to pause a clip mid-capture through the video session,
and there was no `ICsvgClient` member for it in 1.x either.

### Capability policy

The policy is documented on `CsvgCapabilities` and has two halves. Latched telemetry and work the
orchestrator started on its own initiative **degrade**: a reader with nothing to report stays null
or empty (`LastDemoState`), and optional work driven from configuration rather than from a call is
skipped with a warning (the startup game-event subscription). An operation a caller explicitly asked
for that has no v1.0 equivalent **throws** `NotSupportedException` naming the capability, because
returning as though it had run puts the failure somewhere far away from its cause. Where a legacy
path does exist the call takes it instead of throwing: an unacknowledged `SetDemoTick` without
`command-ack`, a plain send without an ack wait.

This is unchanged from 1.x in substance. It is spelled out here because the 1.x README stated only
the "read-only degrades, state-changing throws" half, which does not tell you what happens to
configuration-driven work.

## Bugs fixed

If you built retries, sleeps or restart logic around any of these, you can remove it.

- **`WaitForPause` lost wakeups.** Playing a tick range checked the latched playback status *before*
  subscribing to the status-change event. The plugin emits the auto-pause `PAUSED` status within
  microseconds of the forced end-tick update, and a status dispatched inside that
  check-then-subscribe gap was lost. Nothing later re-signals it, so the call burned its full
  timeout (30 s+) despite playback having paused exactly on cue. It reproduced as a roughly
  every-other-run flake in the mock-mode E2E fallback leg and was reachable against the real plugin
  too. The wait scaffolding now re-probes latched state immediately after the subscription is
  installed.
- **Start→Stop→Start recorded through a disposed capture provider.** The "capture is initialized"
  flag survived a stop, so the second run skipped bring-up and drove the previous run's torn-down
  backend. Capture sessions are now per CS2 run and the stopped one is disposed when replaced.
- **A second run reused the first run's clip numbering and output metadata**, silently overwriting
  the first run's files. Both were session-scoped mutable state; both are gone.
- **Two concurrent starts could both proceed.** The "am I disconnected?" check and the claim on the
  session were separate lock acquisitions. The claim now happens under the same acquisition as the
  check.
- **Two concurrent stops could both proceed.** `StopAsync` read the state outside the gate and only
  flipped it to `Disconnecting` afterwards. Duplicate teardown was itself survivable, but the losing
  stop announced `Disconnected` from its `finally` while the winner was still killing CS2 — enough
  for a concurrent start to be accepted against a dying process. A stop arriving while one is in
  flight now returns instead of running a second teardown.
- `InitializeCaptureAsync` leaked the capture session it replaced. The documented manual
  re-initialization path swapped in a new capture session without disposing the old one, leaking its
  process manager and leaving its event handlers attached. Two related fixes rode along: OBS dropped
  its audio mute controller with the connection (a re-initialized session used to issue mute calls
  against a dead socket), and `CaptureSessionContext.SessionToken` is now a token tied to the
  capture session's lifetime rather than the caller's bring-up token, which could be cancelled or
  disposed while the session was still alive.

## New capabilities worth adopting

Each is gated on a plugin capability token; call `session.Engine.Supports(token)` first, or handle
`NotSupportedException`.

- **Engine-thread command execution.** The plugin no longer calls
  `ISource2EngineToClient::ExecuteClientCmd` from its background threads, which is not thread-safe
  and crashes CS2 outright when it lands during a demo load. It patches
  `Source2Client002::FrameStageNotify` (slot 36) and drains a command queue at `FRAME_START`. The
  hook cannot be installed at plugin load — `client.dll` is not up yet — so the plugin's polling
  loop retries every cycle until `Source2Client002` resolves, a few seconds into boot. Until then
  commands **wait in the queue**; there is no direct-call fallback, because there is no thread-safe
  way to make one. A command that waits more than 10 seconds is named in the plugin log
  (`[gRPC] STRANDED command: …`) and the host's own command timeouts surface it to the caller.
  Nothing to opt into; it is how commands run now.
- **Tick-scheduled commands** (`tick-scheduled-commands`).
  `session.Engine.ScheduleCommandsAtTickAsync(tick, commands)` registers console commands the plugin
  executes on the engine thread the first time the observed tick crosses the target. The interval is
  half-open, `(lastTick, currentTick]`, so a forward seek cannot step over a schedule and a backward
  jump fires nothing. One-shot; cleared by loading a demo or by
  `ClearScheduledCommandsAsync`.
- **Immediate console execution** (`console-exec`).
  `session.Engine.ExecuteConsoleCommandsAsync(commands)` queues a batch for the engine thread with
  no tick condition. Blind by nature: the engine reports nothing back, so the completed task means
  "queued for the engine thread", not "CS2 accepted it".
- **Game-event telemetry** (`game-event-telemetry`, off by default, Windows plugin and mock server
  only) — `session.Engine.SetGameEventSubscriptionAsync(true, ["player_death"])` installs the hook
  lazily and forwards matching events as `session.GameEventReceived`, carrying the game's own
  KV3-as-JSON payload. The queue is bounded and drops the oldest events under backpressure rather
  than blocking CS2's engine thread, so treat it as observability, not a control channel. The Linux
  plugin withholds the capability (the vtable lookup is MSVC-RTTI specific) and the call throws
  there. Config opt-in: `Cs2VideoGenerator:EnableGameEventTelemetry` plus `GameEventNames`, or
  `--game-events` on the CLI.

## The `InEngine` capture provider

2.0 adds a second capture backend that renders offline through CS2's own `startmovie` and
`host_framerate` instead of screen-recording with OBS. It is frame-exact, honors
`CaptureMetadata.FrameRate` per clip, and works headless.

**It is the default since 1.1.1**, validated against a real CS2 build (2000879): where `startmovie`
writes, how it numbers frames, jpg mode, where the wav lands, and Targa's uncompressed output are
observed facts. The assumptions that remain are the cvar restore values and the tick-96 clamp, and
`csvg doctor` and `CheckAvailabilityAsync` lead with them. A faster hooked variant,
`InEngineHooked`, arrived in 1.1.2 (Windows only, experimental).

It requires a plugin advertising **both** `tick-scheduled-commands` (for `startmovie`, which
genuinely needs a tick) and `console-exec` (for the cvar setup, which does not). The clip fails
naming whichever is missing: a plugin that cannot set `host_framerate` must not be allowed to record
in real time and call the result a fixed timestep.

See [In-engine capture](in-engine-capture.md) for how it works, what it costs, and which
questions are still open.

## See also

- [CHANGELOG](../CHANGELOG.md) — the `[1.1.1]` entry
- [README: Configuration](../README.md#configuration) and
  [Using Core as a Library](../README.md#using-core-as-a-library)
- [In-engine capture](in-engine-capture.md)
- [Sequence diagrams](sequence-diagrams.md)
