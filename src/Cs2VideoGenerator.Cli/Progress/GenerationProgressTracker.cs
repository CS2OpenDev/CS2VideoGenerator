#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;
using Spectre.Console;

#endregion

namespace Cs2VideoGenerator.Cli.Progress;

/// <summary>
///     Phase of the compilation currently visible to the user. Only phases the Core events can
///     actually tell apart are modeled.
/// </summary>
public enum GenerationClipPhase
{
    /// <summary>Clip announced but no more specific signal received yet.</summary>
    Starting,

    /// <summary>The demo file is being loaded (DemoPlaybackStatus.DemoLoading).</summary>
    LoadingDemo,

    /// <summary>The capture environment is being prepared (CaptureState.PreparingEnvironment).</summary>
    PreparingCapture,

    /// <summary>Playback + capture are running (CaptureState.Capturing / DemoPlaybackStatus.Playing).</summary>
    Recording,

    /// <summary>Capture is stopping / post-processing (CaptureState.Stopping / Finalizing).</summary>
    Finalizing
}

/// <summary>
///     Consumes <see cref="Core.CsvgVideoSession" /> event payloads raised during
///     <c>CaptureCompilationAsync</c> and turns them into display state and formatted lines.
///     Pure state machine with no console dependency, so the event-to-output mapping is
///     unit-testable independent of Spectre rendering.
/// </summary>
public sealed class GenerationProgressTracker(int totalClips)
{
    private readonly Lock _gate = new();

    private TimeSpan? _captureElapsed;
    private int _completedClips;
    private Cs2CompilationClip? _currentClip;
    private double _currentClipProgress;
    private int _failedClips;
    private GenerationClipPhase _phase = GenerationClipPhase.Starting;

    public int TotalClips { get; } = totalClips;

    /// <summary>Number of clips that have completed (success or failure).</summary>
    public int CompletedClips
    {
        get
        {
            lock (_gate)
            {
                return _completedClips;
            }
        }
    }

    public int FailedClips
    {
        get
        {
            lock (_gate)
            {
                return _failedClips;
            }
        }
    }

    private int? _currentClipIndex;

