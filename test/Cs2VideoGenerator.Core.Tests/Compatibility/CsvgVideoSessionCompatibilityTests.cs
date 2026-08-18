#region

using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests.Compatibility;

/// <summary>
///     Session-level tests for the plugin/game version handshake and compatibility check,
///     driven through in-memory gRPC streams like <see cref="Cs2GameSessionTests" />.
///     Policy under test: newer/unknown versions connect with a warning; only known-broken pairs
///     block, and <c>--force</c> (ForceIncompatiblePlugin) overrides even those.
/// </summary>
public class CsvgVideoSessionCompatibilityTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly TempDir _tempDir = new("csvg-compat-");

    public void Dispose()
    {
        _tempDir.Dispose();
        GC.SuppressFinalize(this);
    }

    private string WriteOverrideFile(string json)
    {
        string path = _tempDir.Combine($"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string BrokenPairOverrideJson =
        """
        {
          "builtForCs2Build": "14093",
          "knownBrokenVersions": [
            { "gameBuild": "14200", "reason": "test: engine interface changed" }
          ]
        }
        """;

    /// <summary>
    ///     Starts a video session whose fake plugin immediately reports Ready with the given handshake
    ///     status, and returns the start task plus the harness pieces for cleanup.
    /// </summary>
    private static (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask)
        StartSessionWithStatus(Cs2ProcessStatus readyStatus, Cs2VideoGeneratorOptions options)
    {
        CsvgVideoSession videoSession =
            SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager, options);

        FakeStreamReader reader = new();
        FakeStreamWriter writer = new();
        Task? runTask = null;

        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns((int? _, int? _, bool _, CancellationToken _) =>
            {
                Cs2GameSession session = new("session-compat", reader, writer, CancellationToken.None,
                    NullLogger<Cs2GameSession>.Instance, videoSession.EngineHost);
                runTask = session.RunAsync();
                reader.Write(new CsProcessEvent { ProcessStatus = readyStatus });
                return Task.CompletedTask;
            });

        Task startTask = videoSession.StartAsync(TestContext.Current.CancellationToken);
        return (videoSession, startTask, reader, () => runTask);
    }

    private static Cs2ProcessStatus ReadyStatus(string? pluginVersion = null, string? gameVersion = null,
        string? hl2SdkCommit = null)
    {
        Cs2ProcessStatus status = new() { Status = Cs2ProcessStatus.Types.ProcessStatus.Ready };
        if (pluginVersion is not null)
        {
            status.PluginVersion = pluginVersion;
        }

        if (gameVersion is not null)
        {
            status.GameVersion = gameVersion;
        }

        if (hl2SdkCommit is not null)
        {
            status.Hl2SdkCommit = hl2SdkCommit;
        }

        return status;
    }

    private static async Task EndSessionAsync(CsvgVideoSession session, FakeStreamReader reader, Func<Task?> runTask)
    {
        reader.Complete();
        Task? task = runTask();
        if (task is not null)
        {
            await task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        await session.StopAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await session.DisposeAsync();
    }

    /// <summary>
    ///     Handshake plumbing: the version fields sent by the plugin surface on
    ///     <see cref="Core.Engine.ICs2EngineSession.LastProcessStatus" /> untouched.
    /// </summary>
    [Fact]
    public async Task Handshake_VersionFields_AreSurfacedOnLastProcessStatus()
    {
        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus("1.0.1", "14100", "5f891c9"), new Cs2VideoGeneratorOptions());

        await startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.NotNull(session.Engine.LastProcessStatus);
        Assert.Equal("1.0.1", session.Engine.LastProcessStatus!.PluginVersion);
        Assert.Equal("14100", session.Engine.LastProcessStatus.GameVersion);
        Assert.Equal("5f891c9", session.Engine.LastProcessStatus.Hl2SdkCommit);

        await EndSessionAsync(session, reader, runTask);
    }

    /// <summary>
    ///     An old plugin that predates the handshake (no version fields) must connect normally -
    ///     unknown versions are a warning, never an error.
    /// </summary>
    [Fact]
    public async Task StartSession_OldPluginWithoutVersionFields_Connects()
    {
        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus(), new Cs2VideoGeneratorOptions());

        await startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, session.State);

        await EndSessionAsync(session, reader, runTask);
    }

    /// <summary>
    ///     A game build that is newer than / different from the plugin's reference build must
    ///     connect (warn-and-proceed), even when a reference build is recorded.
    /// </summary>
    [Fact]
    public async Task StartSession_NewerGameBuild_ConnectsWithWarning()
    {
        string overridePath = WriteOverrideFile(BrokenPairOverrideJson);
        Cs2VideoGeneratorOptions options = new() { CompatibilityOverridePath = overridePath };

        // 14999 differs from the reference build (14093) but is not on the known-broken list
        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus("1.0.1", "14999"), options);

        await startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, session.State);

        await EndSessionAsync(session, reader, runTask);
    }

    /// <summary>
    ///     A version pair on the known-broken list is the only case that blocks session start.
    /// </summary>
    [Fact]
    public async Task StartSession_KnownBrokenPair_ThrowsPluginIncompatibleException()
    {
        string overridePath = WriteOverrideFile(BrokenPairOverrideJson);
        Cs2VideoGeneratorOptions options = new() { CompatibilityOverridePath = overridePath };

        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus("1.0.1", "14200"), options);

        PluginIncompatibleException ex =
            await Assert.ThrowsAsync<PluginIncompatibleException>(() => startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Contains("14200", ex.Message);
        Assert.Contains("--force", ex.Message);
        Assert.Equal(CsvgSessionState.Faulted, session.State);

        reader.Complete();
        Task? runTaskValue = runTask();
        if (runTaskValue is not null)
        {
            await runTaskValue.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }

        // Faulted -> StopAsync is the documented reset path and must succeed.
        await session.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     ForceIncompatiblePlugin (--force) overrides even a known-broken pair.
    /// </summary>
    [Fact]
    public async Task StartSession_KnownBrokenPairWithForce_Connects()
    {
        string overridePath = WriteOverrideFile(BrokenPairOverrideJson);
        Cs2VideoGeneratorOptions options = new()
        {
            CompatibilityOverridePath = overridePath,
            ForceIncompatiblePlugin = true
        };

        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus("1.0.1", "14200"), options);

        await startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, session.State);

        await EndSessionAsync(session, reader, runTask);
    }

    /// <summary>
    ///     Mock mode without a simulated game version must not warn or block - there is no game.
    /// </summary>
    [Fact]
    public async Task StartSession_MockModeWithoutGameVersion_Connects()
    {
        Cs2VideoGeneratorOptions options = new() { MockMode = true };

        (CsvgVideoSession session, Task startTask, FakeStreamReader reader, Func<Task?> runTask) =
            StartSessionWithStatus(ReadyStatus("1.0.1"), options);

        await startTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(CsvgSessionState.Connected, session.State);

        await EndSessionAsync(session, reader, runTask);
    }

}
