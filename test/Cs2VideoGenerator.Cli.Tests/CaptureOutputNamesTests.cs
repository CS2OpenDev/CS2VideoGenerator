#region

using Cs2VideoGenerator.Cli.Utils;
using Cs2VideoGenerator.Core.Capture;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Cli.Tests;

/// <summary>
///     The descriptive output name: what was captured, then how, then when. Pinned as literals
///     because these names are the review surface a directory of ad-hoc recordings is sorted and
///     compared by.
/// </summary>
public class CaptureOutputNamesTests
{
    private static readonly DateTime Timestamp = new(2026, 7, 31, 0, 45, 12);

    [Fact]
    public void AHookedCapture_CarriesProviderResolutionFpsSourceAndQuality()
    {
        // The demo path is a host-native path (it comes straight off --demo), so the directory
        // part is built rather than written as a literal: a Windows literal here would be a
        // legal single filename on Linux and macOS, where '\' separates nothing.
        string name = CaptureOutputNames.Describe(
            Path.Combine("replays", "match730_406.dem"), 49000, 51000,
            CaptureProviderNames.InEngineHooked,
            1920, 1080, 60, new InEngineCaptureConfiguration(), Timestamp);

        Assert.Equal("match730_406_49000-51000.InEngineHooked.1920x1080.60fps.RawRgba.q16.20260731-004512",
            name);
    }

    [Fact]
    public void AnInEngineTgaCapture_NamesTheFrameFormat()
    {
        string name = CaptureOutputNames.Describe(
            "demo.dem", 0, 640, CaptureProviderNames.InEngine,
            1280, 720, 60, new InEngineCaptureConfiguration(), Timestamp);

        Assert.Equal("demo_0-640.InEngine.1280x720.60fps.Tga.q16.20260731-004512", name);
    }

    [Fact]
    public void AJpgCapture_CarriesTheEngineJpegQuality()
    {
        string name = CaptureOutputNames.Describe(
            "demo.dem", 0, 640, CaptureProviderNames.InEngine,
            1920, 1080, 60,
            new InEngineCaptureConfiguration { FrameFormat = InEngineFrameFormat.Jpg, JpegQuality = 85 },
            Timestamp);

        Assert.Contains(".Jpg85.", name);
    }

    [Fact]
    public void AConfiguredPreset_RidesTheName()
    {
        string name = CaptureOutputNames.Describe(
            "demo.dem", 0, 640, CaptureProviderNames.InEngine,
            1920, 1080, 60,
            new InEngineCaptureConfiguration { IntermediateEncoderPreset = "p4" },
            Timestamp);

        Assert.EndsWith(".Tga.q16.p4.20260731-004512", name);
    }

    [Fact]
    public void AnObsCapture_CarriesNoInEngineParts()
    {
        string name = CaptureOutputNames.Describe(
            "demo.dem", 100, 200, CaptureProviderNames.Obs,
            2560, 1440, 60, new InEngineCaptureConfiguration(), Timestamp);

        Assert.Equal("demo_100-200.OBS.2560x1440.60fps.20260731-004512", name);
    }

    [Fact]
    public void HostileConfigurationStrings_CannotBreakTheFileName()
    {
        string name = CaptureOutputNames.Describe(
            "demo.dem", 0, 1, CaptureProviderNames.InEngine,
            1920, 1080, 60,
            new InEngineCaptureConfiguration { IntermediateEncoderPreset = "we<ird|pre:set" },
            Timestamp);

        Assert.Contains("we-ird-pre-set", name);

        // Spelled out rather than asked of Path.GetInvalidFileNameChars(), which on Unix is only
        // NUL and '/' - the point is that the name survives the move to a Windows filesystem too.
        char[] neverValid = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];
        Assert.DoesNotContain(neverValid, c => name.Contains(c));
        Assert.DoesNotContain(name, c => c < ' ');
    }
}