    /// <summary>Original compilation index of the clip currently in flight, or null between clips.</summary>
    public int? CurrentClipIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentClipIndex;
            }
        }
    }

    public GenerationClipPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>Progress through the current clip's tick range, 0..1.</summary>
    public double CurrentClipProgress
    {
        get
        {
            lock (_gate)
            {
                return _currentClipProgress;
            }
        }
    }

    #region Event intake

    public void OnClipStarted(int clipIndex, Cs2CompilationClip clip)
    {
        lock (_gate)
        {
            _currentClipIndex = clipIndex;
            _currentClip = clip;
            _phase = GenerationClipPhase.Starting;
            _currentClipProgress = 0;
            _captureElapsed = null;
        }
    }

    /// <summary>
    ///     Clears the current-clip state, so call <see cref="FormatCompletionLine" /> before this if
    ///     the line should describe the clip that just finished.
    /// </summary>
    public void OnClipCompleted(int clipIndex, Cs2ClipResult result)
    {
        lock (_gate)
        {
            _completedClips++;
            if (!result.Success)
            {
                _failedClips++;
            }

            _currentClip = null;
            _currentClipIndex = null;
            _currentClipProgress = 0;
            _captureElapsed = null;
            _phase = GenerationClipPhase.Starting;
        }
    }

    /// <summary>True when the visible phase changed.</summary>
    public bool OnDemoPlaybackStatusChanged(DemoPlaybackStatusChange statusChange)
    {
        GenerationClipPhase? newPhase = statusChange.NewStatus switch
        {
            DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading => GenerationClipPhase.LoadingDemo,
            DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded => GenerationClipPhase.Starting,
            DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Playing => GenerationClipPhase.Recording,
            _ => null
        };

        return newPhase.HasValue && SetPhase(newPhase.Value);
    }

    /// <summary>True when the visible phase changed.</summary>
    public bool OnCaptureStateChanged(CaptureState newState)
    {
        GenerationClipPhase? newPhase = newState switch
        {
            CaptureState.PreparingEnvironment => GenerationClipPhase.PreparingCapture,
            CaptureState.Capturing => GenerationClipPhase.Recording,
            CaptureState.Stopping or CaptureState.Finalizing => GenerationClipPhase.Finalizing,
            _ => null
        };

        return newPhase.HasValue && SetPhase(newPhase.Value);
    }

    /// <summary>True when the current clip's progress value moved.</summary>
    public bool OnTickUpdated(long tick)
    {
        lock (_gate)
        {
            if (_currentClip is null)
            {
                return false;
            }

            double span = _currentClip.EndTick - _currentClip.StartTick;
            double progress = span <= 0
                ? 1
                : Math.Clamp((tick - _currentClip.StartTick) / span, 0, 1);

            if (Math.Abs(progress - _currentClipProgress) < double.Epsilon)
            {
                return false;
            }

            _currentClipProgress = progress;
            return true;
        }
    }

    /// <summary>Elapsed capture time for the current clip.</summary>
    public void OnCaptureProgress(CaptureProgressEventArgs e)
    {
        lock (_gate)
        {
            _captureElapsed = e.ElapsedTime;
        }
    }

    private bool SetPhase(GenerationClipPhase phase)
    {
        lock (_gate)
        {
            if (_phase == phase)
            {
                return false;
            }

            _phase = phase;
            return true;
        }
    }

    #endregion

    #region Formatting

    /// <summary>Human label for a phase (lower case, used inline in progress lines).</summary>
    public static string PhaseLabel(GenerationClipPhase phase) =>
        phase switch
        {
            GenerationClipPhase.Starting => "starting",
            GenerationClipPhase.LoadingDemo => "loading demo",
            GenerationClipPhase.PreparingCapture => "preparing capture",
            GenerationClipPhase.Recording => "recording",
            GenerationClipPhase.Finalizing => "finalizing",
            _ => phase.ToString().ToLowerInvariant()
        };

    /// <summary>
    ///     Markup describing the clip currently in flight (index, player, phase, capture elapsed),
    ///     or the between-clips state (e.g. loading the next demo).
    /// </summary>
    public string DescribeCurrentClip()
    {
        lock (_gate)
        {
            if (_currentClip is null)
            {
                return _phase == GenerationClipPhase.LoadingDemo
                    ? "[cyan]Loading demo...[/]"
                    : "[dim]Waiting...[/]";
            }

            string elapsed = _captureElapsed is { } t ? $" [dim]{t.TotalSeconds:F0}s[/]" : string.Empty;
            return
                $"Clip {_currentClipIndex + 1}/{TotalClips}: {Markup.Escape(_currentClip.PlayerNameToSpectate)} " +
                $"[dim]({PhaseLabel(_phase)})[/]{elapsed}";
        }
    }

    public string DescribeOverall()
    {
        lock (_gate)
        {
            string suffix = _currentClip is null && _phase == GenerationClipPhase.LoadingDemo
                ? " [cyan](loading demo)[/]"
                : string.Empty;
            return $"[bold]Overall[/] [dim]{_completedClips}/{TotalClips} clips[/]{suffix}";
        }
    }

    /// <summary>Markup announcing that a clip started (plain/non-interactive mode).</summary>
    public string FormatClipStartedLine(int clipIndex, Cs2CompilationClip clip) =>
        $"Clip {clipIndex + 1}/{TotalClips} started: {Markup.Escape(clip.PlayerNameToSpectate)} " +
        $"[dim](ticks {clip.StartTick}-{clip.EndTick}, {Markup.Escape(Path.GetFileName(clip.DemoFilePath))})[/]";

    /// <summary>
    ///     Markup for the persistent per-clip completion line:
    ///     ✓ + output file name + duration on success, ✗ + error on failure. The duration is the clip's
    ///     length, so a provider that reports how fast it rendered gets that appended - for offline
    ///     capture the two are unrelated numbers, and only one of them says whether the run is slow.
    /// </summary>
    public string FormatCompletionLine(int clipIndex, Cs2ClipResult result)
    {
        string label = $"Clip {clipIndex + 1}/{TotalClips}";
        if (result.Success)
        {
            string fileName = Path.GetFileName(result.OutputFilePath);
            string? rates = CaptureStatisticsFormatter.DescribeRates(result.CaptureResult?.Statistics);
            string detail = rates is null
                ? $"{result.Duration.TotalSeconds:F1}s"
                : $"{result.Duration.TotalSeconds:F1}s, {Markup.Escape(rates)}";

            return $"[green]✓[/] {label}: {Markup.Escape(fileName)} [dim]({detail})[/]";
        }

        return $"[red]✗[/] {label}: {Markup.Escape(result.ErrorMessage ?? "capture failed")}";
    }

    #endregion
}
