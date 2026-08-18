#region

using Cs2VideoGenerator.Cli.Progress;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Proto;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     Unit tests for the progress tracker: the mapping from Core event payloads
///     (clip started/completed, playback status, capture state, ticks) to display state
///     and formatted lines. Spectre rendering itself is not under test.
/// </summary>
public class GenerationProgressTrackerTests
{
    private static Cs2CompilationClip MakeClip(
        int startTick = 1000,
        int endTick = 2000,
        string player = "s1mple",
        string demoPath = "/demos/match.dem") =>
        new()
        {
            PlayerSteamId = 76561198000000000,
            MatchChecksum = "abc123",
            DemoFilePath = demoPath,
            StartTick = startTick,
            EndTick = endTick,
            PlayerNameToSpectate = player
        };

    private static Cs2ClipResult MakeResult(
        int clipIndex,
        bool success,
        string outputFilePath = "/out/highlight_000.mp4",
        double durationSeconds = 12.3,
        string? errorMessage = null,
        CaptureResult? captureResult = null) =>
        new()
        {
            ClipIndex = clipIndex,
            Success = success,
            OutputFilePath = outputFilePath,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            ErrorMessage = errorMessage,
            CaptureResult = captureResult
        };

    private static DemoPlaybackStatusChange PlaybackStatus(
        DemoPlaybackStatusChange.Types.DemoPlaybackStatus newStatus) =>
        new() { NewStatus = newStatus };

    [Fact]
    public void NewTracker_HasInitialState()
    {
        GenerationProgressTracker tracker = new(3);

        Assert.Equal(3, tracker.TotalClips);
        Assert.Equal(0, tracker.CompletedClips);
        Assert.Equal(0, tracker.FailedClips);
        Assert.Null(tracker.CurrentClipIndex);
        Assert.Equal(GenerationClipPhase.Starting, tracker.Phase);
        Assert.Equal(0, tracker.CurrentClipProgress);
    }

    [Fact]
    public void OnClipStarted_SetsCurrentClipAndResetsPhase()
    {
        GenerationProgressTracker tracker = new(3);
        tracker.OnDemoPlaybackStatusChanged(
            PlaybackStatus(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading));

        tracker.OnClipStarted(1, MakeClip());

        Assert.Equal(1, tracker.CurrentClipIndex);
        Assert.Equal(GenerationClipPhase.Starting, tracker.Phase);
        Assert.Equal(0, tracker.CurrentClipProgress);
    }

    [Theory]
    [InlineData(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading,
        GenerationClipPhase.LoadingDemo, true)]
    [InlineData(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Playing,
        GenerationClipPhase.Recording, true)]
    // Statuses the display does not distinguish leave the phase untouched
    [InlineData(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Paused,
        GenerationClipPhase.Starting, false)]
    [InlineData(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Stopped,
        GenerationClipPhase.Starting, false)]
    [InlineData(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.Unknown,
        GenerationClipPhase.Starting, false)]
    public void OnDemoPlaybackStatusChanged_MapsStatusToPhase(
        DemoPlaybackStatusChange.Types.DemoPlaybackStatus status,
        GenerationClipPhase expectedPhase,
        bool expectedChanged)
    {
        GenerationProgressTracker tracker = new(1);

        bool changed = tracker.OnDemoPlaybackStatusChanged(PlaybackStatus(status));

        Assert.Equal(expectedChanged, changed);
        Assert.Equal(expectedPhase, tracker.Phase);
    }

    [Fact]
    public void OnDemoPlaybackStatusChanged_DemoLoaded_ReturnsToStarting()
    {
        GenerationProgressTracker tracker = new(1);
        tracker.OnDemoPlaybackStatusChanged(
            PlaybackStatus(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading));

        bool changed = tracker.OnDemoPlaybackStatusChanged(
            PlaybackStatus(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoaded));

        Assert.True(changed);
        Assert.Equal(GenerationClipPhase.Starting, tracker.Phase);
    }

    [Theory]
    [InlineData(CaptureState.PreparingEnvironment, GenerationClipPhase.PreparingCapture, true)]
    [InlineData(CaptureState.Capturing, GenerationClipPhase.Recording, true)]
    [InlineData(CaptureState.Stopping, GenerationClipPhase.Finalizing, true)]
    [InlineData(CaptureState.Finalizing, GenerationClipPhase.Finalizing, true)]
    // States without a user-visible phase leave the phase untouched
    [InlineData(CaptureState.Ready, GenerationClipPhase.Starting, false)]
    [InlineData(CaptureState.Completed, GenerationClipPhase.Starting, false)]
    [InlineData(CaptureState.Error, GenerationClipPhase.Starting, false)]
    public void OnCaptureStateChanged_MapsStateToPhase(
        CaptureState state,
        GenerationClipPhase expectedPhase,
        bool expectedChanged)
    {
        GenerationProgressTracker tracker = new(1);

        bool changed = tracker.OnCaptureStateChanged(state);

        Assert.Equal(expectedChanged, changed);
        Assert.Equal(expectedPhase, tracker.Phase);
    }

