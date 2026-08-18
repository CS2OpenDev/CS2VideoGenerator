#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Tests for <see cref="ClipCaptureCoordinator" />, the single per-clip capture recipe. Driven
///     entirely through fakes - no gRPC, no plugin, no capture backend - because the invariants that
///     matter are ordering ones: every tick bracket armed before playback starts, the spectator
///     re-assert scheduled just ahead of the recording tick, and a failing clip taken down without
///     ever throwing at the caller.
/// </summary>
public class ClipCaptureCoordinatorTests
{
    private const string SpectatedPlayer = "s1mple";

    /// <summary>
    ///     The happy path: begin the clip, arm every batch, start recording, play, stop, finalize -
    ///     in exactly that order. Arming before playback is what makes the plugin's same-tick ordering
    ///     put host batches ahead of its own end-of-range auto-pause.
    /// </summary>
    [Fact]
    public async Task HappyPath_ArmsEveryBatchBeforePlayback_AndRunsHooksInOrder()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan
            {
                Batches =
                [
                    new TickCommandBatch(100, ["host_framerate 60", "startmovie clip_000"]),
                    new TickCommandBatch(200, ["endmovie"])
                ]
            }
        };

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(
        [
            "beginClip",
            "schedule@100:host_framerate 60,startmovie clip_000",
            "schedule@200:endmovie",
            "clip.playbackStarting",
            "play:100-200",
            "clip.playbackEnded",
            "clip.finalize",
            "clip.dispose"
        ], journal.Entries);

        Assert.Equal(0, engine.ClearScheduledCommandsCallCount);
        Assert.Equal([(100, 200, (TimeSpan?)null)], engine.PlayedRanges);
    }

    /// <summary>
    ///     A backend with no tick plan (every screen recorder, OBS included) must have nothing armed on
    ///     its behalf - the coordinator does not invent engine traffic for a provider that never asked
    ///     for any.
    /// </summary>
    [Fact]
    public async Task NullTickPlan_SchedulesNothing()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(engine.ScheduledBatches);
        Assert.Equal(["beginClip", "clip.playbackStarting", "play:100-200", "clip.playbackEnded", "clip.finalize",
            "clip.dispose"], journal.Entries);
    }

    /// <summary>
    ///     The settled degradation rule: an engine-driven backend against a plugin that cannot arm tick
    ///     commands yields a failed result naming the missing capability. Nothing is played, the clip is
    ///     aborted, and no exception escapes - the engine session survives so the caller can carry on.
    /// </summary>
    [Fact]
    public async Task TickPlanWithoutCapability_ReturnsFailedResultNamingCapability()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal); // no capabilities advertised
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan { Batches = [new TickCommandBatch(100, ["startmovie clip_000"])] },
            ProviderName = "InEngine"
        };

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result =
            await coordinator.CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(CsvgCapabilities.TickScheduledCommands, result.ErrorMessage);
        Assert.Contains("InEngine", result.ErrorMessage);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "clip_000.mp4"), result.OutputFilePath);

        Assert.Empty(engine.ScheduledBatches);
        Assert.Empty(engine.PlayedRanges);
        Assert.Equal([CaptureAbortReason.CapabilityMissing], Assert.Single(capture.Clips).AbortReasons);
        Assert.Equal(CaptureState.Error, coordinator.State);

        // The plugin cannot clear schedules either, so the cleanup path must not have tried.
        Assert.Equal(0, engine.ClearScheduledCommandsCallCount);
    }

    /// <summary>
    ///     The per-clip engine setup belongs to the recipe, not to its callers. Both entry points used to
    ///     apply the demo options and the spectator target themselves and then hand the clip to a
    ///     coordinator that scheduled the re-assert for that same target - one recipe written in two
    ///     places, with the half that keeps the target through the seek in a third. The whole sequence is
    ///     now asserted here, in order, on the engine the coordinator was given.
    /// </summary>
    [Fact]
    public async Task AppliesTheClipsEngineSetup_BeforeArmingAndPlayingIt()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan { Batches = [new TickCommandBatch(100, ["startmovie clip_000"])] }
        };

        CaptureResult result = await new ClipCaptureCoordinator().CaptureClipAsync(
            engine, capture,
            Context(spectatorTarget: SpectatedPlayer, clipOptions: Cs2ClipOptions.XrayEnabled),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        // Applied exactly once each: a caller repeating either of these is the duplication this owns.
        Assert.Equal([Cs2ClipOptions.XrayEnabled], engine.DemoOptionsSet);
        Assert.Equal([SpectatedPlayer], engine.SpectatorTargetsSet);

        // Options and target first, then the backend's brackets, then the re-assert that has to survive
        // the seek, and only then playback.
        Assert.Equal(
        [
            $"setDemoOptions:{Cs2ClipOptions.XrayEnabled}",
            $"setSpectatorTarget:{SpectatedPlayer}",
            "beginClip",
            "schedule@100:startmovie clip_000",
            $"schedule@99:spec_player \"{SpectatedPlayer}\"",
            "clip.playbackStarting",
            "play:100-200"
        ], journal.Entries.Take(7));
    }

    /// <summary>
    ///     A clip that names no spectator target leaves the engine's current one alone rather than
    ///     blanking it. The compilation path used to send an empty target for such a clip and then get
    ///     no re-assert for it, because the re-assert has always read a blank target as "no target" -
    ///     one reading now, in one place.
    /// </summary>
    [Fact]
    public async Task ABlankSpectatorTarget_SetsNothingAndSchedulesNothing()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        RecordingLogger<ClipCaptureCoordinator> logger = new();
        CaptureResult result = await new ClipCaptureCoordinator(logger).CaptureClipAsync(
            engine, capture, Context(spectatorTarget: "   "), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(engine.SpectatorTargetsSet);
        Assert.Empty(engine.ScheduledBatches);
        Assert.Single(engine.PlayedRanges);

        // Nothing was asked for, so nothing was dropped: the warning belongs to the clip that named a
        // player and did not get its re-assert, not to every clip that named nobody.
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    ///     A clip carrying no options is not the same as a clip carrying the default ones: the engine
    ///     keeps whatever it was last told, which is what makes the single-clip path usable for a caller
    ///     that set the options once for a whole run.
    /// </summary>
    [Fact]
    public async Task AClipWithoutOptions_LeavesTheEnginesDemoOptionsAlone()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        CaptureResult result = await new ClipCaptureCoordinator().CaptureClipAsync(
            engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(engine.DemoOptionsSet);
    }

    /// <summary>
    ///     Engine setup is part of the clip, so a failure applying it fails the clip the way every other
    ///     capture problem does - a failed result carrying the reason, the state machine driven to Error,
    ///     and the CS2 session left alive - instead of escaping as an exception the way it did while each
    ///     caller applied it before entering this method.
    /// </summary>
    [Fact]
    public async Task WhenTheEngineRefusesTheSetup_TheClipFailsInsteadOfThrowing()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };
        ClipCaptureCoordinator coordinator = new();

        List<CaptureState> states = [];
        coordinator.StateChanged += (_, args) => states.Add(args.NewState);

        engine.SpectatorTargetBehavior = _ => throw new InvalidOperationException("no active session");

        CaptureResult result = await coordinator.CaptureClipAsync(
            engine, capture, Context(spectatorTarget: SpectatedPlayer), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no active session", result.ErrorMessage);
        Assert.Equal([CaptureState.PreparingEnvironment, CaptureState.Error], states);

        // The clip never began, so nothing was armed and nothing played.
        Assert.Empty(engine.ScheduledBatches);
        Assert.Empty(engine.PlayedRanges);
    }

    /// <summary>
    ///     The CSDM 1238 guard: when the caller knows who is being spectated, the target is re-asserted
    ///     from the engine thread one tick before recording starts. It is scheduled by the coordinator
    ///     rather than by a backend's tick plan precisely so screen recorders get it too - note this clip
    ///     has no tick plan at all.
    /// </summary>
    [Fact]
    public async Task SpectatorTarget_SchedulesReassertOneTickBeforeRecording()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        CaptureResult result = await new ClipCaptureCoordinator().CaptureClipAsync(
            engine, capture, Context(spectatorTarget: SpectatedPlayer), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        (int DemoTick, IReadOnlyList<string> Commands) batch = Assert.Single(engine.ScheduledBatches);
        Assert.Equal(99, batch.DemoTick);
        Assert.Equal([$"spec_player \"{SpectatedPlayer}\""], batch.Commands);

        // Still armed before playback.
        Assert.True(journal.Entries.ToList().IndexOf("schedule@99:spec_player \"s1mple\"") <
                    journal.Entries.ToList().IndexOf("play:100-200"));
    }

    /// <summary>
    ///     A clip starting at tick 0 has no earlier tick to fire on, and the engine rejects a negative
    ///     tick outright, so the re-assert clamps onto the recording tick instead of failing the clip.
    /// </summary>
    [Fact]
    public async Task SpectatorTarget_ClipStartingAtTickZero_ClampsReassertToTickZero()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        CaptureResult result = await new ClipCaptureCoordinator().CaptureClipAsync(
            engine, capture, Context(startTick: 0, endTick: 64, spectatorTarget: SpectatedPlayer),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, Assert.Single(engine.ScheduledBatches).DemoTick);
    }

    /// <summary>
    ///     Without the tick-scheduling capability the re-assert cannot be armed. It is an optimization
    ///     for a known engine quirk rather than a requirement, so an older plugin must still record the
    ///     clip - but the clip named a player and the one thing that keeps that target through the seek
    ///     was dropped, which is a degraded recording and has to be said out loud. Same shape as the
    ///     abort path's warning: name the capability, name what could not be run.
    /// </summary>
    [Fact]
    public async Task SpectatorTarget_WithoutCapability_WarnsAndStillRecords()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal); // no capabilities advertised
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        RecordingLogger<ClipCaptureCoordinator> logger = new();
        CaptureResult result = await new ClipCaptureCoordinator(logger).CaptureClipAsync(
            engine, capture, Context(spectatorTarget: SpectatedPlayer), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(engine.ScheduledBatches);
        Assert.Single(engine.PlayedRanges);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains(SpectatedPlayer) &&
            entry.Message.Contains(CsvgCapabilities.TickScheduledCommands));
    }

    /// <summary>
    ///     The fault veto: a backend that dies mid-clip cancels the playback wait instead of leaving it
    ///     to burn the whole playback timeout, and the reported error is the backend's own failure - not
    ///     the bland cancellation it produced.
    /// </summary>
    [Fact]
    public async Task FailClipDuringPlayback_FailsFastAndReportsTheBackendFault()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };
        ClipCaptureContext context = Context();

        engine.PlaybackBehavior = async (_, _, _, ct) =>
        {
            context.FailClip(new InvalidOperationException("the encoder exited with code 1"));

            // Would hang until the (multi-minute) playback timeout without the linked fault token.
            await Task.Delay(Timeout.Infinite, ct);
        };

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result =
            await coordinator.CaptureClipAsync(engine, capture, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("the encoder exited with code 1", result.ErrorMessage);
        Assert.Equal([CaptureAbortReason.BackendFault], Assert.Single(capture.Clips).AbortReasons);
        Assert.Equal(1, engine.ClearScheduledCommandsCallCount);
        Assert.Equal(CaptureState.Error, coordinator.State);
        Assert.Contains("clip.dispose", journal.Entries);
    }

    /// <summary>
    ///     Playback blowing up is a failed result, never an exception, so the CS2 session outlives a bad
    ///     clip. Schedules are dropped so nothing stays armed for the next clip.
    /// </summary>
    [Fact]
    public async Task PlaybackThrows_ReturnsFailedResult_ClearsSchedulesAndAbortsTheClip()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan { Batches = [new TickCommandBatch(100, ["startmovie clip_000"])] }
        };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback never reached tick 200");

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("playback never reached tick 200", result.ErrorMessage);
        Assert.Equal(1, engine.ClearScheduledCommandsCallCount);
        Assert.Equal([CaptureAbortReason.PlaybackFailed], Assert.Single(capture.Clips).AbortReasons);
    }

    [Fact]
    public async Task FinalizeThrows_ReturnsFailedResult()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = null,
            FinalizeFailure = new IOException("the output file never appeared")
        };

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("the output file never appeared", result.ErrorMessage);
    }

    /// <summary>A refused clip is a failed result too, and there is no clip left to abort.</summary>
    [Fact]
    public async Task BeginClipThrows_ReturnsFailedResultWithoutPlayingAnything()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            BeginClipFailure = new InvalidOperationException("OBS is not connected")
        };

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("OBS is not connected", result.ErrorMessage);
        Assert.Empty(engine.PlayedRanges);
        Assert.Empty(capture.Clips);
    }

    /// <summary>
    ///     AbortAsync is contractually non-throwing, but a backend that breaks that must not replace the
    ///     original failure with its own.
    /// </summary>
    [Fact]
    public async Task AbortThrowing_DoesNotMaskTheOriginalFailure()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = null,
            ClipAbortFailure = new InvalidOperationException("abort blew up too")
        };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback stalled");

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("playback stalled", result.ErrorMessage);
        Assert.Equal([CaptureAbortReason.PlaybackFailed], Assert.Single(capture.Clips).AbortReasons);
    }

    /// <summary>
    ///     The abort path's reason for existing: a clip that died mid-playback has left the engine
    ///     mid-recording, and only immediate console execution can undo that - a tick-scheduled restore
    ///     would wait for a tick that is never played. The commands also have to go out before the
    ///     schedules are dropped: clearing first leaves the engine recording for however long the
    ///     clear takes.
    /// </summary>
    [Fact]
    public async Task AbortCommands_AreExecutedImmediately_BeforeSchedulesAreCleared()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands,
            CsvgCapabilities.ConsoleExec);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan
            {
                Batches = [new TickCommandBatch(100, ["startmovie clip_000"])],
                AbortCommands = ["endmovie", "host_framerate 0"]
            }
        };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback stalled");

        RecordingLogger<ClipCaptureCoordinator> logger = new();
        CaptureResult result = await new ClipCaptureCoordinator(logger)
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal([["endmovie", "host_framerate 0"]], engine.ExecutedConsoleCommands);

        IReadOnlyList<string> entries = journal.Entries;
        int executed = entries.ToList().IndexOf("console-exec:endmovie,host_framerate 0");
        int cleared = entries.ToList().IndexOf("clear-scheduled");
        Assert.True(executed >= 0, $"Abort commands were never executed. Journal: {string.Join(" | ", entries)}");
        Assert.True(executed < cleared,
            $"Abort commands must be executed before schedules are cleared. Journal: {string.Join(" | ", entries)}");
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    ///     Abort commands run on a path where the clip's own token is usually already cancelled (the
    ///     backend fault cancels the playback wait), so they must not inherit it - a cancelled restore
    ///     is exactly the failure this path exists to prevent.
    /// </summary>
    [Fact]
    public async Task AbortCommands_AreExecutedEvenWhenTheClipWasCancelled()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands,
            CsvgCapabilities.ConsoleExec);
        List<bool> observedCancellation = [];
        engine.ConsoleExecBehavior = (_, token) =>
        {
            observedCancellation.Add(token.IsCancellationRequested);
            return Task.CompletedTask;
        };
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan { Batches = [], AbortCommands = ["endmovie"] }
        };

        using CancellationTokenSource clipCts = new();
        engine.PlaybackBehavior = (_, _, _, token) =>
        {
            clipCts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        CaptureResult result = await new ClipCaptureCoordinator()
            .CaptureClipAsync(engine, capture, Context(), clipCts.Token);

        Assert.False(result.Success);
        Assert.Equal([["endmovie"]], engine.ExecutedConsoleCommands);
        Assert.Equal([false], observedCancellation);
    }

    /// <summary>
    ///     A plugin without "console-exec" cannot restore anything, and a backend that published abort
    ///     commands has to be told loudly rather than silently ignored - that is a real degraded state,
    ///     not a hypothetical one.
    /// </summary>
    [Fact]
    public async Task AbortCommands_WithoutConsoleExec_AreLoggedAsAWarning()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan
            {
                Batches = [new TickCommandBatch(100, ["startmovie clip_000"])],
                AbortCommands = ["endmovie"]
            }
        };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback stalled");

        RecordingLogger<ClipCaptureCoordinator> logger = new();
        CaptureResult result = await new ClipCaptureCoordinator(logger)
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(engine.ExecutedConsoleCommands);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("endmovie") &&
            entry.Message.Contains(CsvgCapabilities.ConsoleExec));
    }

    /// <summary>
    ///     Abort commands are best effort: the clip is already failing, and a restore that throws must
    ///     not replace the failure the caller needs to see.
    /// </summary>
    [Fact]
    public async Task AbortCommandsThrowing_DoNotMaskTheOriginalFailure()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands,
            CsvgCapabilities.ConsoleExec);
        engine.ConsoleExecBehavior = (_, _) => throw new InvalidOperationException("console send failed");
        FakeCaptureSession capture = new(journal)
        {
            TickPlan = new TickCommandPlan { Batches = [], AbortCommands = ["endmovie"] }
        };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback stalled");

        RecordingLogger<ClipCaptureCoordinator> logger = new();
        CaptureResult result = await new ClipCaptureCoordinator(logger)
            .CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("playback stalled", result.ErrorMessage);
        Assert.Equal([["endmovie"]], engine.ExecutedConsoleCommands);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("abort command"));
    }

    /// <summary>
    ///     The per-clip state machine drives host progress reporting (the CLI maps these states to clip
    ///     phases), so the sequence is part of the contract.
    /// </summary>
    [Fact]
    public async Task StateTransitions_FollowThePerClipSequence()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };

        ClipCaptureCoordinator coordinator = new();
        List<CaptureState> states = [];
        coordinator.StateChanged += (_, e) => states.Add(e.NewState);

        await coordinator.CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            CaptureState.PreparingEnvironment,
            CaptureState.Capturing,
            CaptureState.Stopping,
            CaptureState.Finalizing,
            CaptureState.Ready
        ], states);
        Assert.Equal(CaptureState.Ready, coordinator.State);
    }

    /// <summary>
    ///     A failed clip is announced on <see cref="ClipCaptureCoordinator.ErrorOccurred" /> as well as
    ///     returned, so hosts watching the diagnostic channel see it.
    /// </summary>
    [Fact]
    public async Task FailedClip_RaisesErrorOccurred()
    {
        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal, CsvgCapabilities.TickScheduledCommands);
        FakeCaptureSession capture = new(journal) { TickPlan = null };
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback stalled");

        ClipCaptureCoordinator coordinator = new();
        List<CaptureErrorEventArgs> errors = [];
        coordinator.ErrorOccurred += (_, e) => errors.Add(e);

        await coordinator.CaptureClipAsync(engine, capture, Context(), TestContext.Current.CancellationToken);

        CaptureErrorEventArgs error = Assert.Single(errors);
        Assert.Contains("playback stalled", error.ErrorMessage);
        Assert.IsType<TimeoutException>(error.Exception);
    }

    #region Helpers

    private static FakeEngineSession Engine(CaptureJournal journal, params string[] capabilities)
    {
        FakeEngineSession engine = new(journal);
        foreach (string capability in capabilities)
        {
            engine.AdvertisedCapabilities.Add(capability);
        }

        return engine;
    }

    private static ClipCaptureContext Context(
        int startTick = 100,
        int endTick = 200,
        string? spectatorTarget = null,
        Cs2ClipOptions? clipOptions = null) => new()
    {
        StartTick = startTick,
        EndTick = endTick,
        Metadata = new CaptureMetadata
        {
            OutputFileName = "clip_000",
            OutputDirectory = Path.GetTempPath()
        },
        SpectatorTarget = spectatorTarget,
        ClipOptions = clipOptions
    };

    /// <summary>
    ///     Captures log entries so a test can assert on a warning the production code emits instead of
    ///     asserting on behavior that does not exist yet.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    #endregion
}
