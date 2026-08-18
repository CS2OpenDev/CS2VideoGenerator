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
///     Represents a single gRPC game session created by <see cref="Cs2GameService" /> for an incoming connection.
///     Owns the read loop for incoming <see cref="CsProcessEvent" /> messages and provides a thread-safe
///     <see cref="SendAsync(CsControlRequest)" /> method to send control commands back to the client.
/// </summary>
internal sealed partial class Cs2GameSession
{
    private readonly ICs2EngineHost _engineHost;
    private readonly CancellationToken _externalCancellationToken;
    private readonly CancellationTokenSource _internalCts = new();
    private readonly CancellationTokenSource _linkedCts;
    private readonly ILogger _logger;
    private readonly IAsyncStreamReader<CsProcessEvent> _requestStream;
    private readonly IServerStreamWriter<CsControlRequest> _responseStream;
    private readonly string _sessionId;

    // Serializes writes to the gRPC response stream (ServerStreamWriter is not thread-safe)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Set to true (before the CTSes are disposed) when the read loop terminates, so that
    // RequestStop/SendAsync racing a natural session end never touch a disposed CTS.
    private volatile bool _ctsDisposed;
    private volatile bool _isConnected = true;
    private DateTimeOffset _lastSeenUtc = DateTimeOffset.UtcNow;

    private long? _latestTick;