    [Fact]
    public void SetSamePhaseTwice_SecondChangeReportsFalse()
    {
        GenerationProgressTracker tracker = new(1);

        Assert.True(tracker.OnCaptureStateChanged(CaptureState.Capturing));
        Assert.False(tracker.OnCaptureStateChanged(CaptureState.Capturing));
    }

    [Fact]
    public void OnTickUpdated_WithoutCurrentClip_ReturnsFalse()
    {
        GenerationProgressTracker tracker = new(1);

        Assert.False(tracker.OnTickUpdated(1500));
        Assert.Equal(0, tracker.CurrentClipProgress);
    }

    [Theory]
    [InlineData(1000, 0.0)] // at start tick
    [InlineData(1500, 0.5)] // halfway
    [InlineData(2000, 1.0)] // at end tick
    [InlineData(500, 0.0)] // before start clamps to 0
    [InlineData(9999, 1.0)] // past end clamps to 1
    public void OnTickUpdated_ComputesClampedFraction(long tick, double expected)
    {
        GenerationProgressTracker tracker = new(1);
        tracker.OnClipStarted(0, MakeClip(startTick: 1000, endTick: 2000));

        tracker.OnTickUpdated(tick);

        Assert.Equal(expected, tracker.CurrentClipProgress, precision: 5);
    }

    [Fact]
    public void OnTickUpdated_UnchangedProgress_ReturnsFalse()
    {
        GenerationProgressTracker tracker = new(1);
        tracker.OnClipStarted(0, MakeClip(startTick: 1000, endTick: 2000));

        Assert.True(tracker.OnTickUpdated(1500));
        Assert.False(tracker.OnTickUpdated(1500));
    }

    [Fact]
    public void OnClipCompleted_CountsSuccessesAndFailures_AndClearsCurrentClip()
    {
        GenerationProgressTracker tracker = new(2);

        tracker.OnClipStarted(0, MakeClip());
        tracker.OnClipCompleted(0, MakeResult(0, success: true));

        Assert.Equal(1, tracker.CompletedClips);
        Assert.Equal(0, tracker.FailedClips);
        Assert.Null(tracker.CurrentClipIndex);
        Assert.Equal(0, tracker.CurrentClipProgress);

        tracker.OnClipStarted(1, MakeClip());
        tracker.OnClipCompleted(1, MakeResult(1, success: false, errorMessage: "OBS unreachable"));

        Assert.Equal(2, tracker.CompletedClips);
        Assert.Equal(1, tracker.FailedClips);
    }

    [Fact]
    public void FormatCompletionLine_Success_ShowsCheckFileNameAndDuration()
    {
        GenerationProgressTracker tracker = new(3);

        string line = tracker.FormatCompletionLine(0,
            MakeResult(0, success: true, outputFilePath: "/out/highlight_000.mp4", durationSeconds: 12.34));

        Assert.Contains("✓", line);
        Assert.Contains("Clip 1/3", line);
        Assert.Contains("highlight_000.mp4", line);
        Assert.Contains("12.3s", line);
        Assert.DoesNotContain("/out/", line); // file name only, not the full path
    }

    /// <summary>
    ///     For offline capture the clip's length and the time it took to render are unrelated numbers,
    ///     and only the second one says whether the run is going slowly - so a provider that measures
    ///     itself gets both on the line.
    /// </summary>
    [Fact]
    public void FormatCompletionLine_WithRenderStatistics_AlsoShowsTheRates()
    {
        GenerationProgressTracker tracker = new(1);
        CaptureResult capture = new()
        {
            Success = true,
            OutputFilePath = "/out/highlight_000.mp4",
            Duration = TimeSpan.FromSeconds(9.4),
            Statistics = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["framesConsumed"] = 564,
                ["wallClockSeconds"] = 8.3,
                ["captureFps"] = 67.9,
                ["realtimeFactor"] = 1.13
            }
        };

        string line = tracker.FormatCompletionLine(0,
            MakeResult(0, success: true, durationSeconds: 9.4, captureResult: capture));

