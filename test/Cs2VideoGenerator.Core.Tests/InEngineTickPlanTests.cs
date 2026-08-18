#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The exact console commands the in-engine provider asks the engine to run, and exactly when.
///     Pinned as literal strings on purpose: these are the whole interface to CS2, they are typed into
///     the same console a user would, and a change to any of them is a change to what the engine does.
/// </summary>
public class InEngineTickPlanTests
{
    private static async Task<TickCommandPlan> PlanAsync(
        InEngineCaptureConfiguration? configuration = null,
        int startTick = 1000,
        int endTick = 1640,
        int clipIndex = 0,
        int frameRate = 60)
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(configuration);
        IClipCapture clip = await harness.Session.BeginClipAsync(
            harness.Clip(startTick, endTick, clipIndex, frameRate));

        Assert.NotNull(clip.TickPlan);
        return clip.TickPlan;
    }

    [Fact]
    public async Task JpgCapture_ProducesTheSetupCommandsAndTheTwoBracketBatches()
    {
        TickCommandPlan plan = await PlanAsync();

        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], plan.SetupCommands);

        Assert.Equal(2, plan.Batches.Count);

        // Format, audio and jpeg quality all travel on the startmovie line - they are startmovie
        // arguments, not cvars (observed; the engine's usage string is
        // "startmovie testmovie jpg wav jpeg_qality 75 framerate 30").
        Assert.Equal(1000, plan.Batches[0].DemoTick);
        Assert.Equal(["startmovie csvg_000 jpg wav jpeg_quality 90"], plan.Batches[0].Commands);

        Assert.Equal(1640, plan.Batches[1].DemoTick);
        Assert.Equal(["endmovie", "host_framerate 0", "engine_no_focus_sleep 1"], plan.Batches[1].Commands);
    }

    /// <summary>
    ///     Audio is opt-in on the engine side: without the wav token startmovie writes no wav at all
    ///     (observed), so the token appears exactly when the clip asks for audio.
    /// </summary>
    [Fact]
    public async Task TheWavToken_AppearsExactlyWhenTheClipAsksForAudio()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture silent = await harness.Session.BeginClipAsync(
            harness.Clip(captureAudio: false), TestContext.Current.CancellationToken);

        Assert.NotNull(silent.TickPlan);
        Assert.Equal(["startmovie csvg_000 jpg jpeg_quality 90"], silent.TickPlan.Batches[0].Commands);
    }

    /// <summary>
    ///     The cvars carry no tick at all, which is the whole point: they used to be armed a second of
    ///     demo time ahead of the recording tick, where the plugin could only reach them while the demo
    ///     was still behind that tick. Batches fire on the half-open interval (last observed tick,
    ///     current tick], a compilation reloads the demo only when the demo path changes, and a second
    ///     clip from the same demo therefore starts from wherever the first one ended - which stranded
    ///     the setup of any clip starting within a second of the previous one's end. Nothing armed at a
    ///     tick can be safe from that; not being armed at a tick can. Replaces
    ///     TheSetupBatch_LandsAFullSecondOfDemoTime_BeforeTheRecordingTick and
    ///     SetupBatch_LeadsTheRecordingTickByOneSecondOfDemoTime, which pinned that arithmetic; the
    ///     cross-clip consequence is proven end to end in
    ///     <see cref="InEngineCaptureCoordinationTests" />.
    /// </summary>
    [Fact]
    public async Task TheCvarSetup_IsNotScheduledAtAnyTick()
    {
        TickCommandPlan plan = await PlanAsync(startTick: 1000, endTick: 1640);

        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], plan.SetupCommands);
        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal(1000, plan.Batches[0].DemoTick);
        Assert.Equal(1640, plan.Batches[1].DemoTick);
        Assert.All(plan.Batches, batch => Assert.DoesNotContain("host_framerate 60", batch.Commands));
        Assert.All(plan.Batches, batch => Assert.DoesNotContain("engine_no_focus_sleep 0", batch.Commands));
    }

    /// <summary>
    ///     The production default, pinned: Targa with audio, no jpeg arguments. The out-of-box
    ///     startmovie line is the whole recording contract with the engine.
    /// </summary>
    [Fact]
    public async Task TheProductionDefault_IsTargaWithAudio()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync(
            new InEngineCaptureConfiguration { FrameConsumerPollMs = 20 });
        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip());

        Assert.Equal(["startmovie csvg_000 tga wav"], clip.TickPlan.Batches[0].Commands);
    }

    /// <summary>
    ///     The tga token is mandatory: a token-less startmovie is the engine's AVI/video path
    ///     (observed - no format word in the reply, no image files written), not a Targa dump.
    /// </summary>
    [Fact]
    public async Task TgaCapture_PassesTheTgaToken()
    {
        TickCommandPlan plan = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Tga,
            EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = 6220818 }
        });

        Assert.Equal(["startmovie csvg_000 tga wav"], plan.Batches[0].Commands);
    }

    [Fact]
    public async Task JpegQuality_RidesTheStartmovieLine_WhenConfigured()
    {
        TickCommandPlan plan = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Jpg, JpegQuality = 95
        });

        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], plan.SetupCommands);
        Assert.Equal(["startmovie csvg_000 jpg wav jpeg_quality 95"], plan.Batches[0].Commands);
    }

    /// <summary>
    ///     The argument's spelling is configuration, not a literal, because the engine's own usage
    ///     text carries the typo 'jpeg_qality' and which spelling the parser accepts is unconfirmed.
    ///     A blank name suppresses the argument rather than emitting a broken one, and a null
    ///     quality leaves the engine's default alone.
    /// </summary>
    [Fact]
    public async Task JpegQualityArgument_IsConfigurable_AndBlankSuppressesIt()
    {
        TickCommandPlan renamed = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Jpg,
            JpegQuality = 90,
            EngineOutput = new InEngineEngineOutput { JpegQualityArgument = "jpeg_qality" }
        });
        Assert.Equal(["startmovie csvg_000 jpg wav jpeg_qality 90"], renamed.Batches[0].Commands);

        TickCommandPlan suppressed = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Jpg,
            JpegQuality = 90,
            EngineOutput = new InEngineEngineOutput { JpegQualityArgument = "" }
        });
        Assert.Equal(["startmovie csvg_000 jpg wav"], suppressed.Batches[0].Commands);

        TickCommandPlan defaulted = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Jpg, JpegQuality = null
        });
        Assert.Equal(["startmovie csvg_000 jpg wav"], defaulted.Batches[0].Commands);
    }

    [Fact]
    public async Task TgaCapture_NeverEmitsTheJpegQualityArgument()
    {
        TickCommandPlan plan = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Tga,
            JpegQuality = 95,
            EngineOutput = new InEngineEngineOutput { TgaFrameSizeBytes = 6220818 }
        });

        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], plan.SetupCommands);
        Assert.Equal(["startmovie csvg_000 tga wav"], plan.Batches[0].Commands);
    }

    /// <summary>
    ///     The restore value is configuration because the engine's own default has not been confirmed;
    ///     a hard-coded 1 would be a guess woven into the commands.
    /// </summary>
    [Fact]
    public async Task FocusSleepRestoreValue_IsConfigurable()
    {
        TickCommandPlan plan = await PlanAsync(new InEngineCaptureConfiguration
        {
            EngineOutput = new InEngineEngineOutput { FocusSleepRestoreValue = 0 }
        });

        Assert.Equal(["endmovie", "host_framerate 0", "engine_no_focus_sleep 0"], plan.Batches[1].Commands);
    }

    [Fact]
    public async Task MovieNamePrefix_IsPrependedToTheStartmovieArgument()
    {
        TickCommandPlan plan = await PlanAsync(new InEngineCaptureConfiguration
        {
            FrameFormat = InEngineFrameFormat.Jpg,
            EngineOutput = new InEngineEngineOutput { MovieNamePrefix = "csvg/" }
        });

        Assert.Equal(["startmovie csvg/csvg_000 jpg wav jpeg_quality 90"], plan.Batches[0].Commands);
    }

    [Fact]
    public async Task MovieName_IsIndexedByClip()
    {
        TickCommandPlan plan = await PlanAsync(clipIndex: 12);

        Assert.Equal(["startmovie csvg_012 jpg wav jpeg_quality 90"], plan.Batches[0].Commands);
    }

    [Fact]
    public async Task FrameRate_ComesFromTheClipMetadata()
    {
        TickCommandPlan plan = await PlanAsync(frameRate: 120);

        Assert.Equal("host_framerate 120", plan.SetupCommands[0]);
    }

    /// <summary>
    ///     The case the separate record batch used to exist for. A clip starting at the clamp put the
    ///     cvars and <c>startmovie</c> on the same tick, where only the batch boundary kept them in
    ///     order. There is nothing left to collapse: the recording tick carries <c>startmovie</c> alone,
    ///     and keeping the cvars ahead of it is now the coordinator's sequencing, pinned in
    ///     <see cref="InEngineCaptureCoordinationTests" />. Replaces
    ///     SetupAndRecordCollapseOntoTheClampTick_ButStayTwoOrderedBatches.
    /// </summary>
    [Fact]
    public async Task AClipStartingAtTheClamp_PutsOnlyStartmovieOnTheRecordingTick()
    {
        TickCommandPlan plan = await PlanAsync(startTick: 96, endTick: 736);

        Assert.Equal(96, plan.Batches[0].DemoTick);
        Assert.Equal(["startmovie csvg_000 jpg wav jpeg_quality 90"], plan.Batches[0].Commands);
        Assert.Equal(["host_framerate 60", "engine_no_focus_sleep 0"], plan.SetupCommands);
    }

    /// <summary>
    ///     A clip starting before the clamp records from the clamp, and says so - silently losing the
    ///     first ticks of a clip would be a defect that only shows up on playback.
    /// </summary>
    [Fact]
    public async Task StartTickBeforeTheClamp_IsClampedAndWarnedAbout()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();
        IClipCapture clip = await harness.Session.BeginClipAsync(harness.Clip(startTick: 10, endTick: 640),
            TestContext.Current.CancellationToken);

        Assert.NotNull(clip.TickPlan);
        Assert.Equal(96, clip.TickPlan.Batches[0].DemoTick);

        CaptureResult result = await clip.FinalizeAsync(TestContext.Current.CancellationToken);
        Assert.Contains(result.Warnings,
            warning => warning.Contains("tick 10", StringComparison.Ordinal) &&
                       warning.Contains("recording starts at tick 96", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartTickClamp_IsConfigurable()
    {
        TickCommandPlan plan = await PlanAsync(
            new InEngineCaptureConfiguration { StartTickClamp = 256 },
            startTick: 10,
            endTick: 640);

        Assert.Equal(256, plan.Batches[0].DemoTick);
    }

    /// <summary>
    ///     A degenerate range whose end falls before the clamped recording tick still has to end after
    ///     it starts, or the teardown would be scheduled before the recording it tears down.
    /// </summary>
    [Fact]
    public async Task EndTickNeverPrecedesTheRecordingTick()
    {
        TickCommandPlan plan = await PlanAsync(startTick: 10, endTick: 40);

        Assert.Equal(96, plan.Batches[0].DemoTick);
        Assert.Equal(96, plan.Batches[1].DemoTick);
    }

    /// <summary>
    ///     The abort commands are the teardown batch. A tick-scheduled endmovie is no use to a clip
    ///     that died mid-playback: the tick it waits for never arrives, and the engine keeps writing
    ///     frames.
    /// </summary>
    [Fact]
    public async Task AbortCommands_AreTheTeardownBatch()
    {
        TickCommandPlan plan = await PlanAsync();

        Assert.Equal(plan.Batches[1].Commands, plan.AbortCommands);
        Assert.Equal(["endmovie", "host_framerate 0", "engine_no_focus_sleep 1"], plan.AbortCommands);
    }

    /// <summary>
    ///     The batches are handed over in the order they have to be armed in, because that is the order
    ///     the coordinator registers them in and the plugin's same-tick ordering follows registration.
    /// </summary>
    [Fact]
    public async Task BatchesAreOrderedByTick()
    {
        TickCommandPlan plan = await PlanAsync(startTick: 96, endTick: 736);

        Assert.True(plan.Batches[0].DemoTick <= plan.Batches[1].DemoTick);
    }

    /// <summary>
    ///     The provider is engine-coupled, offline and frame-rate-honoring, and cannot pause: pausing
    ///     under host_framerate keeps rendering the same moment over and over.
    /// </summary>
    [Fact]
    public async Task Traits_DescribeAnOfflineEngineCoupledBackend()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        Assert.Equal(CaptureTimingModel.TickExact, harness.Session.Traits.TimingModel);
        Assert.True(harness.Session.Traits.UsesEngine);
        Assert.True(harness.Session.Traits.HonorsFrameRate);
        Assert.False(harness.Session.Traits.SupportsPauseResume);
    }
}
