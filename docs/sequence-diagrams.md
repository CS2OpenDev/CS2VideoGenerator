# Cs2VideoGenerator Sequence Diagrams

## Session Lifecycle

Start a session, load a demo, shut down.

> **Note:** Two things the diagram leaves out. Before launching CS2, `StartAsync` self-probes the configured gRPC endpoint and fails fast if `Cs2GameService` is not hosted there. And when the plugin connects it reports its version, HL2SDK commit, and the running CS2 build; CSVG evaluates those with a warn-not-block compatibility check (only known-broken plugin/game pairs fail session start, overridable with `--force`).

```mermaid
sequenceDiagram
    participant Consumer as Application
    participant Session as CsvgVideoSession
    participant Engine as Cs2EngineSession
    participant PM as Cs2ProcessManager
    participant gRPC as Cs2GameService
    participant Plugin as CS2 Plugin
    participant CS2 as cs2.exe

    Consumer->>Session: StartAsync()
    activate Session
    Session->>Engine: create fresh session, ConnectAsync()
    activate Engine
    Engine->>PM: LaunchAsync()
    activate PM
    PM->>PM: BackupPath(gameinfo.gi)
    PM->>PM: InstallPluginFiles()
    PM->>PM: ModifyGameInfoFile()
    PM->>CS2: Launch cs2.exe process
    activate CS2
    CS2->>CS2: Initialize engine
    CS2->>Plugin: Load server.dll
    activate Plugin
    Plugin->>gRPC: CreateGameSession (bidirectional stream)
    activate gRPC
    gRPC->>Engine: OnSessionStartedAsync(sessionId)
    Plugin->>gRPC: CsProcessEvent(status: Ready)
    gRPC->>Engine: OnProcessStatusChangedAsync(Ready)
    Engine-->>Session: ConnectAsync completes
    deactivate Engine
    Session-->>Consumer: StartAsync completes
    deactivate Session

    Note over Consumer,CS2: Session is now ready for commands

    Consumer->>Session: LoadDemoAsync(demoPath)
    Session->>Engine: LoadDemoAsync(demoPath)
    Engine->>gRPC: CsControlRequest(LoadDemo)
    gRPC->>Plugin: LoadDemo command
    Plugin->>CS2: playdemo "path"
    CS2-->>Plugin: Demo loading
    Plugin->>gRPC: DemoPlaybackStatusChange(Loading)
    gRPC->>Engine: OnDemoPlaybackStatusChangedAsync
    CS2-->>Plugin: Demo loaded, paused at tick 0
    Plugin->>gRPC: DemoPlaybackStatusChange(Paused)
    gRPC->>Engine: OnDemoPlaybackStatusChangedAsync
    Session->>Session: capture session OnDemoLoadedAsync(demoPath)
    Session-->>Consumer: LoadDemoAsync completes

    Consumer->>Session: StopAsync()
    activate Session
    Note over Session: Capture is torn down FIRST - a recorder hooked into<br/>CS2 must not outlive it
    Session->>Engine: StopAsync()
    Engine->>PM: StopAsync()
    PM->>CS2: Kill process
    deactivate CS2
    deactivate Plugin
    Plugin->>gRPC: Stream ends
    gRPC->>Engine: OnSessionEndedAsync(sessionId)
    deactivate gRPC
    PM->>PM: UninstallPluginFiles()
    PM->>PM: RestorePath(gameinfo.gi)
    deactivate PM
    Session-->>Consumer: StopAsync completes
    deactivate Session
```

Every start creates a fresh `Cs2EngineSession` and a fresh `ICaptureSession`. Nothing
latched by one CS2 run — engine truth, plugin capabilities, a torn-down capture backend —
can be observed by the next one.

## Demo Playback with Recording

