#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Obs.NET;
using Obs.NET.Requests;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     One OBS recording, from StartRecord to the finished file. OBS names its output on its own
///     wall-clock terms, so finalizing means waiting for the file to settle and then moving it to the
///     name the caller asked for.
/// </summary>
/// <remarks>
///     <see cref="TickPlan" /> is always null: OBS records the CS2 window and never issues engine
///     commands, so there is nothing for the coordinator to arm.
/// </remarks>
public sealed partial class ObsClipCapture : IClipCapture
{
    private readonly ClipCaptureContext _context;
    private readonly ILogger _logger;
    private readonly ObsCaptureSession _session;

    private DateTime? _captureEndTime;
    private DateTime? _captureStartTime;
    private int _finished;
    private string? _lastOutputPath;
    private CaptureProgressPump? _progressPump;
    private bool _recordingStarted;
    private bool _recordingStopped;

    /// <summary>
    ///     Creates a clip bound to an OBS capture session. Clips are handed out by
    ///     <see cref="ObsCaptureSession.BeginClipAsync" />; this constructor exists for that call and
    ///     for tests.
    /// </summary>
    internal ObsClipCapture(ObsCaptureSession session, ClipCaptureContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = session.Logger;
    }

    /// <inheritdoc />
    public TickCommandPlan? TickPlan => null;

    /// <inheritdoc />
    /// <remarks>
    ///     Checks that OBS is not already recording, then starts the recording and the once-per-second
    ///     progress readings. Recording begins before playback is requested, so the clip contains the
    ///     whole tick range plus whatever frames render while the play command is in flight.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     OBS is already recording. Reported as a failed <see cref="CaptureResult" /> by the
    ///     coordinator, which never lets a capture problem take the CS2 session down.
    /// </exception>
    public async Task OnPlaybackStartingAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.StartRecord");
        activity?.SetTag("obs.outputPath", _context.Metadata.GetFullOutputPath());

        ObsWsClient client = _session.RequireClient();

        // Session bring-up established that OBS was idle, once. A recording that started since (the
        // user pressed record, or a previous clip's stop never landed) would make StartRecord fail
        // with an opaque error and, worse, fold this clip into somebody else's file. One cheap status
        // request per clip; the stabilization delay stays in bring-up where it belongs.
        GetRecordStatusResponse? recordStatus = await client.GetRecordStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        ThrowIfAlreadyRecording(recordStatus);

        _captureStartTime = DateTime.UtcNow;
        _recordingStarted = true;

        CsvgMetrics.CaptureSessionCount.Add(1,
            new KeyValuePair<string, object?>("provider", _session.ProviderName),
            new KeyValuePair<string, object?>("result", "start"));

        await client.StartRecordAsync(cancellationToken).ConfigureAwait(false);
        LogStartedObsRecording();

        StartProgressMonitoring(client);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Stops the OBS recording and remembers the output path OBS reports, for
    ///     <see cref="FinalizeAsync" /> to rename.
    /// </remarks>
    public async Task OnPlaybackEndedAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.StopRecord");

        if (!_recordingStarted || _recordingStopped)
        {
            return;
        }

        ObsWsClient client = _session.RequireClient();

        StopProgressMonitoring();

        StopRecordResponse? stopResponse = await client.StopRecordAsync(cancellationToken).ConfigureAwait(false);
        _recordingStopped = true;
        _lastOutputPath = stopResponse?.OutputPath;
        _captureEndTime = DateTime.UtcNow;

        if (_captureStartTime.HasValue)
        {
            activity?.SetTag("obs.capture.durationMs",
                (_captureEndTime.Value - _captureStartTime.Value).TotalMilliseconds);
        }

        activity?.SetTag("obs.capture.outputPath", _lastOutputPath);
        LogStoppedObsRecording();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Waits for OBS to finish writing the output file (size stable, handle released), renames it
    ///     from OBS's timestamped name to the filename requested in the capture metadata (with retries;
    ///     the OBS-named file is kept if renaming keeps failing), and restores any OBS audio-input mutes
    ///     applied for an audio-disabled clip. Never throws for capture-level problems: failures and
    ///     oddities (missing file, rename failure) are reported through
    ///     <see cref="CaptureResult.Success" />, <see cref="CaptureResult.Warnings" /> and the session's
    ///     <see cref="ICaptureSession.ErrorOccurred" /> event. <see cref="CaptureResult.Duration" /> is
    ///     the wall-clock span between StartRecord and StopRecord.
    /// </remarks>
    public async Task<CaptureResult> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.Finalize");

        string? obsOutputPath = _lastOutputPath;
        string desiredOutputPath = _context.Metadata.GetFullOutputPath();
        string finalOutputPath = desiredOutputPath;

