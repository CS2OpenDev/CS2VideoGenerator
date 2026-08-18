#region

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Publishes capture progress on a fixed cadence while a clip is recording. Every backend that
///     can report bytes, frames or elapsed time needs the same loop - a timer, a snapshot call that
///     may fail transiently, and a publish that must never take the timer thread down - so it lives
///     here once and each backend supplies only the snapshot.
/// </summary>
public sealed partial class CaptureProgressPump : IDisposable
{
    /// <summary>
    ///     Default cadence: one progress event per second, matching what hosts render.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly Action<Exception>? _onError;
    private readonly Action<CaptureProgressEventArgs> _onProgress;
    private readonly Func<CancellationToken, Task<CaptureProgressEventArgs?>> _snapshotAsync;
    private Timer? _timer;

    /// <summary>
    ///     Creates a pump. Nothing is sampled until <see cref="Start" /> is called.
    /// </summary>
    /// <param name="snapshotAsync">
    ///     Takes one progress reading, or returns null when the backend has nothing to report yet.
    /// </param>
    /// <param name="onProgress">Publishes a reading. Called on a thread-pool thread.</param>
    /// <param name="onError">
    ///     Optional: invoked when a snapshot throws, so the backend can surface it as a capture
    ///     warning. Exceptions are logged either way and never propagate.
    /// </param>
    /// <param name="logger">Logger; null falls back to a no-op logger.</param>
    /// <param name="interval">Cadence; defaults to <see cref="DefaultInterval" />.</param>
    public CaptureProgressPump(
        Func<CancellationToken, Task<CaptureProgressEventArgs?>> snapshotAsync,
        Action<CaptureProgressEventArgs> onProgress,
        Action<Exception>? onError = null,
        ILogger? logger = null,
        TimeSpan? interval = null)
    {
        _snapshotAsync = snapshotAsync ?? throw new ArgumentNullException(nameof(snapshotAsync));
        _onProgress = onProgress ?? throw new ArgumentNullException(nameof(onProgress));
        _onError = onError;
        _logger = logger ?? NullLogger.Instance;
        _interval = interval ?? DefaultInterval;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    ///     Starts sampling. Calling it again while already running is a no-op.
    /// </summary>
    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        _timer = new Timer(_ => Sample(), null, _interval, _interval);
    }

    /// <summary>
    ///     Stops sampling. Idempotent. A snapshot already in flight may still publish once.
    /// </summary>
    public void Stop()
    {
        Timer? timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();
    }

    private void Sample()
    {
        try
        {
            // Fire-and-forget: the timer thread must not be blocked by a backend round trip, and a
            // slow snapshot must not stall the next tick.
            _ = Task.Run(async () =>
            {
                try
                {
                    CaptureProgressEventArgs? progress =
                        await _snapshotAsync(CancellationToken.None).ConfigureAwait(false);
                    if (progress != null)
                    {
                        _onProgress(progress);
                    }
                }
                catch (Exception ex)
                {
                    LogProgressSnapshotFailed(ex);
                    _onError?.Invoke(ex);
                }
            });
        }
        catch (Exception ex)
        {
            LogProgressTimerFailed(ex);
        }
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Error taking a capture progress snapshot")]
    partial void LogProgressSnapshotFailed(Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Error in the capture progress timer")]
    partial void LogProgressTimerFailed(Exception exception);

    #endregion
}
