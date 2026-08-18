#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     The one place a clip is recorded. Holds no per-clip state between calls (everything a clip
///     needs arrives in <see cref="ClipCaptureContext" />), so a single instance serves every session
///     and every provider, and the ordering invariants below exist exactly once.
/// </summary>
/// <remarks>
///     <para>
///         The coordinator is the only component holding the full <see cref="ICs2EngineSession" />:
///         applying the clip's engine setup (demo options, spectator target), arming tick brackets and
///         driving playback are its job, which is why capture backends only ever see
///         <see cref="IEngineConsole" />. Callers hand over a clip and get a result; there is no
///         engine preparation left for them to repeat.
///     </para>
///     <para>
///         Capture problems are reported as failed <see cref="CaptureResult" />s, never as exceptions,
///         so a bad clip never takes the CS2 session down with it.
///     </para>
/// </remarks>
public sealed partial class ClipCaptureCoordinator
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private CaptureState _state = CaptureState.NotInitialized;

    /// <summary>
    ///     Creates the coordinator. A null logger is supported and falls back to a no-op logger.
    /// </summary>
    public ClipCaptureCoordinator(ILogger<ClipCaptureCoordinator>? logger = null) =>
        _logger = logger ?? NullLogger<ClipCaptureCoordinator>.Instance;

    /// <summary>
    ///     The most recent state of the clip being recorded, or
    ///     <see cref="CaptureState.NotInitialized" /> before the first clip. Per-clip transitions run
    ///     <see cref="CaptureState.PreparingEnvironment" /> to <see cref="CaptureState.Capturing" /> to
    ///     <see cref="CaptureState.Stopping" /> to <see cref="CaptureState.Finalizing" /> and back to
    ///     <see cref="CaptureState.Ready" />, or to <see cref="CaptureState.Error" /> when the clip
    ///     fails.
    /// </summary>
    public CaptureState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    ///     Raised on every <see cref="State" /> transition.
    /// </summary>
    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    /// <summary>
    ///     Raised when a clip fails. Backend-level problems are reported by the capture session's own
    ///     <see cref="ICaptureSession.ErrorOccurred" />; this event covers orchestration failures
    ///     (playback, arming, a vetoed clip).
    /// </summary>
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    ///     Records one clip: arm the backend's engine brackets, play the tick range, finalize.
    /// </summary>
    /// <param name="session">
    ///     The engine session that owns playback and tick scheduling.
    /// </param>
    /// <param name="captureSession">The initialized capture session for this engine session.</param>
    /// <param name="context">
    ///     The clip plan. Single-use: its fault token is disposed before this method returns.
    /// </param>
    /// <param name="cancellationToken">Cancels the clip.</param>
    /// <returns>
    ///     The clip's result. A failed result carries the reason in
    ///     <see cref="CaptureResult.ErrorMessage" />; this method does not throw for capture-level
    ///     problems.
    /// </returns>
    public async Task<CaptureResult> CaptureClipAsync(
        ICs2EngineSession session,
        ICaptureSession captureSession,
        ClipCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(captureSession);
        ArgumentNullException.ThrowIfNull(context);

        using Activity? activity = CsvgTracing.StartActivity("ClipCaptureCoordinator.CaptureClip",
            ActivityKind.Internal);
        activity?.SetTag("csvg.capture.provider", captureSession.ProviderName);
        activity?.SetTag("csvg.clip.index", context.ClipIndex);
        activity?.SetTag("csvg.tick.start", context.StartTick);
        activity?.SetTag("csvg.tick.end", context.EndTick);
        activity?.SetTag("csvg.capture.output", context.Metadata.GetFullOutputPath());

        IClipCapture? clip = null;
        CaptureAbortReason abortReason = CaptureAbortReason.PlaybackFailed;

        try
        {
            SetState(captureSession, CaptureState.PreparingEnvironment);
            await ApplyClipEngineSetupAsync(session, context, cancellationToken).ConfigureAwait(false);
            clip = await captureSession.BeginClipAsync(context, cancellationToken).ConfigureAwait(false);

            TickCommandPlan? plan = clip.TickPlan;
            activity?.SetTag("csvg.capture.tickPlan.batches", plan?.Batches.Count ?? 0);

            // The settled degradation rule: a backend that needs engine work done cannot run against a
            // plugin that cannot do it. Fail the clip naming the capability - the engine session stays
            // alive, so the caller can carry on with a different provider or an older plugin. The
            // tick-scheduling check goes first because it is the one every engine-driven backend needs.
            if (plan != null && !session.Supports(CsvgCapabilities.TickScheduledCommands))
            {
                abortReason = CaptureAbortReason.CapabilityMissing;
                throw CapabilityGateFailure(captureSession.ProviderName, CsvgCapabilities.TickScheduledCommands,
                    "engine-scheduled commands", activity);
            }

            if (plan is { SetupCommands.Count: > 0 } && !session.Supports(CsvgCapabilities.ConsoleExec))
            {
                abortReason = CaptureAbortReason.CapabilityMissing;
                throw CapabilityGateFailure(captureSession.ProviderName, CsvgCapabilities.ConsoleExec,
                    "immediate console execution for the engine state its clips need before recording",
                    activity);
            }

            // Arm every batch before playback: registration is acknowledged (so the returned task
            // means "armed"), and the plugin's same-tick ordering then runs host batches ahead of its
            // own end-of-range auto-pause. Arming after playback started would race that pause.
            if (plan != null)
            {
                foreach (TickCommandBatch batch in plan.Batches)
                {
                    await session
                        .ScheduleCommandsAtTickAsync(batch.DemoTick, batch.Commands, cancellationToken)
                        .ConfigureAwait(false);
                    LogScheduledBatch(batch.DemoTick, batch.Commands.Count);
                }

                // Setup goes out immediately rather than on a tick. It only has to be in place before
                // the first batch fires, and a tick would put it behind the playhead whenever the
                // previous clip from this demo ended near where this one starts - the plugin fires
                // batches on (last observed tick, current tick], and anything at or below that
                // observation is stranded for good.
                if (plan.SetupCommands.Count > 0)
                {
                    await session.ExecuteConsoleCommandsAsync(plan.SetupCommands, cancellationToken)
                        .ConfigureAwait(false);
                    LogExecutedSetupCommands(plan.SetupCommands.Count);
                }
            }

            await ScheduleSpectatorReassertAsync(session, context, activity, cancellationToken).ConfigureAwait(false);

            await clip.OnPlaybackStartingAsync(cancellationToken).ConfigureAwait(false);
            SetState(captureSession, CaptureState.Capturing);

            // Link the backend's fault veto into the playback wait: a backend that dies mid-clip
            // cancels this in milliseconds instead of leaving the wait to burn its whole timeout.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, context.FaultToken.Token);
            await session
                .PlayTickRangeAsync(context.StartTick, context.EndTick, context.PlaybackTimeout, linked.Token)
                .ConfigureAwait(false);

            SetState(captureSession, CaptureState.Stopping);
            await clip.OnPlaybackEndedAsync(cancellationToken).ConfigureAwait(false);

            SetState(captureSession, CaptureState.Finalizing);
            CaptureResult result = await clip.FinalizeAsync(cancellationToken).ConfigureAwait(false);

            SetState(captureSession, CaptureState.Ready);

            activity?.SetTag("csvg.capture.success", result.Success);
            activity?.SetTag("csvg.capture.outputPath", result.OutputFilePath);
            activity?.SetTag("csvg.capture.durationMs", result.Duration.TotalMilliseconds);
            LogClipCompleted(context.ClipIndex, result.Success, result.OutputFilePath);

            return result;
        }
        catch (Exception ex)
        {
            // A recorded fault outranks the exception it produced: cancelling the playback wait
            // surfaces as an OperationCanceledException, which says nothing about why the backend died.
            Exception effective = context.FaultToken.Fault ?? ex;
            if (context.FaultToken.Fault != null)
            {
                abortReason = CaptureAbortReason.BackendFault;
            }
            else if (ex is OperationCanceledException)
            {
                abortReason = CaptureAbortReason.Cancelled;
            }

            LogClipFailed(context.ClipIndex, abortReason, effective);
            activity?.SetTag("csvg.capture.error", effective.Message);
            activity?.SetTag("csvg.capture.abortReason", abortReason.ToString());

            // Order matters: the abort commands stop whatever the engine is still doing (recording,
            // a forced framerate), so they go out first; clearing the schedules afterwards only has
            // to prevent the next clip from inheriting unfired batches.
            await ExecuteAbortCommandsAsync(session, clip?.TickPlan, captureSession.ProviderName)
                .ConfigureAwait(false);
            await ClearScheduledCommandsAsync(session).ConfigureAwait(false);

            if (clip != null)
            {
                await AbortClipAsync(clip, abortReason).ConfigureAwait(false);
            }

            SetState(captureSession, CaptureState.Error);
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs(
                $"Capture failed for clip {context.ClipIndex}: {effective.Message}", effective));
            CsvgMetrics.CaptureSessionCount.Add(1,
                new KeyValuePair<string, object?>("provider", captureSession.ProviderName),
                new KeyValuePair<string, object?>("result", "error"));

            return new CaptureResult
            {
                Success = false,
                OutputFilePath = context.Metadata.GetFullOutputPath(),
                Duration = TimeSpan.Zero,
                ErrorMessage = effective.Message
            };
        }
        finally
        {
            if (clip != null)
            {
                try
                {
                    await clip.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogClipDisposeFailed(ex);
                }
            }

            context.FaultToken.Dispose();
        }
    }

    /// <summary>
    ///     Applies the engine state the clip carries: its demo options, and the spectator target when it
    ///     names one. Both are per-clip engine state rather than backend state, so they belong here next
    ///     to the re-assert that carries the target through the seek; a caller that applied them itself
    ///     would be writing half of this recipe a second time.
    /// </summary>
    /// <remarks>
    ///     A blank spectator target means "whatever the demo is already showing" and is skipped, which
    ///     is the same reading <see cref="ScheduleSpectatorReassertAsync" /> takes: setting an empty
    ///     target and then declining to re-assert it is a contradiction, not a fallback.
    /// </remarks>
    private static async Task ApplyClipEngineSetupAsync(
        ICs2EngineSession session,
        ClipCaptureContext context,
        CancellationToken cancellationToken)
    {
        if (context.ClipOptions is { } clipOptions)
        {
            await session.SetDemoOptionsAsync(clipOptions, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(context.SpectatorTarget))
        {
            await session.SetSpectatorTargetAsync(context.SpectatorTarget, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Builds the failure for a plugin that cannot do what the backend's plan needs, and records it
    ///     on the way out. Both gates share it: the message a caller reads has to name the provider, the
    ///     capability and the way out, whichever half of the plan was unrunnable.
    /// </summary>
    private NotSupportedException CapabilityGateFailure(string providerName, string capability, string what,
        Activity? activity)
    {
        LogCapabilityGateFailed(providerName, capability);
        activity?.SetTag("csvg.capture.capabilityMissing", capability);

        return new NotSupportedException(
            $"Capture provider '{providerName}' requires {what}, but the connected plugin does not advertise " +
            $"the '{capability}' capability. Update the CSVG plugin, or select a provider that does not drive " +
            "the engine.");
    }

    /// <summary>
    ///     Re-asserts the spectator target one tick before recording starts, when the caller knows who
    ///     is being spectated and the plugin can schedule commands. Seeking to the clip's start tick
    ///     can drop the spectator lock (CSDM issue 1238), and a re-assert fired by the engine itself at
    ///     the last moment is the only thing that reliably survives it. Screen recorders benefit too,
    ///     which is why this is not part of a backend's own tick plan.
    /// </summary>
    private async Task ScheduleSpectatorReassertAsync(
        ICs2EngineSession session,
        ClipCaptureContext context,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.SpectatorTarget))
        {
            return;
        }

        // Not schedulable is not the same as not wanted: the clip named a player, and dropping the
        // one thing that carries that target through the seek is a degraded recording rather than a
        // no-op. Say so, the way the abort path does.
        if (!session.Supports(CsvgCapabilities.TickScheduledCommands))
        {
            LogSpectatorReassertNotSchedulable(context.SpectatorTarget,
                CsvgCapabilities.TickScheduledCommands);
            return;
        }

        // A clip starting at tick 0 has no earlier tick to fire on; clamp instead of failing
        // validation, so the re-assert simply lands on the recording tick itself.
        int reassertTick = Math.Max(0, context.StartTick - 1);
        string command = $"spec_player \"{context.SpectatorTarget}\"";

        await session.ScheduleCommandsAtTickAsync(reassertTick, [command], cancellationToken).ConfigureAwait(false);
        activity?.SetTag("csvg.capture.spectatorReassertTick", reassertTick);
        LogScheduledSpectatorReassert(context.SpectatorTarget, reassertTick);
    }

    /// <summary>
    ///     Drops any not-yet-fired schedules so a failed clip cannot leave commands armed for the
    ///     next one. Best effort: this runs on a path that is already failing.
    /// </summary>
    private async Task ClearScheduledCommandsAsync(ICs2EngineSession session)
    {
        if (!session.Supports(CsvgCapabilities.TickScheduledCommands))
        {
            return;
        }

        try
        {
            await session.ClearScheduledCommandsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogClearScheduledCommandsFailed(ex);
        }
    }

    /// <summary>
    ///     Runs the plan's engine-side undo the moment a clip fails ("console-exec"). This is the one
    ///     thing a tick schedule cannot do: the clip died mid-playback, so a restore armed at a later
    ///     tick would wait for a tick nothing will ever play, leaving the engine recording.
    /// </summary>
    /// <remarks>
    ///     Uncancellable on purpose (the clip's token is usually already cancelled by the time this
    ///     runs, which is how most failures arrive here) and best effort: a restore that throws must
    ///     not replace the failure the caller needs to see. A plugin without the capability cannot
    ///     restore anything, which is a real degraded state and is reported as such.
    /// </remarks>
    private async Task ExecuteAbortCommandsAsync(ICs2EngineSession session, TickCommandPlan? plan,
        string providerName)
    {
        if (plan is null || plan.AbortCommands.Count == 0)
        {
            return;
        }

        if (!session.Supports(CsvgCapabilities.ConsoleExec))
        {
            LogAbortCommandsNotExecutable(providerName, plan.AbortCommands.Count, CsvgCapabilities.ConsoleExec,
                string.Join("; ", plan.AbortCommands));
            return;
        }

        try
        {
            await session.ExecuteConsoleCommandsAsync(plan.AbortCommands, CancellationToken.None)
                .ConfigureAwait(false);
            LogAbortCommandsExecuted(providerName, plan.AbortCommands.Count);
        }
        catch (Exception ex)
        {
            LogAbortCommandsFailed(ex);
        }
    }

    private async Task AbortClipAsync(IClipCapture clip, CaptureAbortReason reason)
    {
        try
        {
            await clip.AbortAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // AbortAsync is contractually non-throwing; a backend that breaks that must still not
            // replace the original failure with its own.
            LogClipAbortFailed(ex);
        }
    }

    private void SetState(ICaptureSession captureSession, CaptureState newState)
    {
        CaptureState previousState;
        lock (_gate)
        {
            if (_state == newState)
            {
                return;
            }

            previousState = _state;
            _state = newState;
        }

        LogStateChanged(previousState, newState);

        if (captureSession is ICaptureStateSink sink)
        {
            try
            {
                sink.OnCaptureStateChanged(newState);
            }
            catch (Exception ex)
            {
                LogStateSinkFailed(ex);
            }
        }

        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(previousState, newState));
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Capture state changed: {Previous} -> {New}")]
    partial void LogStateChanged(CaptureState previous, CaptureState @new);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Armed {CommandCount} scheduled command(s) at tick {DemoTick}")]
    partial void LogScheduledBatch(int demoTick, int commandCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Scheduled spectator re-assert for '{PlayerName}' at tick {DemoTick}")]
    partial void LogScheduledSpectatorReassert(string playerName, int demoTick);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Clip {ClipIndex} finished: success={Success}, output={OutputPath}")]
    partial void LogClipCompleted(int clipIndex, bool success, string outputPath);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Clip {ClipIndex} failed ({Reason})")]
    partial void LogClipFailed(int clipIndex, CaptureAbortReason reason, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message =
            "Capture provider '{ProviderName}' needs engine-scheduled commands, but the plugin does not " +
            "advertise '{Capability}'")]
    partial void LogCapabilityGateFailed(string providerName, string capability);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Could not clear scheduled commands after a failed clip")]
    partial void LogClearScheduledCommandsFailed(Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message =
            "Capture provider '{ProviderName}' asked for {CommandCount} engine-side abort command(s), but the " +
            "connected plugin does not advertise '{Capability}' - the engine keeps whatever state the failed " +
            "clip left it in: {Commands}")]
    partial void LogAbortCommandsNotExecutable(string providerName, int commandCount, string capability,
        string commands);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Clip abort threw; the original failure is reported instead")]
    partial void LogClipAbortFailed(Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Error disposing a clip capture")]
    partial void LogClipDisposeFailed(Exception exception);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "A capture state sink threw; continuing")]
    partial void LogStateSinkFailed(Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "Executed {CommandCount} engine-side abort command(s) for provider '{ProviderName}'")]
    partial void LogAbortCommandsExecuted(string providerName, int commandCount);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "Could not execute the clip's engine-side abort command(s) after a failed clip")]
    partial void LogAbortCommandsFailed(Exception exception);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
        Message =
            "The clip spectates '{PlayerName}', but the connected plugin does not advertise '{Capability}' - " +
            "the spectator re-assert that carries that target through the seek cannot be scheduled, so the " +
            "clip records whatever the demo shows on arrival")]
    partial void LogSpectatorReassertNotSchedulable(string playerName, string capability);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "Executed {CommandCount} immediate setup command(s) before playback")]
    partial void LogExecutedSetupCommands(int commandCount);

    #endregion
}
