#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;

#endregion

namespace Cs2VideoGenerator.Core.Tests.TestSupport;

/// <summary>
///     Records what the component under test did, in order, so a test can assert the sequence rather
///     than only the end state. Shared by the engine and capture doubles below, which is what makes
///     "every batch was armed before playback started" expressible.
/// </summary>
public sealed class CaptureJournal
{
    private readonly List<string> _entries = [];

    /// <summary>The entries recorded so far, in order.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Records one entry.</summary>
    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>
///     Minimal <see cref="IEngineConsole" />: the whole engine view a capture backend is allowed.
/// </summary>
public sealed class FakeEngineConsole : IEngineConsole
{
    /// <summary>The capability tokens this console advertises.</summary>
    public HashSet<string> AdvertisedCapabilities { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlySet<string> Capabilities => AdvertisedCapabilities;

    /// <inheritdoc />
    public long? LastObservedTick { get; set; }

    /// <inheritdoc />
    public bool Supports(string capability) => AdvertisedCapabilities.Contains(capability);

    /// <inheritdoc />
    public event Action<long>? TickObserved;

    /// <summary>Every batch passed to <see cref="ExecuteConsoleCommandsAsync" />, in order.</summary>
    public List<IReadOnlyList<string>> ExecutedConsoleCommands { get; } = [];

    /// <inheritdoc />
    public Task ExecuteConsoleCommandsAsync(IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.ConsoleExec))
        {
            throw new NotSupportedException("console-exec is not advertised");
        }

        ExecutedConsoleCommands.Add(consoleCommands);
        return Task.CompletedTask;
    }

    /// <summary>Raises <see cref="TickObserved" />.</summary>
    public void RaiseTickObserved(long tick)
    {
        LastObservedTick = tick;
        TickObserved?.Invoke(tick);
    }
}

/// <summary>
///     An <see cref="ICs2EngineSession" /> that records the per-clip engine setup and tick batches it
///     was asked to arm and runs a scriptable playback, so <see cref="ClipCaptureCoordinator" />'s
///     recipe can be proven without a gRPC connection or a plugin. Demo navigation throws unless a test
///     opts in with <see cref="AllowDemoControl" /> (the coordinator never touches it; the compilation
///     runner does).
/// </summary>
public sealed class FakeEngineSession : ICs2EngineSession
{
    private readonly List<(int DemoTick, IReadOnlyList<string> Commands)> _armed = [];
    private readonly CaptureJournal? _journal;

    /// <summary>Creates the fake, optionally recording into a shared journal.</summary>
    public FakeEngineSession(CaptureJournal? journal = null) => _journal = journal;

    /// <summary>The capability tokens the "plugin" advertises.</summary>
    public HashSet<string> AdvertisedCapabilities { get; } = new(StringComparer.Ordinal);

    /// <summary>Every batch passed to <see cref="ScheduleCommandsAtTickAsync" />, in order.</summary>
    public List<(int DemoTick, IReadOnlyList<string> Commands)> ScheduledBatches { get; } = [];

    /// <summary>
    ///     Every batch <see cref="RaiseTickObserved" /> actually fired, in order. A batch that was armed
    ///     but never reached is in <see cref="ScheduledBatches" /> and not here.
    /// </summary>
    public List<IReadOnlyList<string>> FiredBatches { get; } = [];

    /// <summary>How many times <see cref="ClearScheduledCommandsAsync" /> was called.</summary>
    public int ClearScheduledCommandsCallCount { get; private set; }

    /// <summary>Every batch passed to <see cref="ExecuteConsoleCommandsAsync" />, in order.</summary>
    public List<IReadOnlyList<string>> ExecutedConsoleCommands { get; } = [];

    /// <summary>
    ///     What immediate console execution does. The default completes immediately; a test can throw
    ///     from here to prove a failing abort command cannot displace the original failure.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task> ConsoleExecBehavior { get; set; } =
        (_, _) => Task.CompletedTask;

    /// <summary>The tick ranges passed to <see cref="PlayTickRangeAsync" />, in order.</summary>
    public List<(int StartTick, int EndTick, TimeSpan? Timeout)> PlayedRanges { get; } = [];

