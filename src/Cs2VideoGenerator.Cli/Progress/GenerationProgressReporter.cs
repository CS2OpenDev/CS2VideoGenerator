#region

using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Progress;

/// <summary>
///     Runs <see cref="CsvgVideoSession.RunCompilationAsync" /> while rendering live per-clip
///     progress from the session's Core events. Subscriptions are installed around the
///     capture call and removed in a finally.
///     Interactive terminals get a Spectre progress display (overall bar + current-clip bar with
///     phase, persisted per-clip ✓/✗ lines); redirected/CI output gets plain sequential lines.
/// </summary>
public static class GenerationProgressReporter
{
    public static Task<Cs2CompilationResult> CaptureWithProgressAsync(
        IAnsiConsole console,
        CsvgVideoSession session,
        Cs2Compilation compilation,
        CancellationToken cancellationToken)
    {
        GenerationProgressTracker tracker = new(compilation.Clips.Count);

        return console.Profile.Capabilities.Interactive
            ? CaptureInteractiveAsync(console, session, compilation, tracker, cancellationToken)
            : CapturePlainAsync(console, session, compilation, tracker, cancellationToken);
    }

    /// <summary>
    ///     Interactive rendering: a Spectre progress display with an overall bar (clip x/N) and a
    ///     bar per clip. Completed clip rows are re-labelled with their ✓/✗ result line and, with
    ///     AutoClear off, the final frame persists in the scrollback.
    /// </summary>
    private static async Task<Cs2CompilationResult> CaptureInteractiveAsync(
        IAnsiConsole console,
        CsvgVideoSession session,
        Cs2Compilation compilation,
        GenerationProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        Cs2CompilationResult? result = null;

        await console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Guards `current` and keeps tracker-state reads consistent with the task updates.
                object uiGate = new();
                ProgressTask overall = ctx.AddTask(tracker.DescribeOverall(),
                    maxValue: Math.Max(1, tracker.TotalClips));
                ProgressTask? current = null;

                Task OnClipStarted(int clipIndex, Cs2CompilationClip clip)
                {
                    lock (uiGate)
                    {
                        tracker.OnClipStarted(clipIndex, clip);
                        current = ctx.AddTask(tracker.DescribeCurrentClip(), maxValue: 1.0);
                        overall.Description = tracker.DescribeOverall();
                    }

                    return Task.CompletedTask;
                }

                Task OnClipCompleted(int clipIndex, Cs2ClipResult clipResult)
                {
                    lock (uiGate)
                    {
                        // Format before OnClipCompleted clears the current-clip state.
                        string line = tracker.FormatCompletionLine(clipIndex, clipResult);
                        tracker.OnClipCompleted(clipIndex, clipResult);

                        if (current is not null)
                        {
                            if (clipResult.Success)
                            {
                                current.Value = current.MaxValue;
                            }

                            current.Description = line;
                            current.StopTask();
                            current = null;
                        }

                        overall.Increment(1);
                        overall.Description = tracker.DescribeOverall();
                    }

                    return Task.CompletedTask;
                }

                void OnTickUpdated(string _, long tick)
                {
                    if (!tracker.OnTickUpdated(tick))
                    {
                        return;
                    }

                    lock (uiGate)
                    {
                        if (current is not null)
                        {
                            current.Value = tracker.CurrentClipProgress;
                        }
                    }
                }

                void OnCaptureProgress(CaptureProgressEventArgs e)
                {
                    tracker.OnCaptureProgress(e);
                    lock (uiGate)
                    {
                        if (current is not null)
                        {
                            current.Description = tracker.DescribeCurrentClip();
                        }
                    }
                }

                Task OnCaptureStateChanged(CaptureState _, CaptureState newState)
                {
                    if (tracker.OnCaptureStateChanged(newState))
                    {
                        lock (uiGate)
                        {
                            if (current is not null)
                            {
                                current.Description = tracker.DescribeCurrentClip();
                            }
                        }
                    }

                    return Task.CompletedTask;
                }

                Task OnPlaybackStatusChanged(string _, DemoPlaybackStatusChange statusChange)
                {
                    if (tracker.OnDemoPlaybackStatusChanged(statusChange))
                    {
                        lock (uiGate)
                        {
                            if (current is not null)
                            {
                                current.Description = tracker.DescribeCurrentClip();
                            }
                            else
                            {
                                // Demo loads happen between clips - surface them on the overall row.
                                overall.Description = tracker.DescribeOverall();
                            }
                        }
                    }

                    return Task.CompletedTask;
                }

                session.CompilationClipStarted += OnClipStarted;
                session.CompilationClipCompleted += OnClipCompleted;
                session.TickUpdated += OnTickUpdated;
                session.CaptureProgressUpdated += OnCaptureProgress;
                session.CaptureStateChanged += OnCaptureStateChanged;
                session.DemoPlaybackStatusChanged += OnPlaybackStatusChanged;

                try
                {
                    result = await session.RunCompilationAsync(compilation, cancellationToken);
                }
                finally
                {
                    session.CompilationClipStarted -= OnClipStarted;
                    session.CompilationClipCompleted -= OnClipCompleted;
                    session.TickUpdated -= OnTickUpdated;
                    session.CaptureProgressUpdated -= OnCaptureProgress;
                    session.CaptureStateChanged -= OnCaptureStateChanged;
                    session.DemoPlaybackStatusChanged -= OnPlaybackStatusChanged;

                    lock (uiGate)
                    {
                        overall.Description = tracker.DescribeOverall();
                        overall.StopTask();
                        current?.StopTask();
                    }
                }
            });

        return result!;
    }

    /// <summary>
    ///     Non-interactive degradation (piped/redirected/CI): plain sequential lines per event
    ///     instead of live-updating widgets. Phase changes and coarse (>=10%) tick progress are
    ///     printed once each; per-clip ✓/✗ completion lines match the interactive display.
    /// </summary>
    private static async Task<Cs2CompilationResult> CapturePlainAsync(
        IAnsiConsole console,
        CsvgVideoSession session,
        Cs2Compilation compilation,
        GenerationProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        object gate = new();
        int lastPrintedPercent = -1;

        void WriteLine(string markup)
        {
            lock (gate)
            {
                console.MarkupLine(markup);
            }
        }

        Task OnClipStarted(int clipIndex, Cs2CompilationClip clip)
        {
            tracker.OnClipStarted(clipIndex, clip);
            Interlocked.Exchange(ref lastPrintedPercent, -1);
            WriteLine(tracker.FormatClipStartedLine(clipIndex, clip));
            return Task.CompletedTask;
        }

        Task OnClipCompleted(int clipIndex, Cs2ClipResult clipResult)
        {
            // Format before OnClipCompleted clears the current-clip state.
            string line = tracker.FormatCompletionLine(clipIndex, clipResult);
            tracker.OnClipCompleted(clipIndex, clipResult);
            WriteLine(line);
            return Task.CompletedTask;
        }

        void OnTickUpdated(string _, long tick)
        {
            if (!tracker.OnTickUpdated(tick))
            {
                return;
            }

            int percent = (int)(tracker.CurrentClipProgress * 100);
            if (percent - lastPrintedPercent < 10)
            {
                return;
            }

            Interlocked.Exchange(ref lastPrintedPercent, percent);
            WriteLine($"  [dim]{GenerationProgressTracker.PhaseLabel(tracker.Phase)} {percent}%[/]");
        }

        // Elapsed-time updates are tracked but not printed per event - they would flood
        // sequential output. Progress lines come from the coarser tick updates above.
        void OnCaptureProgress(CaptureProgressEventArgs e) => tracker.OnCaptureProgress(e);

        Task OnCaptureStateChanged(CaptureState _, CaptureState newState)
        {
            if (tracker.OnCaptureStateChanged(newState))
            {
                WriteLine($"  [dim]{GenerationProgressTracker.PhaseLabel(tracker.Phase)}[/]");
            }

            return Task.CompletedTask;
        }

        Task OnPlaybackStatusChanged(string _, DemoPlaybackStatusChange statusChange)
        {
            if (tracker.OnDemoPlaybackStatusChanged(statusChange))
            {
                if (tracker.CurrentClipIndex is null)
                {
                    // Between clips only demo loading is worth a line; the return to
                    // "starting" after DemoLoaded is implied by the next clip-started line.
                    if (tracker.Phase == GenerationClipPhase.LoadingDemo)
                    {
                        WriteLine("[cyan]loading demo...[/]");
                    }
                }
                else
                {
                    WriteLine($"  [dim]{GenerationProgressTracker.PhaseLabel(tracker.Phase)}[/]");
                }
            }

            return Task.CompletedTask;
        }

        session.CompilationClipStarted += OnClipStarted;
        session.CompilationClipCompleted += OnClipCompleted;
        session.TickUpdated += OnTickUpdated;
        session.CaptureProgressUpdated += OnCaptureProgress;
        session.CaptureStateChanged += OnCaptureStateChanged;
        session.DemoPlaybackStatusChanged += OnPlaybackStatusChanged;

        try
        {
            return await session.RunCompilationAsync(compilation, cancellationToken);
        }
        finally
        {
            session.CompilationClipStarted -= OnClipStarted;
            session.CompilationClipCompleted -= OnClipCompleted;
            session.TickUpdated -= OnTickUpdated;
            session.CaptureProgressUpdated -= OnCaptureProgress;
            session.CaptureStateChanged -= OnCaptureStateChanged;
            session.DemoPlaybackStatusChanged -= OnPlaybackStatusChanged;
        }
    }
}
