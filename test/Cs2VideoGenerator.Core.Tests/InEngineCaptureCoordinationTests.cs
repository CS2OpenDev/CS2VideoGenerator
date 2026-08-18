#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The in-engine provider driven through the real <see cref="ClipCaptureCoordinator" />, which is
///     what actually arms its brackets. The provider only declares a plan; whether that plan reaches
///     the engine in the right order, and what happens when the plugin cannot arm it, is a property of
///     the pair.
/// </summary>
public class InEngineCaptureCoordinationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The cvars go out immediately, the batches are armed, and both happen before playback is
    ///     requested. Registration order is what the plugin's same-tick ordering follows; the cvars are
    ///     ahead of every batch by being on the other channel entirely.
    /// </summary>
    [Fact]
    public async Task TheSetupAndTheBracketsBothReachTheEngine_BeforePlaybackStarts()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        CaptureJournal journal = new();
        FakeEngineSession engine = Engine(journal);
        engine.PlaybackBehavior = (_, _, _, _) =>
        {
            harness.WriteFrames("csvg_000", 60);
            harness.WriteWav("csvg_000", 1.0);
            return Task.CompletedTask;
        };

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 96, endTick: 736), Ct);

        Assert.True(result.Success, result.ErrorMessage);

        Assert.Equal([["host_framerate 60", "engine_no_focus_sleep 0"]], engine.ExecutedConsoleCommands);

        Assert.Equal(2, engine.ScheduledBatches.Count);
        Assert.Equal(["startmovie csvg_000 jpg wav jpeg_quality 90"], engine.ScheduledBatches[0].Commands);
        Assert.Equal(["endmovie", "host_framerate 0", "engine_no_focus_sleep 1"],
            engine.ScheduledBatches[1].Commands);

        List<string> entries = [.. journal.Entries];
        int lastArmed = entries.FindLastIndex(entry => entry.StartsWith("schedule@", StringComparison.Ordinal));
        int setup = entries.FindIndex(entry => entry.StartsWith("console-exec:host_framerate",
            StringComparison.Ordinal));
        int played = entries.FindIndex(entry => entry.StartsWith("play:", StringComparison.Ordinal));
        Assert.True(lastArmed >= 0 && played > lastArmed,
            $"every batch must be armed before playback: {string.Join(", ", journal.Entries)}");
        Assert.True(setup >= 0 && played > setup,
            $"the cvars must reach the engine before playback: {string.Join(", ", journal.Entries)}");
    }

    /// <summary>
    ///     The settled degradation rule. A backend that needs engine brackets cannot run against a
    ///     plugin that cannot arm them, and the clip fails naming the capability rather than recording
    ///     nothing and calling it a success.
    /// </summary>
    [Fact]
    public async Task APluginWithoutTickScheduling_FailsTheClipNamingTheCapability()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = new();
        ClipCaptureCoordinator coordinator = new();

        CaptureResult result = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(), Ct);

        Assert.False(result.Success);
        Assert.Contains(CsvgCapabilities.TickScheduledCommands, result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(engine.PlayedRanges);
        Assert.Empty(engine.ScheduledBatches);
    }

    /// <summary>
    ///     The same rule for the other half of the plan. The cvars are not tick-scheduled any more, so a
    ///     plugin that cannot run an immediate command cannot put the engine into the state the
    ///     recording needs - and <c>startmovie</c> without <c>host_framerate</c> records in real time
    ///     with the wrong frame count and reports success. Failing loudly is the only honest answer.
    /// </summary>
    [Fact]
    public async Task APluginWithoutConsoleExec_FailsTheClipNamingTheCapability()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = new();
        engine.AdvertisedCapabilities.Add(CsvgCapabilities.TickScheduledCommands);

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(), Ct);

        Assert.False(result.Success);
        Assert.Contains(CsvgCapabilities.ConsoleExec, result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(engine.PlayedRanges);
        Assert.Empty(engine.ScheduledBatches);
        Assert.Empty(Directory.GetFiles(harness.StagingDirectory));
    }

    /// <summary>
    ///     A clip that dies mid-playback leaves the engine recording, and its tick-scheduled endmovie is
    ///     waiting for a tick that will never arrive. The abort commands are the only way out, and they
    ///     go through the immediate console - the same channel the setup used on the way in.
    /// </summary>
    [Fact]
    public async Task AFailedClip_StopsTheEngineRecordingImmediately()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = Engine();
        engine.PlaybackBehavior = (_, _, _, _) => throw new TimeoutException("playback never reached the end tick");

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result = await coordinator.CaptureClipAsync(engine, harness.Session, harness.Clip(), Ct);

        Assert.False(result.Success);
        Assert.Equal(
        [
            ["host_framerate 60", "engine_no_focus_sleep 0"],
            ["endmovie", "host_framerate 0", "engine_no_focus_sleep 1"]
        ], engine.ExecutedConsoleCommands);
        Assert.Equal(1, engine.ClearScheduledCommandsCallCount);
    }

    /// <summary>
    ///     The defect this whole channel exists for, end to end. A compilation reloads the demo only
    ///     when the demo path changes, so the second clip from one demo starts from wherever the first
    ///     one left the playhead. The cvar setup used to be armed 64 ticks ahead of the recording tick,
    ///     which for two clips less than a second apart is behind that playhead - and a batch armed at
    ///     or below the last observed tick is stranded for good. <c>startmovie</c> still fired, so the
    ///     clip recorded, succeeded, and was silently wrong: real-time capture instead of a fixed
    ///     timestep, because <c>host_framerate</c> was never set.
    /// </summary>
    [Fact]
    public async Task TwoClipsFromOneDemoLessThanASecondApart_BothGetTheirCvarSetup()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = Engine();
        ClipCaptureCoordinator coordinator = new();

        engine.PlaybackBehavior = Playback(harness, engine, "csvg_000");
        CaptureResult first = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 1000, endTick: 1640), Ct);
        Assert.True(first.Success, first.ErrorMessage);

        // The playhead carries over, which is what puts the second clip's old setup tick (1660 - 64 =
        // 1596) behind the last observation and stranded it.
        Assert.Equal(1640, engine.LastObservedTick);

        engine.PlaybackBehavior = Playback(harness, engine, "csvg_001");
        CaptureResult second = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 1660, endTick: 2000, clipIndex: 1), Ct);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(2, TimesTheEngineRan(engine, "host_framerate 60"));
    }

    /// <summary>
    ///     What is left of the same defect after the cvars moved off the scheduler, and the reason the
    ///     report recommends an overlap rule in <c>Cs2Compilation.Validate()</c>. <c>startmovie</c> does
    ///     need a tick, so it can still be armed at or below the playhead: a clip starting exactly where
    ///     the previous one ended never sees it fire, records nothing and fails. Documented in
    ///     docs/in-engine-capture.md; this test is the tripwire for anyone who fixes it.
    /// </summary>
    [Fact]
    public async Task AClipStartingWhereThePreviousOneEnded_NeverReceivesStartmovie()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = Engine();
        ClipCaptureCoordinator coordinator = new();

        engine.PlaybackBehavior = Playback(harness, engine, "csvg_000");
        CaptureResult first = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 1000, endTick: 1640), Ct);
        Assert.True(first.Success, first.ErrorMessage);

        engine.PlaybackBehavior = Playback(harness, engine, "csvg_001");
        CaptureResult second = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 1640, endTick: 2000, clipIndex: 1), Ct);

        Assert.False(second.Success);
        Assert.Contains("wrote no frames", second.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, TimesTheEngineRan(engine, "startmovie csvg_001 jpg wav jpeg_quality 90"));

        // The setup did arrive - it no longer needs a tick to arrive on. Only the batch is stranded.
        Assert.Equal(2, TimesTheEngineRan(engine, "host_framerate 60"));
    }

    /// <summary>
    ///     The whole point of the provider, seen from the outside: game time out, wall clock nowhere
    ///     near it.
    /// </summary>
    [Fact]
    public async Task ACapturedClipReportsGameTime()
    {
        await using InEngineTestHarness harness = await InEngineTestHarness.CreateAsync();

        FakeEngineSession engine = Engine();
        engine.PlaybackBehavior = (_, _, _, _) =>
        {
            harness.WriteFrames("csvg_000", 600);
            harness.WriteWav("csvg_000", 10.0);
            return Task.CompletedTask;
        };

        ClipCaptureCoordinator coordinator = new();
        CaptureResult result = await coordinator.CaptureClipAsync(
            engine, harness.Session, harness.Clip(startTick: 1000, endTick: 1640), Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Duration);
        Assert.Equal(600, result.TotalFrames);
    }

    #region Helpers

    /// <summary>
    ///     An engine advertising everything the in-engine provider needs: the brackets are tick
    ///     scheduled and the setup is not.
    /// </summary>
    private static FakeEngineSession Engine(CaptureJournal? journal = null)
    {
        FakeEngineSession engine = new(journal);
        engine.AdvertisedCapabilities.Add(CsvgCapabilities.TickScheduledCommands);
        engine.AdvertisedCapabilities.Add(CsvgCapabilities.ConsoleExec);
        return engine;
    }

    /// <summary>
    ///     Playback that moves the demo the way the plugin sees it move: the seek arrival is observed
    ///     first, which fires whatever the crossing reached, then the range is rendered, then the end
    ///     tick is observed. Frames appear only if <c>startmovie</c> actually fired - nothing else makes
    ///     the engine write any, and a test that wrote them regardless could not tell a recording from a
    ///     stranded batch.
    /// </summary>
    private static Func<int, int, TimeSpan?, CancellationToken, Task> Playback(
        InEngineTestHarness harness, FakeEngineSession engine, string movieName, int frames = 60)
    {
        return (startTick, endTick, _, _) =>
        {
            engine.RaiseTickObserved(startTick);

            // Prefix match, not the whole line: the arguments after the movie name (format, wav,
            // jpeg_quality) are the provider's business, and the fake engine, like the real one,
            // starts recording for any of them.
            if (TimesTheEngineRanStartmovieFor(engine, movieName) > 0)
            {
                harness.WriteFrames(movieName, frames);
                harness.WriteWav(movieName, frames / 60.0);
            }

            engine.RaiseTickObserved(endTick);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    ///     How many times the engine actually ran a command, whichever channel carried it. Which channel
    ///     that was is the implementation detail under test; that the engine ran it is the property.
    /// </summary>
    private static int TimesTheEngineRan(FakeEngineSession engine, string command) =>
        engine.FiredBatches.Concat(engine.ExecutedConsoleCommands).Count(batch => batch.Contains(command));

    /// <summary>
    ///     How many times the engine was asked to start recording this movie, whatever the rest of the
    ///     startmovie line said.
    /// </summary>
    private static int TimesTheEngineRanStartmovieFor(FakeEngineSession engine, string movieName) =>
        engine.FiredBatches.Concat(engine.ExecutedConsoleCommands).Count(batch =>
            batch.Any(command => command.StartsWith($"startmovie {movieName}", StringComparison.Ordinal)));

    #endregion
}