```mermaid
sequenceDiagram
    participant Consumer as Application
    participant Session as CsvgVideoSession
    participant Coord as ClipCaptureCoordinator
    participant Capture as ICaptureSession / IClipCapture
    participant gRPC as Cs2GameService
    participant Plugin as CS2 Plugin

    Note over Consumer,Plugin: Session already started, demo loaded

    Consumer->>Session: CaptureClipAsync(ClipCaptureContext)
    activate Session

    Session->>Coord: CaptureClipAsync(engineSession, captureSession, clip)
    activate Coord
    Coord->>Plugin: SetDemoOptions / SetSpectatorTarget (when the clip carries them)
    Coord->>Capture: BeginClipAsync(clip)
    activate Capture
    Coord->>Plugin: ScheduleCommandsAtTick(batch) for each TickPlan batch
    Coord->>Plugin: ExecuteConsoleCommands(TickPlan setup commands)
    Coord->>Plugin: ScheduleCommandsAtTick(startTick-1, spec_player) when a target is set
    Coord->>Capture: OnPlaybackStartingAsync()
    Capture-->>Coord: Recording started

    Coord->>gRPC: CsControlRequest(PlayDemoTickRange)
    gRPC->>Plugin: PlayDemoTickRange command
    Plugin->>Plugin: SetDemoTick(startTick)
    Plugin->>Plugin: ResumeDemo()

    loop Every 500 ticks (configurable)
        Plugin->>gRPC: CsProcessEvent(TickUpdate)
        gRPC->>Session: OnTickUpdated(tick)
        Session->>Consumer: TickUpdated event
    end

    Plugin->>Plugin: Tick reaches endTick
    Plugin->>Plugin: PauseDemo()
    Plugin->>gRPC: DemoPlaybackStatusChange(Paused)
    gRPC->>Session: OnDemoPlaybackStatusChanged

    Coord->>Capture: OnPlaybackEndedAsync()
    Coord->>Capture: FinalizeAsync()
    Capture-->>Coord: CaptureResult
    deactivate Capture
    Coord-->>Session: CaptureResult
    deactivate Coord

    Session-->>Consumer: CaptureResult
    deactivate Session
```

Capture problems come back as a failed `CaptureResult`, never as an exception. One bad
clip does not take the CS2 session down with it.

## Compilation Capture Flow

One compilation can pull clips from several demo files.

```mermaid
sequenceDiagram
    participant Consumer as Application
    participant Session as CsvgVideoSession
    participant Runner as Cs2CompilationRunner
    participant Coord as ClipCaptureCoordinator
    participant Capture as ICaptureSession / IClipCapture
    participant Plugin as CS2 Plugin

    Consumer->>Session: RunCompilationAsync(compilation)
    activate Session
    Session->>Runner: RunAsync(compilation, engine + capture session)
    activate Runner

    loop For each demo group (clips grouped by demo file)
        alt A different demo is open
            Runner->>Plugin: CloseDemo()
        end
        Runner->>Plugin: LoadDemo(demoPath)
        Plugin-->>Runner: Demo loaded
        Runner->>Capture: OnDemoLoadedAsync(demoPath)

        loop For each clip of the group, by ascending start tick
            Runner->>Session: clip started
            Session->>Consumer: CompilationClipStarted event

            Runner->>Coord: CaptureClipAsync(clip plan)
            Coord->>Plugin: SetDemoOptions / SetSpectatorTarget
            Coord->>Capture: BeginClipAsync + OnPlaybackStartingAsync
            Coord->>Plugin: PlayDemoTickRange(start, end)

            loop Tick updates
                Plugin->>Session: TickUpdate
            end

            Plugin-->>Coord: Playback paused at endTick
            Coord->>Capture: OnPlaybackEndedAsync + FinalizeAsync
            Capture-->>Coord: CaptureResult
            Coord-->>Runner: CaptureResult

            Runner->>Session: clip completed
            Session->>Consumer: CompilationClipCompleted event
            note over Runner: A failed clip ends the run here (fail fast)
        end
    end

    Runner->>Plugin: CloseDemo()
    opt Settings.ConcatenateClips
        Runner->>Runner: IFfmpegService.ConcatenateAsync(clips)
    end
    Runner-->>Session: Cs2CompilationResult
    deactivate Runner
    Session-->>Consumer: Cs2CompilationResult
    deactivate Session
```

Output files are numbered by each clip's ORIGINAL position in the compilation
(`{BaseFileName}_000`, `_001`, ...), not by the order the clips were recorded in. The
grouping and tick ordering above exist only to avoid demo reloads and backwards seeks.

## gRPC Bidirectional Stream

