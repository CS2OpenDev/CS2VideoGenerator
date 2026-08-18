#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Proto;
using Grpc.Core;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core.Grpc;

/// <summary>
///     gRPC service implementation the CS2 server plugin connects to. Each incoming
///     <see cref="CreateGameSession" /> call gets its own <see cref="Cs2GameSession" /> that
///     bridges the plugin's event stream to the engine host's current session; multiple concurrent
///     connections are allowed and no global per-connection state is kept.
/// </summary>
/// <param name="loggerFactory">Factory used for the service and per-session loggers.</param>
/// <param name="engineHost">
///     The engine host whose current session consumes plugin events and issues control commands. It
///     must be the same host the composition entry point creates its sessions from, or the plugin's
///     events reach a session nobody is using.
/// </param>
public partial class Cs2GameService(ILoggerFactory loggerFactory, ICs2EngineHost engineHost)
    : Proto.Cs2GameService.Cs2GameServiceBase
{
    private readonly ICs2EngineHost _engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));

    private readonly ILogger<Cs2GameService> _logger = loggerFactory?.CreateLogger<Cs2GameService>() ??
                                                       throw new ArgumentNullException(nameof(loggerFactory));

    private readonly ILoggerFactory _loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    /// <summary>
    ///     Primary gRPC bidi streaming entry point. Creates a Cs2GameSession that owns reading and writing logic.
    ///     Each incoming call manages its own session: there is no global per-connection state, and
    ///     additional connections are not rejected.
    /// </summary>
    public override async Task CreateGameSession(IAsyncStreamReader<CsProcessEvent> requestStream,
        IServerStreamWriter<CsControlRequest> responseStream, ServerCallContext context)
    {
        string sessionId = Guid.NewGuid().ToString("N");

        // The trace context is automatically extracted from the gRPC traceparent header by OpenTelemetry
        // Activity.Current will already have the correct parent from the incoming request
        using Activity? activity = CsvgTracing.StartActivity("Cs2GameService.CreateGameSession", ActivityKind.Server);
        activity?.SetTag("cs2.session.id", sessionId);
        activity?.SetTag("cs2.peer", context.Peer);
        activity?.SetTag("cs2.session.hasIncomingTraceContext", Activity.Current?.ParentId != null);

        LogClientConnected(sessionId);

        ILogger<Cs2GameSession> sessionLogger = _loggerFactory.CreateLogger<Cs2GameSession>();
        Cs2GameSession session = new(sessionId, requestStream, responseStream, context.CancellationToken, sessionLogger,
            _engineHost);

        try
        {
            await session.RunAsync();
        }
        finally
        {
            LogClientDisconnected(sessionId);
            activity?.AddEvent(new ActivityEvent("session.closed"));
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Client connected (session {SessionId}). Ready to receive events and send commands.")]
    partial void LogClientConnected(string sessionId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Client disconnected (session {SessionId}).")]
    partial void LogClientDisconnected(string sessionId);
}