    /// <summary>
    ///     What playback does. The default completes immediately; a test can hang (observing the token),
    ///     throw, or trigger a backend fault from here.
    /// </summary>
    public Func<int, int, TimeSpan?, CancellationToken, Task> PlaybackBehavior { get; set; } =
        (_, _, _, _) => Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlySet<string> Capabilities => AdvertisedCapabilities;

    /// <inheritdoc />
    public IReadOnlySet<string> PluginCapabilities => AdvertisedCapabilities;

    /// <inheritdoc />
    public bool Supports(string capability) => AdvertisedCapabilities.Contains(capability);

    /// <inheritdoc />
    public long? LastObservedTick { get; set; }

    /// <inheritdoc />
    public string? SessionId { get; set; } = "fake-session";

    /// <inheritdoc />
    public long? LastTick => LastObservedTick;

    /// <inheritdoc />
    public bool? LastTickIsPaused => null;

    /// <inheritdoc />
    public Cs2ProcessStatus? LastProcessStatus => null;

    /// <inheritdoc />
    public DemoPlaybackStatusChange? LastDemoPlaybackStatusChange => null;

    /// <inheritdoc />
    public DemoState? LastDemoState => null;

    /// <inheritdoc />
    public float? LastKnownTimescale => null;

    /// <inheritdoc />
    public event Action<long>? TickObserved;

    /// <inheritdoc />
    public event Action<string, long>? TickUpdated;

    // These telemetry events exist only to satisfy the interface: the clip capture coordinator does
    // not subscribe to any of them, so nothing in this fake ever raises them.
#pragma warning disable CS0067

    /// <inheritdoc />
    public event Func<string, DemoPlaybackStatusChange, Task>? DemoPlaybackStatusChanged;

    /// <inheritdoc />
    public event Func<string, DemoState, Task>? DemoStateChanged;

    /// <inheritdoc />
    public event Func<string, GameEventTelemetry, Task>? GameEventReceived;

    /// <inheritdoc />
    public event Func<string, Cs2ProcessStatus, Task>? ProcessStatusChanged;

    /// <inheritdoc />
    public event Func<string, Task>? Started;

    /// <inheritdoc />
    public event Func<string, long?, DateTimeOffset, Task>? Ended;

#pragma warning restore CS0067

