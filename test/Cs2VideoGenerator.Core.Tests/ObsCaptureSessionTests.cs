#region

using System.Reflection;
using Cs2VideoGenerator.Core.Cache;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Obs.NET;
using Obs.NET.Requests;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The OBS capture triplet (<see cref="ObsCaptureSessionFactory" />,
///     <see cref="ObsCaptureSession" />, <see cref="ObsClipCapture" />). Covers the defect
///     fixes that still have a home after the M3 re-partition: null logger support (no NRE from
///     source-generated log calls or the process manager), availability failures reported as issues
///     rather than exceptions, clip finalize returning capture start/end times (they used to be
///     nulled before the copy), and the metrics registration lifecycle (weak accessor, unregister on
///     dispose). Then the audio-capture policy: mute OBS inputs when CaptureAudio=false,
///     restore on finalize, shutdown and dispose. It is one class because capture-session
///     construction mutates the static metrics registry, and xUnit runs same-class tests
///     sequentially.
/// </summary>
public class ObsCaptureSessionTests
{
    /// <summary>
    ///     Item 2: a null logger must be genuinely supported - constructing the factory and session and
    ///     running an availability check (which logs) must not throw.
    /// </summary>
    [Fact]
    public async Task NullLogger_IsSupported_ConstructionAndAvailabilityCheckDoNotThrow()
    {
        // An executable path that cannot exist keeps the result deterministic on every platform
        // (the old test got determinism from passing a wrong-typed configuration, which the
        // factory's bound ObsCaptureConfiguration now makes impossible).
        ObsCaptureSessionFactory factory = CreateFactory(new ObsCaptureConfiguration
        {
            ExecutablePath = MissingObsPath()
        });

        CaptureAvailabilityResult result = await factory.CheckAvailabilityAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);

        await using ObsCaptureSession session = CreateSession();
        Assert.Equal(ObsCaptureSessionFactory.ProviderKey, session.ProviderName);
    }

    /// <summary>
    ///     Item 3 (surviving half): a failed availability check reports its issues in the result instead
    ///     of throwing. The "wrong configuration type" branch it also covered - and the provider state
    ///     reset that branch used to skip - are gone by construction: the factory is bound to
    ///     <see cref="ObsCaptureConfiguration" /> and holds no capture state.
    /// </summary>
    [Fact]
    public async Task CheckAvailability_MissingExecutable_ReportsIssueWithoutThrowing()
    {
        string missingPath = MissingObsPath();
        ObsCaptureSessionFactory factory = CreateFactory(new ObsCaptureConfiguration
        {
            ExecutablePath = missingPath
        });

        CaptureAvailabilityResult result = await factory.CheckAvailabilityAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Contains(result.Issues, i => i.Contains(missingPath));
    }

    /// <summary>
    ///     OBS records the CS2 window in real time and issues no engine commands, so its traits say
    ///     wall-clock timing, no engine use and frame rate not honored - and its clips publish no tick
    ///     plan, which is what keeps the coordinator's capability gate out of the OBS path entirely.
    /// </summary>
    [Fact]
    public async Task Traits_DescribeWallClockScreenRecording_AndClipsPublishNoTickPlan()
    {
        await using ObsCaptureSession session = CreateSession();

        Assert.Equal(CaptureTimingModel.WallClock, session.Traits.TimingModel);
        Assert.False(session.Traits.UsesEngine);
        Assert.False(session.Traits.HonorsFrameRate);
        Assert.True(session.Traits.SupportsPauseResume);

        ObsClipCapture clip = new(session, ClipContext(new CaptureMetadata
        {
            OutputFileName = "clip_000",
            OutputDirectory = Path.GetTempPath()
        }));

        Assert.Null(clip.TickPlan);
    }

    /// <summary>
    ///     Per-clip readiness guard: the session-level verification runs once at bring-up, so a
    ///     recording that starts afterwards (the user pressed record, or a previous clip's stop never
    ///     landed) has to be caught before StartRecord folds this clip into that file. A status OBS
    ///     could not answer is deliberately not treated as "recording".
    /// </summary>
    [Fact]
    public void StartRecord_WhenObsIsAlreadyRecording_IsRejected()
    {
        ObsClipCapture.ThrowIfAlreadyRecording(null);
        ObsClipCapture.ThrowIfAlreadyRecording(RecordStatus(outputActive: false));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            ObsClipCapture.ThrowIfAlreadyRecording(RecordStatus(outputActive: true)));

        Assert.Contains("already recording", ex.Message);
    }

    private static GetRecordStatusResponse RecordStatus(bool outputActive) => new()
    {
        OutputActive = outputActive,
        OutputPaused = false,
        OutputTimecode = "00:00:00.000",
        OutputDuration = 0,
        OutputBytes = 0
    };

    /// <summary>
    ///     Item 1: finalize copies the capture start/end times into the result before clearing them
    ///     (StartTime/EndTime used to come back null, and Duration was right only by luck of
    ///     ordering), and renames OBS's timestamped file to the requested name.
    /// </summary>
    [Fact]
    public async Task FinalizeClip_ReturnsStartAndEndTimes_AndRenamesOutputFile()
    {
        using TempDir tempDir = new("csvg-obscapture-tests-");
        await using ObsCaptureSession session = CreateSession();

        DateTime startTime = DateTime.UtcNow.AddSeconds(-30);
        DateTime endTime = DateTime.UtcNow;

        // The output file "OBS" wrote; finalize only inspects/renames it, no OBS calls needed.
        string obsOutputPath = tempDir.Combine("2026-07-08 12-00-00.mp4");
        await File.WriteAllBytesAsync(obsOutputPath, new byte[] { 1, 2, 3, 4 },
            TestContext.Current.CancellationToken);

        CaptureMetadata metadata = new()
        {
            OutputFileName = "clip_000",
            OutputDirectory = tempDir.Path,
            ContainerFormat = "mp4"
        };

        ObsClipCapture clip = new(session, ClipContext(metadata));
        SetPrivateField(clip, "_lastOutputPath", obsOutputPath);
        SetPrivateField(clip, "_captureStartTime", startTime);
        SetPrivateField(clip, "_captureEndTime", endTime);

        CaptureResult result = await clip.FinalizeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(startTime, result.StartTime);
        Assert.Equal(endTime, result.EndTime);
        Assert.Equal(endTime - startTime, result.Duration);
        Assert.Equal(metadata.GetFullOutputPath(), result.OutputFilePath);
        Assert.True(File.Exists(metadata.GetFullOutputPath()));
    }

    /// <summary>
    ///     Item 5: constructing a capture session registers it with the static metrics registry;
    ///     disposing it must unregister so the static registry never samples (or roots) a disposed
    ///     instance.
    /// </summary>
    [Fact]
    public async Task Dispose_UnregistersCaptureProviderFromMetrics()
    {
        ObsCaptureSession session = CreateSession();

        Assert.NotNull(CsvgMetricsSampler.CaptureProviderNameProvider);
        Assert.Equal(ObsCaptureSessionFactory.ProviderKey,
            CsvgMetricsSampler.CaptureProviderNameProvider!.Invoke());

        await session.DisposeAsync();

        Assert.Null(CsvgMetricsSampler.CaptureProviderStatusProvider);
        Assert.Null(CsvgMetricsSampler.CaptureProviderNameProvider);
    }

    /// <summary>
    ///     Item 5: last registration wins - disposing an old session must not clobber the registration
    ///     of a newer one, which is exactly what a Start→Stop→Start cycle now produces.
    /// </summary>
    [Fact]
    public async Task DisposingOlderSession_DoesNotClobberNewerRegistration()
    {
        ObsCaptureSession older = CreateSession();
        ObsCaptureSession newer = CreateSession();

        await older.DisposeAsync();

        Assert.NotNull(CsvgMetricsSampler.CaptureProviderNameProvider);
        Assert.Equal(ObsCaptureSessionFactory.ProviderKey,
            CsvgMetricsSampler.CaptureProviderNameProvider!.Invoke());

        await newer.DisposeAsync();
        Assert.Null(CsvgMetricsSampler.CaptureProviderNameProvider);
    }

    /// <summary>
    ///     Item 5: the registered accessor holds the session weakly - once the instance is unreachable
    ///     and collected, the accessor returns null instead of rooting it forever.
    /// </summary>
    [Fact]
    public void MetricsAccessor_HoldsSessionWeakly()
    {
        WeakReference weakSession = CreateUnreferencedSession();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakSession.IsAlive);
        Assert.NotNull(CsvgMetricsSampler.CaptureProviderNameProvider);
        Assert.Null(CsvgMetricsSampler.CaptureProviderNameProvider!.Invoke());

        // Clean up the (now dead) registration so later tests start from a clean slate.
        CsvgMetricsSampler.CaptureProviderStatusProvider = null;
        CsvgMetricsSampler.CaptureProviderNameProvider = null;
    }

    /// <summary>
    ///     Creates a capture session in a non-inlined method so the local reference is collectible.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnreferencedSession() => new(CreateSession());

    #region Audio capture policy

    /// <summary>CaptureAudio=true is the default, and it issues no mute calls at all.</summary>
    [Fact]
    public async Task ApplyAudioCapturePolicy_CaptureAudioEnabled_IssuesNoMuteCalls()
    {
        await using ObsCaptureSession session = CreateSession();
        FakeObsAudioClient fake = new();
        fake.SpecialInputNames.Add("Desktop Audio");
        fake.MuteStates["Desktop Audio"] = false;

        SetPrivateField(session, "_audioMuteController",
            new ObsAudioMuteController(fake, NullLogger.Instance));

        await session.ApplyAudioCapturePolicyAsync(new CaptureMetadata
        {
            OutputFileName = "clip_000",
            OutputDirectory = Path.GetTempPath(),
            CaptureAudio = true
        }, TestContext.Current.CancellationToken);

        Assert.Empty(fake.GetMuteCalls);
        Assert.Empty(fake.SetMuteCalls);
    }

    [Fact]
    public async Task ApplyAudioCapturePolicy_CaptureAudioDisabled_MutesAudioInputs()
    {
        await using ObsCaptureSession session = CreateSession();
        FakeObsAudioClient fake = new();
        fake.SpecialInputNames.Add("Desktop Audio");
        fake.MuteStates["Desktop Audio"] = false;

        SetPrivateField(session, "_audioMuteController",
            new ObsAudioMuteController(fake, NullLogger.Instance));

        await session.ApplyAudioCapturePolicyAsync(new CaptureMetadata
        {
            OutputFileName = "clip_000",
            OutputDirectory = Path.GetTempPath(),
            CaptureAudio = false
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ("Desktop Audio", true) }, fake.SetMuteCalls);
        Assert.True(fake.MuteStates["Desktop Audio"]);
    }

    /// <summary>
    ///     Finalizing a clip restores the prior OBS mute states on the success path, and an
    ///     input the user had already muted is left alone.
    /// </summary>
    [Fact]
    public async Task FinalizeClip_RestoresAudioInputMuteStates()
    {
        using TempDir tempDir = new("csvg-obscapture-tests-");
        await using ObsCaptureSession session = CreateSession();

        FakeObsAudioClient fake = new();
        fake.SpecialInputNames.AddRange(["Desktop Audio", "Mic/Aux"]);
        fake.MuteStates["Desktop Audio"] = false;
        fake.MuteStates["Mic/Aux"] = true; // already muted by the user - must stay muted

        ObsAudioMuteController controller = new(fake, NullLogger.Instance);
        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        Assert.True(fake.MuteStates["Desktop Audio"]);

        string obsOutputPath = tempDir.Combine("2026-07-08 12-00-00.mp4");
        await File.WriteAllBytesAsync(obsOutputPath, new byte[] { 1, 2, 3, 4 },
            TestContext.Current.CancellationToken);

        SetPrivateField(session, "_audioMuteController", controller);

        ObsClipCapture clip = new(session, ClipContext(new CaptureMetadata
        {
            OutputFileName = "clip_000",
            OutputDirectory = tempDir.Path,
            ContainerFormat = "mp4",
            CaptureAudio = false
        }));
        SetPrivateField(clip, "_lastOutputPath", obsOutputPath);

        CaptureResult result = await clip.FinalizeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(fake.MuteStates["Desktop Audio"]); // restored to unmuted
        Assert.True(fake.MuteStates["Mic/Aux"]); // stays muted, exactly as the user had it
        Assert.DoesNotContain(fake.SetMuteCalls, call => call.Name == "Mic/Aux");
    }

    /// <summary>
    ///     When a capture fails mid-clip and the clip never finalizes, disposing the session is
    ///     the backstop that restores the OBS mute states before the connection goes away.
    /// </summary>
    [Fact]
    public async Task Dispose_AfterMidCaptureFailure_RestoresAudioInputMuteStates()
    {
        ObsCaptureSession session = CreateSession();

        FakeObsAudioClient fake = new();
        fake.SpecialInputNames.Add("Desktop Audio");
        fake.MuteStates["Desktop Audio"] = false;

        ObsAudioMuteController controller = new(fake, NullLogger.Instance);
        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        Assert.True(fake.MuteStates["Desktop Audio"]);

        // Simulate a mid-capture failure: mutes applied, finalize never runs, session disposed.
        SetPrivateField(session, "_audioMuteController", controller);

        await session.DisposeAsync();

        Assert.False(fake.MuteStates["Desktop Audio"]);
    }

    /// <summary>
    ///     Shutdown is the reversible teardown that runs before CS2 is killed, so it has to restore
    ///     mutes just like disposal does - otherwise a session stopped between runs leaves the user's
    ///     OBS muted.
    /// </summary>
    [Fact]
    public async Task Shutdown_RestoresAudioInputMuteStates()
    {
        await using ObsCaptureSession session = CreateSession();

        FakeObsAudioClient fake = new();
        fake.SpecialInputNames.Add("Desktop Audio");
        fake.MuteStates["Desktop Audio"] = false;

        ObsAudioMuteController controller = new(fake, NullLogger.Instance);
        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        SetPrivateField(session, "_audioMuteController", controller);

        await session.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.False(fake.MuteStates["Desktop Audio"]);
    }

    #endregion

    #region OBS readiness gate (207 "not ready" detection)

    /// <summary>
    ///     The readiness gate only retries the transient "OBS not ready" condition, which
    ///     obs-websocket reports as RequestStatus 207. An exception carrying <see cref="ObsRequestException.Code" />
    ///     207 must be recognized as not-ready.
    /// </summary>
    [Fact]
    public void IsObsNotReady_Code207_ReturnsTrue()
    {
        ObsRequestException ex = new(
            "OBS request 'SetRecordDirectory' failed with code 207: OBS is not ready to perform the request.",
            207,
            "OBS is not ready to perform the request.");

        Assert.True(ObsCaptureSession.IsObsNotReady(ex));
        Assert.Equal(207, ObsCaptureSession.ObsNotReadyStatusCode);
    }

    /// <summary>
    ///     Any other RequestStatus code is a genuine failure that retrying will not fix, so it must
    ///     not be treated as not-ready: the gate rethrows immediately instead of looping.
    /// </summary>
    [Theory]
    [InlineData(100, "Unknown request type.")]
    [InlineData(604, "The resource was not found.")]
    [InlineData(500, "Request processing failed.")]
    public void IsObsNotReady_OtherCodes_ReturnFalse(int code, string comment)
    {
        ObsRequestException ex = new($"OBS request 'X' failed with code {code}: {comment}", code, comment);

        Assert.False(ObsCaptureSession.IsObsNotReady(ex));
    }

    /// <summary>
    ///     An exception raised without status details (null <see cref="ObsRequestException.Code" />, e.g. a
    ///     transport or deserialization failure) must be treated as a genuine failure, not silently
    ///     retried until timeout.
    /// </summary>
    [Fact]
    public void IsObsNotReady_ExceptionWithoutStatusCode_ReturnsFalse()
    {
        Assert.False(ObsCaptureSession.IsObsNotReady(new ObsRequestException("Failed to deserialize response data")));
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     A session context for OBS, which never uses the engine beyond holding the console handle.
    /// </summary>
    private static CaptureSessionContext Context() => new() { Engine = new FakeEngineConsole() };

    private static ObsCaptureSession CreateSession(ObsCaptureConfiguration? configuration = null) =>
        new(configuration ?? new ObsCaptureConfiguration(), Context(), logger: null, pathDiscoveryService: null);

    private static ObsCaptureSessionFactory CreateFactory(ObsCaptureConfiguration configuration) =>
        new(Options.Create(configuration),
            new ObsProcessManager(NullLoggerFactory.Instance,
                CachedPathDiscoveryService.CreateDefault(NullLoggerFactory.Instance)));

    private static string MissingObsPath() =>
        Path.Combine(Path.GetTempPath(), $"csvg-missing-obs-{Guid.NewGuid():N}");

    private static ClipCaptureContext ClipContext(CaptureMetadata metadata) => new()
    {
        StartTick = 100,
        EndTick = 200,
        Metadata = metadata
    };

    private static void SetPrivateField(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    #endregion
}