        Assert.Contains("9.4s", line);
        Assert.Contains("68 fps", line);
        Assert.Contains("1.13x realtime", line);
    }

    /// <summary>
    ///     OBS reports no statistics, and the line stays exactly as it was.
    /// </summary>
    [Fact]
    public void FormatCompletionLine_WithoutStatistics_IsUnchanged()
    {
        GenerationProgressTracker tracker = new(1);

        string line = tracker.FormatCompletionLine(0, MakeResult(0, success: true, durationSeconds: 9.4));

        Assert.Contains("(9.4s)", line);
        Assert.DoesNotContain("fps", line);
    }

    [Fact]
    public void FormatCompletionLine_Failure_ShowsCrossAndError()
    {
        GenerationProgressTracker tracker = new(3);

        string line = tracker.FormatCompletionLine(2,
            MakeResult(2, success: false, errorMessage: "OBS connection refused"));

        Assert.Contains("✗", line);
        Assert.Contains("Clip 3/3", line);
        Assert.Contains("OBS connection refused", line);
    }

    [Fact]
    public void FormatCompletionLine_FailureWithoutMessage_UsesFallbackText()
    {
        GenerationProgressTracker tracker = new(1);

        string line = tracker.FormatCompletionLine(0, MakeResult(0, success: false));

        Assert.Contains("✗", line);
        Assert.Contains("capture failed", line);
    }

    [Fact]
    public void FormatClipStartedLine_ShowsIndexPlayerTicksAndDemoName()
    {
        GenerationProgressTracker tracker = new(3);
        Cs2CompilationClip clip = MakeClip(startTick: 100, endTick: 500,
            player: "device", demoPath: "/demos/nuke_final.dem");

        string line = tracker.FormatClipStartedLine(1, clip);

        Assert.Contains("Clip 2/3", line);
        Assert.Contains("device", line);
        Assert.Contains("100-500", line);
        Assert.Contains("nuke_final.dem", line);
    }

    [Fact]
    public void DescribeCurrentClip_NoClip_ReflectsDemoLoading()
    {
        GenerationProgressTracker tracker = new(2);

        Assert.Contains("Waiting", tracker.DescribeCurrentClip());

        tracker.OnDemoPlaybackStatusChanged(
            PlaybackStatus(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading));

        Assert.Contains("Loading demo", tracker.DescribeCurrentClip());
    }

    [Fact]
    public void DescribeCurrentClip_WithClip_ShowsIndexPlayerAndPhase()
    {
        GenerationProgressTracker tracker = new(2);
        tracker.OnClipStarted(0, MakeClip(player: "NiKo"));
        tracker.OnCaptureStateChanged(CaptureState.Capturing);

        string description = tracker.DescribeCurrentClip();

        Assert.Contains("Clip 1/2", description);
        Assert.Contains("NiKo", description);
        Assert.Contains("recording", description);
    }

    [Fact]
    public void DescribeCurrentClip_IncludesCaptureElapsedSeconds()
    {
        GenerationProgressTracker tracker = new(1);
        tracker.OnClipStarted(0, MakeClip());
        tracker.OnCaptureProgress(new CaptureProgressEventArgs(TimeSpan.FromSeconds(42)));

        Assert.Contains("42s", tracker.DescribeCurrentClip());
    }

    [Fact]
    public void Formatting_EscapesMarkupInUserSuppliedValues()
    {
        GenerationProgressTracker tracker = new(1);
        Cs2CompilationClip clip = MakeClip(player: "evil[red]name");
        tracker.OnClipStarted(0, clip);

        // Escaped markup doubles the brackets so Spectre renders them literally
        Assert.Contains("[[red]]", tracker.DescribeCurrentClip());
        Assert.Contains("[[red]]", tracker.FormatClipStartedLine(0, clip));

        string errorLine = tracker.FormatCompletionLine(0,
            MakeResult(0, success: false, errorMessage: "bad [markup] here"));
        Assert.Contains("[[markup]]", errorLine);
    }

    [Fact]
    public void PhaseLabel_CoversAllPhases()
    {
        Assert.Equal("starting", GenerationProgressTracker.PhaseLabel(GenerationClipPhase.Starting));
        Assert.Equal("loading demo", GenerationProgressTracker.PhaseLabel(GenerationClipPhase.LoadingDemo));
        Assert.Equal("preparing capture", GenerationProgressTracker.PhaseLabel(GenerationClipPhase.PreparingCapture));
        Assert.Equal("recording", GenerationProgressTracker.PhaseLabel(GenerationClipPhase.Recording));
        Assert.Equal("finalizing", GenerationProgressTracker.PhaseLabel(GenerationClipPhase.Finalizing));
    }

    [Fact]
    public void DescribeOverall_TracksCompletedCountAndDemoLoading()
    {
        GenerationProgressTracker tracker = new(3);

        Assert.Contains("0/3", tracker.DescribeOverall());

        tracker.OnClipStarted(0, MakeClip());
        tracker.OnClipCompleted(0, MakeResult(0, success: true));

        Assert.Contains("1/3", tracker.DescribeOverall());

        tracker.OnDemoPlaybackStatusChanged(
            PlaybackStatus(DemoPlaybackStatusChange.Types.DemoPlaybackStatus.DemoLoading));

        Assert.Contains("loading demo", tracker.DescribeOverall());
    }
}
