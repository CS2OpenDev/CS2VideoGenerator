#region

using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     <see cref="Cs2EngineSession.SetDemoTimescaleAsync" /> and its
///     <see cref="Cs2EngineSession.LastKnownTimescale" /> tracking, the timescale-aware
///     <see cref="Cs2EngineSession.ComputeDefaultPlaybackTimeout" /> math, and the
///     <see cref="CsvgVideoSession.StartWatchAsync" /> live-sync bootstrap.
/// </summary>
public class Cs2EngineSessionTimescaleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Non-finite, non-positive, or above the 8.0 sanity bound - all rejected before any
    ///     capability or session check.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(8.001f)]
    [InlineData(100f)]
    public async Task SetDemoTimescale_InvalidValue_ThrowsArgumentOutOfRange(float timescale)
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.SetDemoTimescaleAsync(timescale, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetDemoTimescale_SendsCommand_AndUpdatesLastKnownTimescale()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        List<CsControlRequest> sent =
            await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.TimescaleSet);
        Assert.Null(session.LastKnownTimescale);

        await session.SetDemoTimescaleAsync(2.0f, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetDemoTimescale, command.CommandCase);
        Assert.Equal(2.0f, command.SetDemoTimescale.Timescale);
        Assert.Equal(2.0f, session.LastKnownTimescale);
    }

    /// <summary>The upper bound is inclusive.</summary>
    [Fact]
    public async Task SetDemoTimescale_ExactUpperBound_IsAccepted()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.ConnectWithCapabilitiesAsync("session-1", CsvgCapabilities.TimescaleSet);

        await session.SetDemoTimescaleAsync(8.0f, TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(8.0f, session.LastKnownTimescale);
    }

    /// <summary>
    ///     A failed send must not update the tracked timescale - the plugin never applied it.
    /// </summary>
    [Fact]
    public async Task SetDemoTimescale_FailedSend_DoesNotUpdateLastKnownTimescale()
    {
        Cs2EngineSession session = SessionHarness.CreateEngineSession();
        await session.OnSessionStartedAsync("session-1");
        session.OnCommandSenderAvailable("session-1",
            _ => throw new InvalidOperationException("session disconnected"));
        Cs2ProcessStatus status = new() { Status = Cs2ProcessStatus.Types.ProcessStatus.Ready };
        status.Capabilities.Add(CsvgCapabilities.TimescaleSet);
        await session.OnProcessStatusChangedAsync("session-1", status);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SetDemoTimescaleAsync(2.0f, TestContext.Current.CancellationToken)
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Null(session.LastKnownTimescale);
    }

    /// <summary>
    ///     The 64 t/s wall-clock estimate divided by the effective timescale (plan §5.4). Unknown
    ///     falls back to 1.0; the divisor is clamped to 0.25 so extreme slow motion cannot inflate
    ///     the default without bound. The +30 s buffer is unchanged.
    /// </summary>
    [Theory]
    [InlineData(0, 6400, null, 130.0)] // 100 s at 1x (unknown -> 1.0) + 30 s buffer
    [InlineData(0, 6400, 1.0f, 130.0)] // explicit 1x matches the legacy math
    [InlineData(0, 6400, 2.0f, 80.0)] // double speed halves the estimate: 50 + 30
    [InlineData(0, 6400, 0.5f, 230.0)] // half speed doubles it: 200 + 30
    [InlineData(0, 6400, 0.1f, 430.0)] // clamped to 0.25: 400 + 30 (not 1000 + 30)
    [InlineData(0, 6400, 8.0f, 42.5)] // 12.5 + 30
    [InlineData(1000, 1000, null, 30.0)] // empty range: buffer only
    public void ComputeDefaultPlaybackTimeout_DividesByEffectiveTimescale(
        int startTick, int endTick, float? lastKnownTimescale, double expectedSeconds)
    {
        TimeSpan timeout = Cs2EngineSession.ComputeDefaultPlaybackTimeout(startTick, endTick, lastKnownTimescale);

        Assert.Equal(expectedSeconds, timeout.TotalSeconds, precision: 6);
    }

    /// <summary>
    ///     The live-sync bootstrap: playback-only session, capture never initialized, then
    ///     SetTickUpdateFrequency(0) for every-tick updates.
    /// </summary>
    [Fact]
    public async Task StartWatchSession_ConnectsAndSetsTickFrequencyZero()
    {
        await using CsvgVideoSession videoSession =
            SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        List<CsControlRequest> sent = [];

        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (int? _, int? _, bool _, CancellationToken _) =>
            {
                Cs2EngineSession engine = videoSession.EngineSession();
                await engine.OnSessionStartedAsync("session-1");
                engine.OnCommandSenderAvailable("session-1", cmd =>
                {
                    lock (sent)
                    {
                        sent.Add(cmd);
                    }

                    return Task.CompletedTask;
                });
                await engine.OnProcessStatusChangedAsync("session-1",
                    new Cs2ProcessStatus { Status = Cs2ProcessStatus.Types.ProcessStatus.Ready });
            });

        await videoSession.StartWatchAsync(cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(CsvgSessionState.Connected, videoSession.State);
        CsControlRequest command = Assert.Single(sent);
        Assert.Equal(CsControlRequest.CommandOneofCase.SetTickUpdateFrequency, command.CommandCase);
        Assert.Equal(0, command.SetTickUpdateFrequency.Frequency);
    }
}
