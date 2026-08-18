#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Proto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

#endregion

namespace Cs2VideoGenerator.Core.Tests.TestSupport;

/// <summary>
///     Builds and drives the two objects under test in the session suites - a bare
///     <see cref="Cs2EngineSession" /> for engine behavior, and a <see cref="CsvgVideoSession" /> for
///     composition and lifecycle - against a mocked CS2 process manager, so neither needs a real gRPC
///     connection or a plugin.
/// </summary>
internal static class SessionHarness
{
    /// <summary>
    ///     Creates a standalone engine session over a mocked process manager. Use this where the
    ///     behavior under test is the plugin command channel or the latched engine truth.
    /// </summary>
    internal static Cs2EngineSession CreateEngineSession(Cs2VideoGeneratorOptions? options = null) =>
        CreateEngineSession(out _, options);

    /// <inheritdoc cref="CreateEngineSession(Cs2VideoGeneratorOptions?)" />
    internal static Cs2EngineSession CreateEngineSession(out Mock<ICs2ProcessManager> processManager,
        Cs2VideoGeneratorOptions? options = null)
    {
        processManager = new Mock<ICs2ProcessManager>();
        return new Cs2EngineSession(
            NullLogger.Instance,
            Options.Create(options ?? new Cs2VideoGeneratorOptions()),
            processManager.Object,
            endpointProbe: null,
            new EngineSessionOptions());
    }

    /// <summary>
    ///     Creates an engine host whose current session is the one returned, for tests that push events
    ///     through a real <c>Cs2GameSession</c> read loop instead of calling the ingestion seams.
    /// </summary>
    internal static Cs2EngineHost CreateEngineHost(out Cs2EngineSession session,
        Cs2VideoGeneratorOptions? options = null)
    {
        Cs2EngineHost host = new(NullLogger.Instance, Options.Create(options ?? new Cs2VideoGeneratorOptions()),
            new Mock<ICs2ProcessManager>().Object);
        session = (Cs2EngineSession)host.CreateSession(new EngineSessionOptions());
        return host;
    }

    /// <summary>
    ///     Creates a video session over a mocked process manager and a real engine host, so every
    ///     start produces a fresh engine session exactly as it does in production.
    /// </summary>
    internal static CsvgVideoSession CreateVideoSession(out Mock<ICs2ProcessManager> processManager,
        Cs2VideoGeneratorOptions? options = null,
        ICaptureSessionFactory? captureFactory = null,
        ClipCaptureCoordinator? coordinator = null,
        Cs2CompilationRunner? compilationRunner = null)
    {
        processManager = new Mock<ICs2ProcessManager>();
        IOptions<Cs2VideoGeneratorOptions> wrapped = Options.Create(options ?? new Cs2VideoGeneratorOptions());
        Cs2EngineHost host = new(NullLogger.Instance, wrapped, processManager.Object);

        return new CsvgVideoSession(
            NullLogger<CsvgVideoSession>.Instance,
            host,
            wrapped,
            captureFactory,
            coordinator,
            compilationRunner);
    }

    /// <summary>
    ///     The video session's current engine session as its concrete type - the wire ingestion seams
    ///     the plugin's event stream normally drives are internal to it.
    /// </summary>
    internal static Cs2EngineSession EngineSession(this CsvgVideoSession session) =>
        (Cs2EngineSession)session.Engine;

    /// <summary>
    ///     A plugin-Ready process status advertising the given capability tokens.
    /// </summary>
    internal static Cs2ProcessStatus ReadyStatus(params string[] capabilities)
    {
        Cs2ProcessStatus status = new() { Status = Cs2ProcessStatus.Types.ProcessStatus.Ready };
        status.Capabilities.AddRange(capabilities);
        return status;
    }

    /// <summary>
    ///     Wires a session id and a recording command sender straight into the engine session, then
    ///     latches the given capabilities with a Ready process status. Returns the list every command
    ///     the session sends is appended to.
    /// </summary>
    internal static async Task<List<CsControlRequest>> ConnectWithCapabilitiesAsync(
        this Cs2EngineSession session, string sessionId, params string[] capabilities)
    {
        List<CsControlRequest> sent = await ConnectSenderAsync(session, sessionId).ConfigureAwait(false);
        await session.OnProcessStatusChangedAsync(sessionId, ReadyStatus(capabilities)).ConfigureAwait(false);
        return sent;
    }

    /// <summary>
    ///     Wires a session id and a recording command sender only - no process status, so the
    ///     capability set stays empty (the v1.0 plugin shape).
    /// </summary>
    internal static async Task<List<CsControlRequest>> ConnectSenderAsync(
        this Cs2EngineSession session, string sessionId)
    {
        List<CsControlRequest> sent = [];
        await session.OnSessionStartedAsync(sessionId).ConfigureAwait(false);
        session.OnCommandSenderAvailable(sessionId, cmd =>
        {
            lock (sent)
            {
                sent.Add(cmd);
            }

            return Task.CompletedTask;
        });

        return sent;
    }

    /// <summary>
    ///     Configures the process manager mock so LaunchAsync simulates the plugin connecting and
    ///     reporting Ready (with the given capability tokens) while the launch call is still in flight
    ///     - i.e. inside the post-launch delay window - and starts the video session.
    ///     <paramref name="onLaunchBeforeEvents" /> runs at the very start of the simulated launch,
    ///     BEFORE any session event is delivered: state observed there is what a restart exposes
    ///     before the new session's handshake.
    /// </summary>
    internal static Task StartConnectedAsync(
        this CsvgVideoSession videoSession,
        Mock<ICs2ProcessManager> processManager,
        string sessionId,
        Func<CsControlRequest, Task>? sendAsync = null,
        Action? requestStop = null,
        string[]? capabilities = null,
        Action? onLaunchBeforeEvents = null,
        bool initializeCapture = true,
        CancellationToken cancellationToken = default)
    {
        Func<CsControlRequest, Task> sender = sendAsync ?? (_ => Task.CompletedTask);
        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (int? _, int? _, bool _, CancellationToken _) =>
            {
                onLaunchBeforeEvents?.Invoke();

                // Resolved inside the launch so the events land on the session THIS start created.
                Cs2EngineSession engine = videoSession.EngineSession();
                await engine.OnSessionStartedAsync(sessionId);
                engine.OnCommandSenderAvailable(sessionId, sender, requestStop);
                await engine.OnProcessStatusChangedAsync(sessionId, ReadyStatus(capabilities ?? []));
            });

        return videoSession.StartAsync(new EngineSessionOptions(), initializeCapture, cancellationToken);
    }
}