    /// <inheritdoc />
    public Task ScheduleCommandsAtTickAsync(int demoTick, IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.TickScheduledCommands))
        {
            throw new NotSupportedException("tick-scheduled-commands is not advertised");
        }

        ScheduledBatches.Add((demoTick, consoleCommands));
        _armed.Add((demoTick, consoleCommands));
        _journal?.Add($"schedule@{demoTick}:{string.Join(",", consoleCommands)}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearScheduledCommandsAsync(CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.TickScheduledCommands))
        {
            throw new NotSupportedException("tick-scheduled-commands is not advertised");
        }

        ClearScheduledCommandsCallCount++;
        _armed.Clear();
        _journal?.Add("clear-scheduled");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ExecuteConsoleCommandsAsync(IReadOnlyList<string> consoleCommands,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(CsvgCapabilities.ConsoleExec))
        {
            throw new NotSupportedException("console-exec is not advertised");
        }

        ExecutedConsoleCommands.Add(consoleCommands);
        _journal?.Add($"console-exec:{string.Join(",", consoleCommands)}");
        return ConsoleExecBehavior(consoleCommands, cancellationToken);
    }

    /// <inheritdoc />
    public Task PlayTickRangeAsync(int startTick, int endTick, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        PlayedRanges.Add((startTick, endTick, timeout));
        _journal?.Add($"play:{startTick}-{endTick}");
        return PlaybackBehavior(startTick, endTick, timeout, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     Observes a demo tick the way the plugin does, then raises <see cref="TickObserved" /> and
    ///     <see cref="TickUpdated" />. Batches armed inside the half-open interval
    ///     (last observed tick, <paramref name="tick" />] fire, lowest tick first and registration order
    ///     within a tick, and are consumed. Movement that is backward or standing still fires nothing at
    ///     all - which is exactly what leaves a batch armed at or below the last observation unreachable
    ///     for the rest of the demo (<c>Cs2PluginClient.cpp:543</c>).
    /// </summary>
    public void RaiseTickObserved(long tick)
    {
        long previous = LastObservedTick ?? -1;
        LastObservedTick = tick;

        if (tick > previous)
        {
            List<(int DemoTick, IReadOnlyList<string> Commands)> due =
            [
                .. _armed.Where(batch => batch.DemoTick > previous && batch.DemoTick <= tick)
                    .OrderBy(batch => batch.DemoTick)
            ];
            _armed.RemoveAll(batch => batch.DemoTick > previous && batch.DemoTick <= tick);

            foreach ((int demoTick, IReadOnlyList<string> commands) in due)
            {
                FiredBatches.Add(commands);
                _journal?.Add($"fire@{demoTick}:{string.Join(",", commands)}");
            }
        }

        TickObserved?.Invoke(tick);
        TickUpdated?.Invoke(SessionId ?? "fake-session", tick);
    }

    #region Demo control

    /// <summary>
    ///     Opens the demo-control surface only the compilation runner drives (loading and closing a
    ///     demo). Off by default, which keeps the guard the coordinator tests rely on: a per-clip
    ///     recipe configures the clip it is recording and drives playback, and must never move the
    ///     demo out from under the caller that positioned it.
    /// </summary>
    public bool AllowDemoControl { get; set; }

    /// <summary>Demo paths passed to <see cref="LoadDemoAsync(string, bool, CancellationToken)" />, in order.</summary>
    public List<string> LoadedDemos { get; } = [];

    /// <summary>How many times <see cref="CloseDemoAsync" /> was called.</summary>
    public int CloseDemoCallCount { get; private set; }

    /// <summary>Options passed to <see cref="SetDemoOptionsAsync" />, in order.</summary>
    public List<Cs2ClipOptions> DemoOptionsSet { get; } = [];

    /// <summary>Player names passed to <see cref="SetSpectatorTargetAsync" />, in order.</summary>
    public List<string> SpectatorTargetsSet { get; } = [];

    /// <summary>
    ///     What a demo load does after being recorded. The default succeeds; a test can throw from here
    ///     to fail one load.
    /// </summary>
    public Func<string, Task> LoadDemoBehavior { get; set; } = _ => Task.CompletedTask;

    /// <inheritdoc />
    public Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken = default) =>
        LoadDemoAsync(demoPath, interactiveDemoUi: false, cancellationToken);

    /// <inheritdoc />
    public Task LoadDemoAsync(string demoPath, bool interactiveDemoUi,
        CancellationToken cancellationToken = default)
    {
        RequireDemoControl();
        LoadedDemos.Add(demoPath);
        _journal?.Add($"loadDemo:{demoPath}");
        return LoadDemoBehavior(demoPath);
    }

    /// <inheritdoc />
    public Task CloseDemoAsync(CancellationToken cancellationToken = default)
    {
        RequireDemoControl();
        CloseDemoCallCount++;
        _journal?.Add("closeDemo");
        return Task.CompletedTask;
    }

    // Per-clip engine setup belongs to the coordinator's recipe, so unlike the rest of the demo-control
    // surface these two are always open: the guard exists to keep a per-clip recipe from driving the
    // demo (loading, closing, seeking, pausing), not from configuring the clip it is about to record.

    /// <summary>
    ///     What setting the spectator target does after being recorded. The default succeeds; a test can
    ///     throw from here to fail one clip's engine setup.
    /// </summary>
    public Func<string, Task> SpectatorTargetBehavior { get; set; } = _ => Task.CompletedTask;

    /// <inheritdoc />
    public Task SetSpectatorTargetAsync(string playerName, CancellationToken cancellationToken = default)
    {
        SpectatorTargetsSet.Add(playerName);
        _journal?.Add($"setSpectatorTarget:{playerName}");
        return SpectatorTargetBehavior(playerName);
    }

    /// <inheritdoc />
    public Task SetDemoOptionsAsync(Cs2ClipOptions options, CancellationToken cancellationToken = default)
    {
        DemoOptionsSet.Add(options);
        _journal?.Add($"setDemoOptions:{options}");
        return Task.CompletedTask;
    }

    private void RequireDemoControl()
    {
        if (!AllowDemoControl)
        {
            throw Unexpected();
        }
    }

    /// <inheritdoc />
    public Task PauseDemoAsync(CancellationToken cancellationToken = default) => throw Unexpected();

    /// <inheritdoc />
    public Task ResumeDemoAsync(CancellationToken cancellationToken = default) => throw Unexpected();

    /// <inheritdoc />
    public Task SetDemoTickAsync(int tick, CancellationToken cancellationToken = default) => throw Unexpected();

    /// <inheritdoc />
    public Task<SeekResult> SetDemoTickAsync(int demoTick, bool? pauseAfterSeek, bool waitForCompletion,
        CancellationToken cancellationToken = default) => throw Unexpected();

    /// <inheritdoc />
    public Task SetTickUpdateFrequencyAsync(int frequency, CancellationToken cancellationToken = default) =>
        throw Unexpected();

    /// <inheritdoc />
    public Task SetDemoTimescaleAsync(float timescale, CancellationToken cancellationToken = default) =>
        throw Unexpected();

    /// <inheritdoc />
    public Task<DemoState> QueryDemoStateAsync(CancellationToken cancellationToken = default) => throw Unexpected();

    /// <inheritdoc />
    public Task SetGameEventSubscriptionAsync(bool enabled, IReadOnlyList<string>? eventNames = null,
        CancellationToken cancellationToken = default) => throw Unexpected();

    private static NotSupportedException Unexpected() =>
        new("Demo control is disabled on this fake (set AllowDemoControl to drive it); a per-clip " +
            "capture recipe must never touch it.");

    #endregion
}

