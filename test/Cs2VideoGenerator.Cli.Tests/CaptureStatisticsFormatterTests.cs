#region

using Cs2VideoGenerator.Cli.Progress;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     The one line a user reads after an offline capture. What matters here is that it says what
///     really happened (the resolved encoder and preset, not the configured ones) and that it says
///     nothing at all for a provider that does not measure its own render.
/// </summary>
public class CaptureStatisticsFormatterTests
{
    private static Dictionary<string, object> Statistics(
        double framesConsumed = 564,
        double wallClockSeconds = 8.3,
        double captureFps = 67.9,
        double realtimeFactor = 1.13,
        string? encoder = "h264_nvenc",
        string? preset = "p7")
    {
        Dictionary<string, object> statistics = new(StringComparer.Ordinal)
        {
            ["framesConsumed"] = framesConsumed,
            ["wallClockSeconds"] = wallClockSeconds,
            ["captureFps"] = captureFps,
            ["realtimeFactor"] = realtimeFactor
        };

        if (encoder != null)
        {
            statistics["encoder"] = encoder;
        }

        if (preset != null)
        {
            statistics["encoderPreset"] = preset;
        }

        return statistics;
    }

    [Fact]
    public void Describe_FullStatistics_ReadsAsOneSentence() =>
        Assert.Equal(
            "Rendered 564 frames in 8.3 s wall (68 fps, 1.13x realtime), encoder h264_nvenc p7",
            CaptureStatisticsFormatter.Describe(Statistics()));

    /// <summary>
    ///     OBS reports no statistics at all - its wall clock and its clip length are the same number.
    ///     A caller that prints null prints nothing, which is the intended behavior there.
    /// </summary>
    [Fact]
    public void Describe_NoStatistics_SaysNothing()
    {
        Assert.Null(CaptureStatisticsFormatter.Describe(null));
        Assert.Null(CaptureStatisticsFormatter.Describe(new Dictionary<string, object>(StringComparer.Ordinal)));
        Assert.Null(CaptureStatisticsFormatter.DescribeRates(null));
    }

    /// <summary>
    ///     A provider that reports fewer keys gets a shorter line rather than a line with holes in it:
    ///     <c>Statistics</c> is provider-specific by design.
    /// </summary>
    [Fact]
    public void Describe_PartialStatistics_DegradesToWhatIsKnown()
    {
        Dictionary<string, object> frameCountOnly = new(StringComparer.Ordinal) { ["framesConsumed"] = 120 };
        Assert.Equal("Rendered 120 frames", CaptureStatisticsFormatter.Describe(frameCountOnly));

        string? noPreset = CaptureStatisticsFormatter.Describe(Statistics(encoder: "utvideo", preset: null));
        Assert.EndsWith("encoder utvideo", noPreset, StringComparison.Ordinal);

        string? noEncoder = CaptureStatisticsFormatter.Describe(Statistics(encoder: null, preset: null));
        Assert.DoesNotContain("encoder", noEncoder);
    }

    /// <summary>
    ///     A wall clock of zero means the render was never timed, so the rates derived from it are
    ///     absent rather than infinite.
    /// </summary>
    [Fact]
    public void Describe_UntimedRender_ReportsFramesAlone() =>
        Assert.Equal("Rendered 564 frames, encoder h264_nvenc p7",
            CaptureStatisticsFormatter.Describe(Statistics(wallClockSeconds: 0)));

    /// <summary>
    ///     The dictionary is boxed <c>object</c>, and a provider is free to put an int in it.
    /// </summary>
    [Fact]
    public void Describe_IntegerValues_AreReadTheSameAsDoubles()
    {
        Dictionary<string, object> statistics = new(StringComparer.Ordinal)
        {
            ["framesConsumed"] = 60,
            ["wallClockSeconds"] = 2L
        };

        Assert.Equal("Rendered 60 frames in 2.0 s wall", CaptureStatisticsFormatter.Describe(statistics));
    }

    [Fact]
    public void DescribeRates_IsJustTheRates() =>
        Assert.Equal("68 fps, 1.13x realtime", CaptureStatisticsFormatter.DescribeRates(Statistics()));
}