    /// <param name="sessionId">A unique identifier for the session (for logging/tracing).</param>
    /// <param name="requestStream">The incoming stream of <see cref="CsProcessEvent" /> messages from the client.</param>
    /// <param name="responseStream">The outgoing stream used to send <see cref="CsControlRequest" /> messages to the client.</param>
    /// <param name="cancellationToken">
    ///     Cancellation token tied to the gRPC call lifetime; when canceled the session should
    ///     stop.
    /// </param>
    /// <param name="logger">Logger used for session-scoped logs.</param>
    /// <param name="engineHost">The engine host whose current session events are reported to.</param>
    public Cs2GameSession(string sessionId, IAsyncStreamReader<CsProcessEvent> requestStream,
        IServerStreamWriter<CsControlRequest> responseStream, CancellationToken cancellationToken, ILogger logger,
        ICs2EngineHost engineHost)
    {
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _requestStream = requestStream ?? throw new ArgumentNullException(nameof(requestStream));
        _responseStream = responseStream ?? throw new ArgumentNullException(nameof(responseStream));
        _externalCancellationToken = cancellationToken;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _internalCts.Token);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
    }

    /// <summary>
    ///     The engine session this connection's events are pushed into, resolved fresh on every
    ///     dispatch so a plugin reconnect lands on whichever session the host most recently created
    ///     (last writer wins). Null when no session exists yet, or when the host hands out a session
    ///     type that does not implement the transport seam - either way the event is dropped rather
    ///     than crashing the read loop.
    /// </summary>
    private ICs2EngineWire? Wire => _engineHost.Current as ICs2EngineWire;

    /// <summary>
    ///     Requests the session to stop gracefully. Cancelling the internal token makes the read
    ///     loop exit via OperationCanceledException rather than waiting for the IOException that a
    ///     killed client process eventually produces.
    /// </summary>
    public void RequestStop()
    {
        LogSessionStopRequested(_sessionId);

        if (_ctsDisposed)
        {
            return; // Session already ended naturally; nothing to stop
        }

        try
        {
            _internalCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A natural session end raced this stop request and already disposed the CTS.
            // The session is over either way, so there is nothing left to cancel.
        }
    }

    /// <summary>
    ///     Runs the read loop until the client disconnects or the cancellation token is triggered,
    ///     dispatching each incoming event through <see cref="ProcessEventAsync" />.
    /// </summary>
    public async Task RunAsync()
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2GameSession.Run");
        activity?.SetTag("cs2.session.id", _sessionId);

        Stopwatch handlerStopwatch = Stopwatch.StartNew();
        ICs2EngineWire? startWire = Wire;
        if (startWire == null)
        {
            LogNoEngineSession(_sessionId);
        }
        else
        {
            await startWire.OnSessionStartedAsync(_sessionId);
            startWire.OnCommandSenderAvailable(_sessionId, SendAsync, RequestStop);
        }

        // The read loop runs inline; anything that needs to write concurrently goes through SendAsync.
        try
        {
            await foreach (CsProcessEvent processEvent in _requestStream.ReadAllAsync(_linkedCts.Token))
            {
                _lastSeenUtc = DateTimeOffset.UtcNow;

                try
                {
                    await ProcessEventAsync(processEvent);
                }
                catch (OperationCanceledException)
                {
                    // propagate cancellation
                    break;
                }
                catch (Exception ex)
                {
                    LogErrorProcessingEvent(ex, _sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the client disconnects or server is shutting down (graceful)
            LogSessionDisconnected(_sessionId, "cancelled");
        }
        catch (IOException)
        {
            // Expected when client process is killed - gRPC stream aborts
            LogSessionDisconnected(_sessionId, "stream_aborted");
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex, _sessionId);
        }
        finally
        {
            handlerStopwatch.Stop();
            _isConnected = false;

            // Dispose cancellation token sources. The flag is published first so that
            // RequestStop/SendAsync racing this teardown fail fast instead of hitting
            // an ObjectDisposedException (any remaining window is guarded by catches there).
            _ctsDisposed = true;
            _linkedCts.Dispose();
            _internalCts.Dispose();

            LogSessionEnded(_sessionId, _latestTick, _lastSeenUtc);
            if (Wire is { } endWire)
            {
                await endWire.OnSessionEndedAsync(_sessionId, _latestTick, _lastSeenUtc);
            }

            activity?.AddEvent(new ActivityEvent("session.ended"));
            activity?.SetTag("cs2.session.lastTick", _latestTick);
            activity?.SetTag("cs2.session.lastSeenUtc", _lastSeenUtc);
            activity?.SetTag("cs2.session.durationMs", handlerStopwatch.Elapsed.TotalMilliseconds);

            CsvgMetrics.GrpcServerHandlerDuration.Record(handlerStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("method", "CreateGameSession"));
        }
    }

    /// <summary>
    ///     Handles a single <see cref="CsProcessEvent" /> from the client. Expected to be fast and
    ///     idempotent; it may call <see cref="SendAsync" /> to issue commands.
    /// </summary>
    /// <param name="processEvent">The event message to handle.</param>
    private async Task ProcessEventAsync(CsProcessEvent processEvent)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2GameSession.ProcessEvent");
        activity?.SetTag("cs2.session.id", _sessionId);
        activity?.SetTag("cs2.event.type", processEvent.EventCase.ToString());

        try
        {
            // Resolved once per event: the whole event is applied to one session, and a session
            // created mid-dispatch takes over from the next event on.
            ICs2EngineWire? wire = Wire;
            if (wire == null)
            {
                LogNoEngineSession(_sessionId);
                return;
            }

            switch (processEvent.EventCase)
            {
                case CsProcessEvent.EventOneofCase.TickUpdate:
                    _latestTick = processEvent.TickUpdate.CurrentTick;
                    activity?.SetTag("cs2.event.tick", _latestTick);
                    wire.OnTickUpdated(_sessionId, _latestTick.Value,
                        processEvent.TickUpdate.HasIsPaused ? processEvent.TickUpdate.IsPaused : null);
                    break;

                case CsProcessEvent.EventOneofCase.DemoPlaybackStatusChange:
                    await wire.OnDemoPlaybackStatusChangedAsync(_sessionId, processEvent.DemoPlaybackStatusChange);
                    break;

                case CsProcessEvent.EventOneofCase.ProcessStatus:
                    await wire.OnProcessStatusChangedAsync(_sessionId, processEvent.ProcessStatus);
                    break;

                case CsProcessEvent.EventOneofCase.DemoState:
                    // Awaited async dispatch, same pattern as DemoPlaybackStatusChange: subscriber
                    // handlers run (isolated per-subscriber) inline on this read loop.
                    await wire.OnDemoStateAsync(_sessionId, processEvent.DemoState);
                    break;

                case CsProcessEvent.EventOneofCase.CommandAck:
                    // Synchronous routing to the pending-ack table only - acks have no public event
                    // surface, so nothing here can block the read loop.
                    wire.OnCommandAck(_sessionId, processEvent.CommandAck);
                    break;

                case CsProcessEvent.EventOneofCase.GameEventTelemetry:
                    await wire.OnGameEventTelemetryAsync(_sessionId, processEvent.GameEventTelemetry);
                    break;

                case CsProcessEvent.EventOneofCase.None:
                default:
                    LogUnhandledEvent(_sessionId, processEvent.EventCase);
                    break;
            }

            CsvgMetrics.GrpcServerStatusCount.Add(1, new KeyValuePair<string, object?>("method", "CreateGameSession"),
                new KeyValuePair<string, object?>("status_code", StatusCode.OK.ToString()));
        }
        catch (RpcException rpcEx)
        {
            CsvgMetrics.GrpcServerStatusCount.Add(1, new KeyValuePair<string, object?>("method", "CreateGameSession"),
                new KeyValuePair<string, object?>("status_code", rpcEx.Status.StatusCode.ToString()));
            activity?.SetTag("grpc.status_code", rpcEx.Status.StatusCode.ToString());
            throw;
        }
        catch (Exception ex)
        {
            CsvgMetrics.GrpcServerStatusCount.Add(1, new KeyValuePair<string, object?>("method", "CreateGameSession"),
                new KeyValuePair<string, object?>("status_code", StatusCode.Unknown.ToString()));
            activity?.SetTag("grpc.status_code", StatusCode.Unknown.ToString());
            LogErrorProcessingEvent(ex, _sessionId);
            throw;
        }
    }

    /// <summary>
    ///     Safely send a <see cref="CsControlRequest" /> to the connected client. Writes are serialized
    ///     to the response stream. If the session has ended, a stop was requested, or the write fails,
    ///     an <see cref="InvalidOperationException" /> is thrown so callers fail fast instead of silently
    ///     losing the command and burning their full wait timeout.
    /// </summary>
    /// <param name="command">The control request/command to send.</param>
    /// <exception cref="InvalidOperationException">The session is disconnected or the write failed.</exception>
    public async Task SendAsync(CsControlRequest command)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2GameSession.SendCommand", ActivityKind.Producer);
        activity?.SetTag("cs2.session.id", _sessionId);
        activity?.SetTag("cs2.command.type", command.CommandCase.ToString());

        // Fail fast if connection is closed or a stop was requested - the command would never be delivered.
        // Reading IsCancellationRequested on a disposed CTS is safe; it does not throw.
        if (!_isConnected || _ctsDisposed || _linkedCts.IsCancellationRequested)
        {
            throw SessionDisconnectedError(command);
        }

        // Capture the session token; the read loop may dispose the CTS concurrently on natural session end.
        CancellationToken sessionToken;
        try
        {
            sessionToken = _linkedCts.Token;
        }
        catch (ObjectDisposedException)
        {
            throw SessionDisconnectedError(command);
        }

        try
        {
            await _writeLock.WaitAsync(sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session stop was requested while waiting for the write lock
            throw SessionDisconnectedError(command);
        }

        try
        {
            await _responseStream.WriteAsync(command).ConfigureAwait(false);
            LogCommandSent(_sessionId, command.CommandCase);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            LogSendError(ex, _sessionId);
            throw new InvalidOperationException(
                $"Failed to send command {command.CommandCase} to session '{_sessionId}': {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private InvalidOperationException SessionDisconnectedError(CsControlRequest command) =>
        new($"Cannot send command {command.CommandCase}: session '{_sessionId}' is disconnected.");

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Session {SessionId}: Unhandled event {Event}")]
    partial void LogUnhandledEvent(string sessionId, CsProcessEvent.EventOneofCase @event);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Session {SessionId}: Sent command {CommandType}")]
    partial void LogCommandSent(string sessionId, CsControlRequest.CommandOneofCase commandType);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Session {SessionId}: Error processing event")]
    partial void LogErrorProcessingEvent(Exception exception, string sessionId);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "Unhandled exception while reading events for session {SessionId}")]
    partial void LogUnhandledException(Exception exception, string sessionId);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Session {SessionId} ended. LastTick={LastTick} LastSeenUtc={LastSeenUtc}")]
    partial void LogSessionEnded(string sessionId, long? lastTick, DateTimeOffset lastSeenUtc);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Session {SessionId}: Error sending command")]
    partial void LogSendError(Exception exception, string sessionId);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Session {SessionId} disconnected: {Reason}")]
    partial void LogSessionDisconnected(string sessionId, string reason);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "Session {SessionId}: Graceful stop requested")]
    partial void LogSessionStopRequested(string sessionId);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message =
            "Session {SessionId}: no engine session is available to receive plugin events. Build a " +
            "CsvgVideoSession (or create a session on the engine host) before the plugin connects.")]
    partial void LogNoEngineSession(string sessionId);
}