/// <summary>
///     An <see cref="ICaptureSession" /> whose clips record the order their hooks were called in and
///     can be scripted to fail at any point.
/// </summary>
public sealed class FakeCaptureSession : ICaptureSession
{
    private readonly CaptureJournal _journal;

    /// <summary>Creates the fake, recording into <paramref name="journal" />.</summary>
    public FakeCaptureSession(CaptureJournal journal) =>
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    /// <inheritdoc />
    public string ProviderName { get; set; } = "Fake";

    /// <inheritdoc />
    public CaptureTraits Traits { get; set; } = new()
    {
        TimingModel = CaptureTimingModel.TickExact,
        UsesEngine = true,
        HonorsFrameRate = true
    };

    /// <summary>The tick plan handed to every clip this session creates.</summary>
    public TickCommandPlan? TickPlan { get; set; }

    /// <summary>The result <see cref="FakeClipCapture.FinalizeAsync" /> returns, when it succeeds.</summary>
    public CaptureResult? FinalizeResult { get; set; }

    /// <summary>When set, <see cref="BeginClipAsync" /> throws it.</summary>
    public Exception? BeginClipFailure { get; set; }

    /// <summary>When set, <see cref="FakeClipCapture.FinalizeAsync" /> throws it.</summary>
    public Exception? FinalizeFailure { get; set; }

    /// <summary>
    ///     When set, <see cref="FakeClipCapture.AbortAsync" /> throws it - a backend breaking the
    ///     non-throwing abort contract, which must not replace the original failure.
    /// </summary>
    public Exception? ClipAbortFailure { get; set; }

    /// <summary>The clips handed out so far, in order.</summary>
    public List<FakeClipCapture> Clips { get; } = [];

    /// <summary>How many times <see cref="InitializeAsync" /> ran.</summary>
    public int InitializeCallCount { get; private set; }

    /// <summary>How many times <see cref="ShutdownAsync" /> ran.</summary>
    public int ShutdownCallCount { get; private set; }

    /// <summary>How many times <see cref="DisposeAsync" /> ran.</summary>
    public int DisposeCallCount { get; private set; }

    /// <summary>Demo paths passed to <see cref="OnDemoLoadedAsync" />.</summary>
    public List<string> DemosLoaded { get; } = [];

    /// <inheritdoc />
    public event EventHandler<CaptureProgressEventArgs>? ProgressUpdated;