```mermaid
sequenceDiagram
    participant Plugin as CS2 Plugin (gRPC Client)
    participant Server as Cs2GameService (gRPC Server)

    Plugin->>Server: CreateGameSession() - open stream
    activate Server

    Note over Plugin,Server: Server sends commands, Plugin sends events

    rect rgb(200, 220, 240)
        Note over Plugin,Server: Commands (Server → Plugin)
        Server->>Plugin: CsControlRequest.LoadDemo
        Server->>Plugin: CsControlRequest.ResumeDemo
        Server->>Plugin: CsControlRequest.PauseDemo
        Server->>Plugin: CsControlRequest.SetDemoTick
        Server->>Plugin: CsControlRequest.CloseDemo
        Server->>Plugin: CsControlRequest.PlayDemoTickRange
        Server->>Plugin: CsControlRequest.SetSpectatorTarget
        Server->>Plugin: CsControlRequest.SetDemoOptions
        Server->>Plugin: CsControlRequest.SetTickUpdateFrequency
        Server->>Plugin: CsControlRequest.SetDemoTimescale / QueryDemoState
        Server->>Plugin: CsControlRequest.ScheduleCommandsAtTick / ClearScheduledCommands
        Server->>Plugin: CsControlRequest.ExecuteConsoleCommands
        Server->>Plugin: CsControlRequest.SetGameEventSubscription
    end

    rect rgb(220, 240, 200)
        Note over Plugin,Server: Events (Plugin → Server)
        Plugin->>Server: CsProcessEvent.TickUpdate
        Plugin->>Server: CsProcessEvent.DemoPlaybackStatusChange
        Plugin->>Server: CsProcessEvent.Cs2ProcessStatus
        Plugin->>Server: CsProcessEvent.DemoState / CommandAck
        Plugin->>Server: CsProcessEvent.GameEventTelemetry
    end

    Plugin->>Server: Stream closes (CS2 exit)
    deactivate Server
```

## Playback-Only Session (No Recording)

`StartWatchAsync` is the playback-only bootstrap for hosts that mirror playback without
recording. `csvg watch` (`WatchTask`) records by default, so it calls `StartAsync` and skips
capture bring-up only with `--no-capture`.

```mermaid
sequenceDiagram
    participant Consumer as Application
    participant Session as CsvgVideoSession
    participant PM as Cs2ProcessManager
    participant Plugin as CS2 Plugin

    Consumer->>Session: StartWatchAsync()
    activate Session
    Note over Session: Skips capture bring-up, then sets tick frequency 0
    Session->>PM: LaunchAsync()
    PM->>PM: Install plugin, launch CS2
    Plugin->>Session: Session connected
    Session-->>Consumer: Session ready
    deactivate Session

    Consumer->>Session: LoadDemoAsync(demoPath)
    Session->>Plugin: LoadDemo
    Plugin-->>Session: Demo loaded

    Consumer->>Session: PlayTickRangeAsync(start, end)
    Session->>Plugin: PlayDemoTickRange

    loop Demo playback
        Plugin->>Session: TickUpdate (every tick after StartWatchAsync)
        Session->>Consumer: TickUpdated event
    end

    Note over Consumer: User cancels or the range ends

    Consumer->>Session: StopAsync()
    Session->>PM: Cleanup and restore
```

## State Transitions

```mermaid
stateDiagram-v2
    [*] --> Disconnected
    Disconnected --> Connecting: StartAsync()
    Connecting --> Connected: Plugin connects
    Connecting --> Faulted: Timeout/Error
    Connected --> Disconnecting: StopAsync()
    Connected --> Faulted: Unexpected disconnect
    Disconnecting --> Disconnected: Cleanup complete
    Faulted --> Disconnected: StopAsync()
```

## Capture States

`ClipCaptureCoordinator` owns this machine and drives it once per clip; `CsvgVideoSession`
re-publishes the transitions as `CaptureStateChanged`.

```mermaid
stateDiagram-v2
    [*] --> NotInitialized
    NotInitialized --> PreparingEnvironment: BeginClipAsync()
    PreparingEnvironment --> Capturing: OnPlaybackStartingAsync()
    Capturing --> Stopping: playback finished
    Stopping --> Finalizing: OnPlaybackEndedAsync()
    Finalizing --> Ready: FinalizeAsync()
    Ready --> PreparingEnvironment: next clip
    PreparingEnvironment --> Error: clip failed
    Capturing --> Error: clip failed
    Stopping --> Error: clip failed
    Finalizing --> Error: clip failed
    Error --> PreparingEnvironment: next clip
```
