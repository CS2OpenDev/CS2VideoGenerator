#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Services;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The in-engine configuration's own rules. Most of these settings exist because what CS2 does has
///     not been observed, so validation is about catching combinations that cannot work at all -
///     above all a Targa record length that disagrees with what the engine writes, the one wrong
///     value that could corrupt output rather than fail.
/// </summary>
public class InEngineCaptureConfigurationTests
{
    [Fact]
    public void Defaults_AreCoherent()
    {
        InEngineCaptureConfiguration config = new();

        Assert.Empty(config.Validate());
        Assert.Equal(InEngineFrameFormat.Tga, config.FrameFormat);
        Assert.Equal(MissingAudioBehavior.Fail, config.MissingAudioBehavior);
        Assert.Equal(ImagePipeEncoder.Auto, config.IntermediateEncoder);
        Assert.Equal(16, config.IntermediateQuality);

        // Unset by design: the useful default depends on which encoder Auto resolves to, and that is
        // not known until ffmpeg has been probed.
        Assert.Null(config.IntermediateEncoderPreset);
        Assert.Equal(96, config.StartTickClamp);
        Assert.True(config.DeleteFramesAfterPipe);
        Assert.False(config.KeepStagingOnFailure);
        Assert.Equal(8, config.EngineOutput.FrameNumberDigits);
        Assert.Equal(0, config.EngineOutput.FirstFrameNumber);
        Assert.True(config.EngineOutput.FramesInTimestampedSubdirectory);
        Assert.Equal(90, config.JpegQuality);
    }

    /// <summary>
    ///     The settled default: audio is confirmed working on build 2000879 (the wav token writes a
    ///     game-time-length wav under csgo/movie/&lt;timestamp&gt;/), so a clip that asked for audio
    ///     and has none does not meet the requested spec and fails rather than shipping mute.
    /// </summary>
    [Fact]
    public void MissingAudio_DefaultsToFailing()
    {
        Assert.Equal(MissingAudioBehavior.Fail, new InEngineCaptureConfiguration().MissingAudioBehavior);
    }

    /// <summary>
    ///     A Targa configuration without an explicit record length is valid: the length is computed
    ///     per clip from the clip's width and height (the 32-bit arithmetic is an observed fact),
    ///     and a clip that supplies neither resolution nor an explicit value fails loudly at capture
    ///     start rather than at configuration time.
    /// </summary>
    [Fact]
    public void TgaWithoutARecordLength_IsAcceptedAtConfigurationTime()
    {
        Assert.Empty(new InEngineCaptureConfiguration { FrameFormat = InEngineFrameFormat.Tga }.Validate());
    }

    [Fact]
    public void TgaWithARecordLength_IsAccepted()
    {
        InEngineCaptureConfiguration config = new()
        {
            FrameFormat = InEngineFrameFormat.Tga,
            EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = 6220818 }
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void JpgNeedsNoRecordLength()
    {
        Assert.Empty(new InEngineCaptureConfiguration { FrameFormat = InEngineFrameFormat.Jpg }.Validate());
    }

    /// <summary>
    ///     The observed arithmetic: a live 1080p frame on CS2 build 2000879 was exactly
    ///     18 + 1920 x 1080 x 4 bytes - 32-bit Targa with the plain 18-byte header.
    /// </summary>
    [Fact]
    public void UncompressedTgaFrameSize_IsHeaderPlusFourBytesPerPixel()
    {
        Assert.Equal(18 + (1920L * 1080 * 4), InEngineCaptureConfiguration.UncompressedTgaFrameSize(1920, 1080));
        Assert.Equal(8294418, InEngineCaptureConfiguration.UncompressedTgaFrameSize(1920, 1080));
    }

    [Theory]
    [InlineData(0, "csvg_000")]
    [InlineData(7, "csvg_007")]
    [InlineData(123, "csvg_123")]
    [InlineData(1234, "csvg_1234")]
    public void MovieName_IsPaddedByClipIndex(int clipIndex, string expected)
    {
        Assert.Equal(expected, InEngineCaptureConfiguration.MovieName(clipIndex));
    }

    [Fact]
    public void FrameFormat_SelectsTheDecoderAndTheExtension()
    {
        InEngineCaptureConfiguration jpg = new() { FrameFormat = InEngineFrameFormat.Jpg };
        Assert.Equal(ImagePipeSourceFormat.Mjpeg, jpg.SourceFormat());
        Assert.Equal("jpg", jpg.FrameExtension());

        InEngineCaptureConfiguration tga = new()
        {
            FrameFormat = InEngineFrameFormat.Tga,
            EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = 1 }
        };
        Assert.Equal(ImagePipeSourceFormat.Targa, tga.SourceFormat());
        Assert.Equal("tga", tga.FrameExtension());
    }

    [Fact]
    public void ZeroOrNegativeRecordLength_IsRejected()
    {
        InEngineCaptureConfiguration config = new()
        {
            FrameFormat = InEngineFrameFormat.Tga,
            EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = 0 }
        };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("must be greater than zero", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The audio glob has to name the movie, or every clip finds every other clip's wav and the
    ///     newest-wins tiebreak silently picks the wrong one.
    /// </summary>
    [Fact]
    public void AudioSearchPatternWithoutTheMovieNamePlaceholder_IsRejected()
    {
        InEngineCaptureConfiguration config = new()
        {
            EngineOutput = new InEngineEngineOutput { AudioFileSearchPattern = "*.wav" }
        };

        Assert.Contains(config.Validate(), issue => issue.Contains("{0}", StringComparison.Ordinal));
    }

    [Fact]
    public void BlankAudioSearchPattern_IsRejected()
    {
        InEngineCaptureConfiguration config = new()
        {
            EngineOutput = new InEngineEngineOutput { AudioFileSearchPattern = "" }
        };

        Assert.Contains(config.Validate(), issue => issue.Contains("cannot be blank", StringComparison.Ordinal));
    }

    [Fact]
    public void JpgWithoutAFormatToken_IsRejected()
    {
        InEngineCaptureConfiguration config = new()
        {
            FrameFormat = InEngineFrameFormat.Jpg,
            EngineOutput = new InEngineEngineOutput { JpgFormatToken = " " }
        };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("JpgFormatToken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositivePollInterval_IsRejected(int pollMs)
    {
        InEngineCaptureConfiguration config = new() { FrameConsumerPollMs = pollMs };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("FrameConsumerPollMs", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroDigitFrameNumbers_AreRejected()
    {
        InEngineCaptureConfiguration config = new()
        {
            EngineOutput = new InEngineEngineOutput { FrameNumberDigits = 0 }
        };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("FrameNumberDigits", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2.5)]
    public void NonPositiveAudioTolerance_IsRejected(double tolerance)
    {
        InEngineCaptureConfiguration config = new() { AudioDurationTolerancePercent = tolerance };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("AudioDurationTolerancePercent", StringComparison.Ordinal));
    }

    [Fact]
    public void BlankIntermediateContainer_IsRejected()
    {
        InEngineCaptureConfiguration config = new() { IntermediateContainer = "  " };

        Assert.Contains(config.Validate(),
            issue => issue.Contains("IntermediateContainer", StringComparison.Ordinal));
    }
}
