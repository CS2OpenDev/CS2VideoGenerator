#region

using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Services;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The hooked in-engine provider: the audio-only startmovie plan, the raw-video encode request,
///     the capability and geometry fail-fasts, and the provider surface. The pipe protocol itself is
///     covered in <see cref="PresentHookFrameConsumerTests" />.
/// </summary>
public class InEngineHookedProviderTests
{
    private static Task<InEngineTestHarness> HookedHarnessAsync(InEngineCaptureConfiguration? configuration = null) =>
        InEngineTestHarness.CreateAsync(configuration, frameSource: InEngineFrameSource.PresentHook);

    /// <summary>
    ///     The hooked startmovie line, pinned as a literal: `wav nocodec` is the empirically tested
    ///     token combination that gives fixed-timestep pacing and a game-time wav with zero frame
    ///     files (observed on build 2000879). The plugin's arm seam keys on this command executing.
    /// </summary>
    [Fact]
    public async Task TheHookedPlan_UsesTheAudioOnlyStartmovieLine()
    {
        await using InEngineTestHarness harness = await HookedHarnessAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], clip.TickPlan.SetupCommands);
        Assert.Equal(["startmovie csvg_000 wav nocodec"], clip.TickPlan.Batches[0].Commands);
        Assert.Equal(["endmovie", "host_framerate 0", "engine_no_focus_sleep 1"],
            clip.TickPlan.Batches[1].Commands);
    }

    /// <summary>
    ///     The wav rides even for a silent clip: it is the mode selector, not the audio opt-in. The
    ///     unwanted wav is discarded at finalize by the same path that already handles stale wavs.
    /// </summary>
    [Fact]
    public async Task ASilentClip_StillUsesTheAudioOnlyMode()
    {
        await using InEngineTestHarness harness = await HookedHarnessAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(captureAudio: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(["startmovie csvg_000 wav nocodec"], clip.TickPlan.Batches[0].Commands);
    }

    [Fact]
    public async Task WithoutThePresentCaptureCapability_TheClipFailsBeforePlayback()
    {
        await using InEngineTestHarness harness = await HookedHarnessAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(
            harness.Clip(width: 1920, height: 1080), TestContext.Current.CancellationToken);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => clip.OnPlaybackStartingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("present-capture", ex.Message);
        Assert.Contains("InEngine", ex.Message);
    }

    [Fact]
    public async Task WithoutAClipResolution_TheClipFailsNamingTheRemedy()
    {
        await using InEngineTestHarness harness = await HookedHarnessAsync();
        harness.Engine.AdvertisedCapabilities.Add(CsvgCapabilities.PresentCapture);

        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(),
            TestContext.Current.CancellationToken);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => clip.OnPlaybackStartingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Width", ex.Message);
        Assert.Contains("--width/--height", ex.Message);
    }

    [Fact]
    public async Task TheHookedSession_ReportsTheHookedProviderName()
    {
        await using InEngineTestHarness harness = await HookedHarnessAsync();

        Assert.Equal(CaptureProviderNames.InEngineHooked, harness.Session.ProviderName);
    }

    [Fact]
    public void TheHookedProviderName_ResolvesCaseInsensitively()
    {
        Assert.True(CaptureProviderNames.TryResolve("inenginehooked", out string canonical));
        Assert.Equal(CaptureProviderNames.InEngineHooked, canonical);
        Assert.Contains(CaptureProviderNames.InEngineHooked, CaptureProviderNames.All);
    }

    [Fact]
    public void TheHookedFactory_IsWindowsOnly_AndNamesItself()
    {
        InEngineHookedCaptureSessionFactory factory = new(
            Options.Create(new InEngineCaptureConfiguration()),
            new FakeFfmpegService(),
            () => Path.GetTempPath(),
            () => Path.GetTempPath());

        Assert.Equal(CaptureProviderNames.InEngineHooked, factory.ProviderName);
        Assert.Equal(OperatingSystem.IsWindows(), factory.IsSupportedOnCurrentPlatform);
    }

    /// <summary>rawvideo demuxer, the observed rgba layout, no image decoder and no record length.</summary>
    [Fact]
    public void RawVideoArguments_DeclareTheGeometryAndPixelFormat()
    {
        List<string> arguments = [.. FfmpegService.BuildImagePipeArguments(
            new ImagePipeEncodeRequest
            {
                OutputPath = "out.mkv",
                FrameRate = 60,
                SourceFormat = ImagePipeSourceFormat.Targa,
                FrameSizeBytes = 12345, // must be ignored on the raw path
                RawVideo = new RawVideoInput { PixelFormat = "rgba", Width = 1920, Height = 1080 }
            },
            ImagePipeEncoder.X264)];

        int f = arguments.IndexOf("-f");
        Assert.Equal("rawvideo", arguments[f + 1]);
        Assert.Equal("rgba", arguments[arguments.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("1920x1080", arguments[arguments.IndexOf("-video_size") + 1]);
        Assert.DoesNotContain("-frame_size", arguments);
        Assert.DoesNotContain("image2pipe", arguments);
    }

    [Fact]
    public void HookedConfigurationKnobs_AreValidated()
    {
        Assert.Contains(new InEngineCaptureConfiguration { FramePipeName = " " }.Validate(),
            issue => issue.Contains("FramePipeName"));
        Assert.Contains(new InEngineCaptureConfiguration { HookedDrainTimeoutMs = 0 }.Validate(),
            issue => issue.Contains("HookedDrainTimeoutMs"));
        Assert.Contains(
            new InEngineCaptureConfiguration
            {
                EngineOutput = new InEngineEngineOutput { AudioOnlyTokens = "" }
            }.Validate(),
            issue => issue.Contains("AudioOnlyTokens"));
        Assert.Empty(new InEngineCaptureConfiguration().Validate());
    }
}