        activity?.SetTag("obs.outputPath.obs", obsOutputPath);
        activity?.SetTag("obs.outputPath.desired", desiredOutputPath);

        bool success = false;
        long? fileSize = null;
        List<string> warnings = new();

        try
        {
            // Wait for OBS to finish writing the file and release the handle. This matters most for
            // the last clip, where OBS may still be flushing buffers.
            if (!string.IsNullOrEmpty(obsOutputPath))
            {
                await _session.Finalizer.WaitForFileReadyAsync(obsOutputPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(obsOutputPath) && File.Exists(obsOutputPath))
            {
                // Path comparison must match the platform: Linux file systems are case-sensitive,
                // while Windows and macOS are case-insensitive by default.
                StringComparison pathComparison = OperatingSystem.IsLinux()
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (!string.Equals(obsOutputPath, desiredOutputPath, pathComparison))
                {
                    bool renamed = await _session.Finalizer
                        .TryRenameWithRetryAsync(obsOutputPath, desiredOutputPath, cancellationToken)
                        .ConfigureAwait(false);

                    if (renamed)
                    {
                        finalOutputPath = desiredOutputPath;
                        LogRenamedOutputFile(obsOutputPath, desiredOutputPath);
                        activity?.SetTag("obs.output.renamed", true);
                    }
                    else
                    {
                        // Keep the OBS-named file rather than losing the recording.
                        finalOutputPath = obsOutputPath;
                        warnings.Add("Could not rename output file after multiple attempts");
                        activity?.SetTag("obs.output.renameError", "Failed after retries");
                    }
                }
                else
                {
                    finalOutputPath = obsOutputPath;
                }

                FileInfo fileInfo = new(finalOutputPath);
                fileSize = fileInfo.Length;
                success = fileSize > 0;

                CsvgMetrics.CaptureOutputSize.Record((double)fileSize,
                    new KeyValuePair<string, object?>("provider", _session.ProviderName));
                LogCaptureOutput(finalOutputPath, fileSize);
                activity?.SetTag("obs.output.size", fileSize);
            }
            else if (!string.IsNullOrEmpty(obsOutputPath))
            {
                warnings.Add($"Expected output file not found: {obsOutputPath}");
                activity?.SetTag("obs.output.missing", true);
                LogExpectedOutputFileNotFound(obsOutputPath);
            }
            else
            {
                warnings.Add("OBS did not return an output path");
                activity?.SetTag("obs.output.noPath", true);
            }

            DateTime? startTime = _captureStartTime;
            DateTime? endTime = _captureEndTime;
            TimeSpan duration = endTime.HasValue && startTime.HasValue
                ? endTime.Value - startTime.Value
                : TimeSpan.Zero;

            activity?.SetTag("obs.capture.durationMs", duration.TotalMilliseconds);
            activity?.SetTag("obs.output.final", finalOutputPath);

            CsvgMetrics.CaptureSessionCount.Add(1,
                new KeyValuePair<string, object?>("provider", _session.ProviderName),
                new KeyValuePair<string, object?>("result", success ? "completed" : "empty_output"));

            return new CaptureResult
            {
                Success = success,
                OutputFilePath = finalOutputPath,
                Duration = duration,
                FileSizeBytes = fileSize,
                StartTime = startTime,
                EndTime = endTime,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            activity?.SetTag("obs.finalize.error", ex.Message);

            LogErrorFinalizingCapture(ex);
            _session.RaiseError(new CaptureErrorEventArgs($"Error finalizing capture: {ex.Message}", ex));
            CsvgMetrics.CaptureSessionCount.Add(1,
                new KeyValuePair<string, object?>("provider", _session.ProviderName),
                new KeyValuePair<string, object?>("result", "error"));

            return new CaptureResult
            {
                Success = false,
                OutputFilePath = finalOutputPath,
                Duration = TimeSpan.Zero,
                ErrorMessage = ex.Message,
                Warnings = warnings
            };
        }
        finally
        {
            MarkFinished();

            // Always restore OBS audio input mute states at the end of a clip, success or failure,
            // so disabled audio capture never leaks muted inputs into the user's OBS.
            await _session.RestoreAudioInputMutesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Stops the progress readings, stops the OBS recording when one is running (OBS still writes
    ///     the partial file, which is better than leaving it recording), and restores audio-input mutes.
    ///     Idempotent and non-throwing.
    /// </remarks>
    public async Task AbortAsync(CaptureAbortReason reason, CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.Abort");
        activity?.SetTag("obs.abort.reason", reason.ToString());

        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        StopProgressMonitoring();

        if (_recordingStarted && !_recordingStopped)
        {
            try
            {
                StopRecordResponse? stopResponse = await _session.RequireClient()
                    .StopRecordAsync(cancellationToken).ConfigureAwait(false);
                _recordingStopped = true;
                _lastOutputPath = stopResponse?.OutputPath;
                _captureEndTime = DateTime.UtcNow;
                LogStoppedObsRecording();
            }
            catch (Exception ex)
            {
                LogAbortCleanupFailed(ex);
            }
        }

        try
        {
            await _session.RestoreAudioInputMutesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAbortCleanupFailed(ex);
        }

        _session.ClipFinished(this);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Aborts the clip when it never finalized, so a clip dropped without a result cannot leave OBS
    ///     recording.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _finished) == 0)
        {
            await AbortAsync(CaptureAbortReason.Cancelled, CancellationToken.None).ConfigureAwait(false);
        }

        StopProgressMonitoring();
        _session.ClipFinished(this);
    }

    /// <summary>
    ///     Pauses the OBS recording. The output file stays open and recording continues into the same
    ///     file on <see cref="ResumeAsync" />. OBS-only, which is why it is not on
    ///     <see cref="IClipCapture" /> - see <see cref="CaptureTraits.SupportsPauseResume" />.
    /// </summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.PauseRecord");
        await _session.RequireClient().PauseRecordAsync(cancellationToken).ConfigureAwait(false);
        LogPausedObsRecording();
    }

    /// <summary>
    ///     Resumes a paused OBS recording into the same output file.
    /// </summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsClipCapture.ResumeRecord");
        await _session.RequireClient().ResumeRecordAsync(cancellationToken).ConfigureAwait(false);
        LogResumedObsRecording();
    }

    /// <summary>
    ///     Rejects a clip when OBS reports an active recording output. A status OBS could not answer
    ///     (null) is not treated as "recording": the guard exists to catch the definite case, and
    ///     failing a clip over an unanswered status request would be worse than letting StartRecord
    ///     speak for itself.
    /// </summary>
    internal static void ThrowIfAlreadyRecording(GetRecordStatusResponse? recordStatus)
    {
        if (recordStatus?.OutputActive != true)
        {
            return;
        }

        throw new InvalidOperationException(
            "OBS is already recording, so this clip cannot be started - it would be folded into the " +
            "recording already in progress. Stop the recording in OBS (or let the previous clip " +
            "finish) before capturing again.");
    }

    private void MarkFinished()
    {
        Interlocked.Exchange(ref _finished, 1);
        StopProgressMonitoring();
        _session.ClipFinished(this);
    }

    private void StartProgressMonitoring(ObsWsClient client)
    {
        _progressPump = new CaptureProgressPump(
            async ct =>
            {
                if (_recordingStopped)
                {
                    return null;
                }

                GetRecordStatusResponse? recordStatus = await client.GetRecordStatusAsync(ct).ConfigureAwait(false);
                if (recordStatus is null)
                {
                    return null;
                }

                TimeSpan elapsed = _captureStartTime.HasValue
                    ? DateTime.UtcNow - _captureStartTime.Value
                    : TimeSpan.Zero;

                return new CaptureProgressEventArgs(
                    elapsed,
                    (long)recordStatus.OutputBytes,
                    currentBitrate: elapsed.TotalSeconds > 0
                        ? recordStatus.OutputBytes * 8 / 1000.0 / elapsed.TotalSeconds
                        : null);
            },
            progress => _session.RaiseProgress(progress),
            ex => _session.RaiseError(new CaptureErrorEventArgs(
                $"Error getting recording status: {ex.Message}", ex, CaptureErrorSeverity.Warning)),
            _logger);

        _progressPump.Start();
    }

    private void StopProgressMonitoring()
    {
        CaptureProgressPump? pump = Interlocked.Exchange(ref _progressPump, null);
        pump?.Dispose();
    }

    #region Logging

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Started OBS recording")]
    partial void LogStartedObsRecording();

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Expected output file not found: {Path}")]
    partial void LogExpectedOutputFileNotFound(string path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Error finalizing capture")]
    partial void LogErrorFinalizingCapture(Exception exception);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Paused OBS recording")]
    partial void LogPausedObsRecording();

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "Resumed OBS recording")]
    partial void LogResumedObsRecording();

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Stopped OBS recording")]
    partial void LogStoppedObsRecording();

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Capture output: {Path} ({Size} bytes)")]
    partial void LogCaptureOutput(string path, long? size);

    [LoggerMessage(EventId = 27, Level = LogLevel.Information,
        Message = "Renamed output file from {SourcePath} to {DestPath}")]
    partial void LogRenamedOutputFile(string sourcePath, string destPath);

    [LoggerMessage(EventId = 58, Level = LogLevel.Warning, Message = "Error cleaning up an aborted OBS clip")]
    partial void LogAbortCleanupFailed(Exception exception);

    #endregion
}