    /// <inheritdoc />
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCallCount++;
        _journal.Add("session.initialize");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnDemoLoadedAsync(string demoFilePath, CancellationToken cancellationToken = default)
    {
        DemosLoaded.Add(demoFilePath);
        _journal.Add($"session.demoLoaded:{demoFilePath}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IClipCapture> BeginClipAsync(ClipCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        _journal.Add("beginClip");

        if (BeginClipFailure != null)
        {
            throw BeginClipFailure;
        }

        FakeClipCapture clip = new(this, context, _journal);
        Clips.Add(clip);
        return Task.FromResult<IClipCapture>(clip);
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ShutdownCallCount++;
        _journal.Add("session.shutdown");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _journal.Add("session.dispose");
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises <see cref="ProgressUpdated" />.</summary>
    public void RaiseProgress(CaptureProgressEventArgs args) => ProgressUpdated?.Invoke(this, args);

    /// <summary>Raises <see cref="ErrorOccurred" />.</summary>
    public void RaiseError(CaptureErrorEventArgs args) => ErrorOccurred?.Invoke(this, args);
}

/// <summary>
///     A clip that records its hook order and can be scripted to fault mid-playback through
///     <see cref="ClipCaptureContext.FailClip" />.
/// </summary>
public sealed class FakeClipCapture : IClipCapture
{
    private readonly ClipCaptureContext _context;
    private readonly CaptureJournal _journal;
    private readonly FakeCaptureSession _session;

    internal FakeClipCapture(FakeCaptureSession session, ClipCaptureContext context, CaptureJournal journal)
    {
        _session = session;
        _context = context;
        _journal = journal;
    }

    /// <inheritdoc />
    public TickCommandPlan? TickPlan => _session.TickPlan;

    /// <summary>The clip plan this capture was created for, so a test can assert what it was handed.</summary>
    public ClipCaptureContext Context => _context;

    /// <summary>Abort reasons this clip was aborted with, in order.</summary>
    public List<CaptureAbortReason> AbortReasons { get; } = [];

    /// <summary>How many times <see cref="DisposeAsync" /> ran.</summary>
    public int DisposeCallCount { get; private set; }

    /// <inheritdoc />
    public Task OnPlaybackStartingAsync(CancellationToken cancellationToken = default)
    {
        _journal.Add("clip.playbackStarting");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnPlaybackEndedAsync(CancellationToken cancellationToken = default)
    {
        _journal.Add("clip.playbackEnded");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CaptureResult> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        _journal.Add("clip.finalize");

        if (_session.FinalizeFailure != null)
        {
            throw _session.FinalizeFailure;
        }

        return Task.FromResult(_session.FinalizeResult ?? new CaptureResult
        {
            Success = true,
            OutputFilePath = _context.Metadata.GetFullOutputPath(),
            Duration = TimeSpan.FromSeconds(2)
        });
    }

    /// <inheritdoc />
    public Task AbortAsync(CaptureAbortReason reason, CancellationToken cancellationToken = default)
    {
        AbortReasons.Add(reason);
        _journal.Add($"clip.abort:{reason}");

        if (_session.ClipAbortFailure != null)
        {
            throw _session.ClipAbortFailure;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _journal.Add("clip.dispose");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     An <see cref="ICaptureSessionFactory" /> that hands out <see cref="FakeCaptureSession" />s, so a
///     host-registered factory can stand in for OBS in tests that never touch a real backend.
/// </summary>
public sealed class FakeCaptureSessionFactory : ICaptureSessionFactory
{
    /// <summary>Every session created, in order - one per CS2 run.</summary>
    public List<FakeCaptureSession> CreatedSessions { get; } = [];

    /// <summary>The engine contexts <see cref="Create" /> was called with, in order.</summary>
    public List<CaptureSessionContext> Contexts { get; } = [];

    /// <summary>Journal shared by every session this factory creates.</summary>
    public CaptureJournal Journal { get; } = new();

    /// <summary>How many times availability was checked.</summary>
    public int CheckAvailabilityCallCount { get; private set; }

    /// <summary>What availability reports; available by default.</summary>
    public CaptureAvailabilityResult Availability { get; set; } = CaptureAvailabilityResult.Succeeded("fake-1.0");

    /// <inheritdoc />
    public string ProviderName { get; set; } = "Fake";

    /// <inheritdoc />
    public bool IsSupportedOnCurrentPlatform { get; set; } = true;

    /// <inheritdoc />
    public Task<CaptureAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        CheckAvailabilityCallCount++;
        Journal.Add("factory.checkAvailability");
        return Task.FromResult(Availability);
    }

    /// <inheritdoc />
    public ICaptureSession Create(CaptureSessionContext context)
    {
        Contexts.Add(context);
        FakeCaptureSession session = new(Journal) { ProviderName = ProviderName };
        CreatedSessions.Add(session);
        Journal.Add("factory.create");
        return session;
    }
}
