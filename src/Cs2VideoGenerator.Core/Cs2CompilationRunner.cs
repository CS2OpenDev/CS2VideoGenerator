#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     Runs a whole <see cref="Cs2Compilation" />: validate, group the clips by demo so each demo is
///     loaded once, capture every clip through <see cref="ClipCaptureCoordinator" />, then concatenate
///     the results. Holds no run state — everything a run needs arrives in
///     <see cref="Cs2CompilationRunContext" /> — so one instance serves every session.
/// </summary>
/// <remarks>
///     <para>
///         Clips are processed grouped by demo and in ascending start-tick order within a group, but
///         numbered by their original position in <see cref="Cs2Compilation.Clips" />: the output files
///         match the input order no matter what order they were recorded in.
///     </para>
///     <para>
///         Clip failures are fail-fast: the first failed clip ends the run and the results collected so
///         far are returned. A failed concatenation is not. The individual clips survive and the run
///         reports partial success.
///     </para>
/// </remarks>
public sealed partial class Cs2CompilationRunner
{
    private readonly ClipCaptureCoordinator _coordinator;
    private readonly IFfmpegService? _ffmpegService;
    private readonly ILogger _logger;

    /// <summary>
    ///     Creates the runner. A null <paramref name="ffmpegService" /> means concatenation is
    ///     unavailable: clips are still captured, and a compilation that asked for concatenation gets a
    ///     partial-success result naming the missing service. A null logger falls back to a no-op logger.
    /// </summary>
    public Cs2CompilationRunner(
        ClipCaptureCoordinator coordinator,
        IFfmpegService? ffmpegService = null,
        ILogger<Cs2CompilationRunner>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ffmpegService = ffmpegService;
        _logger = logger ?? NullLogger<Cs2CompilationRunner>.Instance;
    }

    // exposed so a composition entry point can verify it shares one coordinator with the runner it
    // was handed, instead of forwarding capture state from a state machine no clip ever touches.
    internal ClipCaptureCoordinator Coordinator => _coordinator;

    /// <summary>
    ///     Captures a compilation of clips from CS2 demos.
    /// </summary>
    /// <param name="compilation">The compilation definition with clips and settings.</param>
    /// <param name="context">The engine session, capture session and per-clip callbacks for this run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing status and details for each captured clip.</returns>
    /// <exception cref="ArgumentException">The compilation is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    ///     No capture session was supplied, i.e. capture is not initialized for this engine session.
    /// </exception>
    public async Task<Cs2CompilationResult> RunAsync(
        Cs2Compilation compilation,
        Cs2CompilationRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(context);

        ICs2EngineSession session = context.Engine;

        using Activity? activity = CsvgTracing.StartActivity("Cs2CompilationRunner.Run", ActivityKind.Client);
        activity?.SetTag("csvg.session.id", session.SessionId);
        activity?.SetTag("csvg.compilation.clips", compilation.Clips.Count);

        IReadOnlyList<string> issues = compilation.Validate();
        if (issues.Count > 0)
        {
            string errorMessage = $"Invalid compilation: {string.Join(", ", issues)}";
            activity?.SetTag("csvg.compilation.validation.error", errorMessage);
            throw new ArgumentException(errorMessage);
        }

        if (context.Capture is not { } captureSession)
        {
            throw new InvalidOperationException(
                "Capture provider not initialized. Ensure StartSessionAsync completed successfully with a capture provider configured.");
        }

        LogCaptureCompilationStart(compilation.Clips.Count);

        // Grouped by demo so each demo is loaded once, but the original index rides along and names
        // the output file: clip_000.mp4, clip_001.mp4 match the input order whatever order they were
        // recorded in.
        List<IGrouping<string, (Cs2CompilationClip clip, int originalIndex)>> clipsByDemo = compilation.Clips
            .Select((clip, originalIndex) => (clip, originalIndex))
            .GroupBy(x => x.clip.DemoFilePath)
            .ToList();

        activity?.SetTag("csvg.compilation.demos", clipsByDemo.Count);

        // pre-allocated so results can be slotted in by original index
        Cs2ClipResult?[] results = new Cs2ClipResult?[compilation.Clips.Count];
        string? currentDemo = null;

        try
        {
            foreach (IGrouping<string, (Cs2CompilationClip clip, int originalIndex)> demoGroup in clipsByDemo)
            {
                string demoPath = demoGroup.Key;

                if (currentDemo != demoPath)
                {
                    if (currentDemo != null)
                    {
                        LogClosingPreviousDemo(currentDemo);
                        await session.CloseDemoAsync(cancellationToken).ConfigureAwait(false);
                    }

                    LogLoadingDemo(demoPath);
                    await LoadDemoAsync(session, captureSession, demoPath, cancellationToken).ConfigureAwait(false);
                    currentDemo = demoPath;
                }

                // ascending tick order, for efficiency
                foreach ((Cs2CompilationClip clip, int originalIndex) in demoGroup.OrderBy(x => x.clip.StartTick))
                {
                    if (context.ClipStarted != null)
                    {
                        await context.ClipStarted(originalIndex, clip).ConfigureAwait(false);
                    }

                    Cs2ClipResult clipResult = await CaptureClipAsync(
                        session, captureSession, clip, originalIndex, compilation.Settings, cancellationToken)
                        .ConfigureAwait(false);

                    results[originalIndex] = clipResult;
                    if (context.ClipCompleted != null)
                    {
                        await context.ClipCompleted(originalIndex, clipResult).ConfigureAwait(false);
                    }

                    if (!clipResult.Success)
                    {
                        LogClipCaptureFailed(originalIndex, clipResult.ErrorMessage);
                        activity?.SetTag("csvg.compilation.failed.clipIndex", originalIndex);
                        activity?.SetTag("csvg.compilation.failed.error", clipResult.ErrorMessage);

                        return Cs2CompilationResult.Failed(
                            results.Where(r => r != null).Cast<Cs2ClipResult>().ToList(),
                            clipResult.ErrorMessage!);
                    }
                }
            }

            if (currentDemo != null)
            {
                LogClosingPreviousDemo(currentDemo);
                await session.CloseDemoAsync(cancellationToken).ConfigureAwait(false);
            }

            List<Cs2ClipResult> clipResults = results.Cast<Cs2ClipResult>().ToList();

            if (compilation.Settings.ConcatenateClips)
            {
                FfmpegConcatResult concatResult = await ConcatenateClipsAsync(
                    clipResults, compilation.Settings, cancellationToken).ConfigureAwait(false);

                if (concatResult.Success)
                {
                    activity?.SetTag("csvg.compilation.concatenated", true);
                    activity?.SetTag("csvg.compilation.outputPath", concatResult.OutputPath);

                    if (compilation.Settings.DeleteClipsAfterConcat)
                    {
                        await DeleteClipFilesAsync(clipResults).ConfigureAwait(false);
                    }

                    LogCaptureCompilationComplete(compilation.Clips.Count);
                    activity?.SetTag("csvg.compilation.success", true);
                    return Cs2CompilationResult.Succeeded(clipResults, concatResult);
                }

                // the clips themselves came out fine, so this is a partial success, not a failure
                LogConcatenationFailed(concatResult.ErrorMessage);
                activity?.SetTag("csvg.compilation.concat.failed", true);
                activity?.SetTag("csvg.compilation.concat.error", concatResult.ErrorMessage);

                return new Cs2CompilationResult
                {
                    Success = true,
                    ClipResults = clipResults,
                    ConcatenationResult = concatResult,
                    ErrorMessage = $"Clips captured successfully but concatenation failed: {concatResult.ErrorMessage}"
                };
            }

            LogCaptureCompilationComplete(compilation.Clips.Count);
            activity?.SetTag("csvg.compilation.success", true);

            return Cs2CompilationResult.Succeeded(clipResults);
        }
        catch (Exception ex)
        {
            LogCaptureCompilationError(ex);
            activity?.SetTag("csvg.compilation.error", ex.Message);

            if (currentDemo != null)
            {
                try
                {
                    await session.CloseDemoAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return Cs2CompilationResult.Failed(
                results.Where(r => r != null).Cast<Cs2ClipResult>().ToList(),
                ex.Message);
        }
    }

    // Backends that have to register against the loaded demo (HLAE's mirv handshake) get told here,
    // once per successful load and only once the engine actually has the demo open. The default
    // implementation is a no-op, so a backend that does not care pays nothing.
    private static async Task LoadDemoAsync(
        ICs2EngineSession session,
        ICaptureSession captureSession,
        string demoPath,
        CancellationToken cancellationToken)
    {
        await session.LoadDemoAsync(demoPath, interactiveDemoUi: false, cancellationToken).ConfigureAwait(false);
        await captureSession.OnDemoLoadedAsync(demoPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Cs2ClipResult> CaptureClipAsync(
        ICs2EngineSession session,
        ICaptureSession captureSession,
        Cs2CompilationClip clip,
        int clipIndex,
        Cs2CompilationSettings settings,
        CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2CompilationRunner.CaptureClip",
            ActivityKind.Client);
        activity?.SetTag("csvg.clip.index", clipIndex);
        activity?.SetTag("csvg.clip.startTick", clip.StartTick);
        activity?.SetTag("csvg.clip.endTick", clip.EndTick);
        activity?.SetTag("csvg.clip.playerName", clip.PlayerNameToSpectate);

        LogCapturingClip(clipIndex, clip.StartTick, clip.EndTick, clip.PlayerNameToSpectate);

        try
        {
            CaptureMetadata metadata = settings.CreateClipMetadata(clip, clipIndex);

            LogPlayDemoTickRangeWithCapture(clip.StartTick, clip.EndTick, metadata.OutputFileName);

            // Everything the clip needs from the engine travels on the context: the coordinator applies
            // the demo options and the spectator target, then re-asserts that target from the engine
            // thread just before the recording tick, which is what survives the seek into the clip.
            ClipCaptureContext captureContext = new()
            {
                StartTick = clip.StartTick,
                EndTick = clip.EndTick,
                Metadata = metadata,
                ClipIndex = clipIndex,
                SpectatorTarget = clip.PlayerNameToSpectate,
                ClipOptions = clip.ClipOptions,
                PlaybackTimeout = null
            };

            CaptureResult captureResult = await _coordinator
                .CaptureClipAsync(session, captureSession, captureContext, cancellationToken)
                .ConfigureAwait(false);

            activity?.SetTag("csvg.capture.success", captureResult.Success);
            activity?.SetTag("csvg.capture.outputPath", captureResult.OutputFilePath);
            activity?.SetTag("csvg.capture.durationMs", captureResult.Duration.TotalMilliseconds);
            if (captureResult.ErrorMessage != null)
            {
                activity?.SetTag("csvg.capture.error", captureResult.ErrorMessage);
            }

            LogCaptureCompleted(captureResult.Success, captureResult.OutputFilePath);

            LogClipCaptureSuccess(clipIndex, captureResult.OutputFilePath);
            activity?.SetTag("csvg.clip.success", captureResult.Success);
            activity?.SetTag("csvg.clip.outputPath", captureResult.OutputFilePath);

            return new Cs2ClipResult
            {
                ClipIndex = clipIndex,
                Success = captureResult.Success,
                OutputFilePath = captureResult.OutputFilePath,
                Duration = captureResult.Duration,
                CaptureResult = captureResult,
                ErrorMessage = captureResult.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            LogClipCaptureException(clipIndex, ex);
            activity?.SetTag("csvg.clip.error", ex.Message);

            return new Cs2ClipResult
            {
                ClipIndex = clipIndex,
                Success = false,
                OutputFilePath = string.Empty,
                Duration = TimeSpan.Zero,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<FfmpegConcatResult> ConcatenateClipsAsync(
        IReadOnlyList<Cs2ClipResult> clipResults,
        Cs2CompilationSettings settings,
        CancellationToken cancellationToken)
    {
        using Activity? activity = CsvgTracing.StartActivity("Cs2CompilationRunner.ConcatenateClips",
            ActivityKind.Internal);

        if (_ffmpegService == null)
        {
            LogFfmpegServiceNotAvailable();
            return FfmpegConcatResult.Failed(
                settings.GetConcatenatedOutputPath(),
                "FFmpeg service is not configured");
        }

        List<string> clipPaths = clipResults
            .Where(r => r.Success && !string.IsNullOrEmpty(r.OutputFilePath))
            .OrderBy(r => r.ClipIndex)
            .Select(r => r.OutputFilePath)
            .ToList();

        if (clipPaths.Count == 0)
        {
            return FfmpegConcatResult.Failed(
                settings.GetConcatenatedOutputPath(),
                "No successful clips to concatenate");
        }

        if (clipPaths.Count == 1 && settings.Encoding?.RequiresReEncode != true)
        {
            // one clip, no re-encode asked for: a copy is the whole job
            string outputPath = settings.GetConcatenatedOutputPath();
            try
            {
                File.Copy(clipPaths[0], outputPath, overwrite: true);
                return FfmpegConcatResult.Succeeded(outputPath, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                return FfmpegConcatResult.Failed(outputPath, $"Failed to copy single clip: {ex.Message}");
            }
        }

        LogConcatenatingClips(clipPaths.Count, settings.GetConcatenatedOutputPath());
        activity?.SetTag("csvg.concat.clipCount", clipPaths.Count);

        return await _ffmpegService.ConcatenateAsync(
            clipPaths,
            settings.GetConcatenatedOutputPath(),
            settings.Encoding,
            cancellationToken).ConfigureAwait(false);
    }

    private Task DeleteClipFilesAsync(IReadOnlyList<Cs2ClipResult> clipResults)
    {
        foreach (Cs2ClipResult clip in clipResults.Where(r => r.Success && !string.IsNullOrEmpty(r.OutputFilePath)))
        {
            try
            {
                if (File.Exists(clip.OutputFilePath))
                {
                    File.Delete(clip.OutputFilePath);
                    LogClipFileDeleted(clip.ClipIndex, clip.OutputFilePath);
                }
            }
            catch (Exception ex)
            {
                LogClipFileDeletionError(clip.ClipIndex, clip.OutputFilePath, ex);
                // Continue deleting other files even if one fails
            }
        }

        return Task.CompletedTask;
    }

    #region Logging

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "Playing demo tick range with capture: {StartTick}-{EndTick}, output: {OutputFileName}")]
    partial void LogPlayDemoTickRangeWithCapture(int startTick, int endTick, string outputFileName);

    [LoggerMessage(EventId = 108, Level = LogLevel.Information,
        Message = "Capture completed: success={Success}, output={OutputFilePath}")]
    partial void LogCaptureCompleted(bool success, string outputFilePath);

    [LoggerMessage(EventId = 200, Level = LogLevel.Information,
        Message = "Starting compilation capture with {ClipCount} clips")]
    partial void LogCaptureCompilationStart(int clipCount);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information,
        Message = "Compilation capture completed successfully: {ClipCount} clips")]
    partial void LogCaptureCompilationComplete(int clipCount);

    [LoggerMessage(EventId = 202, Level = LogLevel.Error, Message = "Compilation capture failed")]
    partial void LogCaptureCompilationError(Exception exception);

    [LoggerMessage(EventId = 203, Level = LogLevel.Debug, Message = "Loading demo: {DemoPath}")]
    partial void LogLoadingDemo(string demoPath);

    [LoggerMessage(EventId = 204, Level = LogLevel.Debug, Message = "Closing previous demo: {DemoPath}")]
    partial void LogClosingPreviousDemo(string demoPath);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information,
        Message = "Capturing clip {ClipIndex}: ticks {StartTick}-{EndTick}, player {PlayerName}")]
    partial void LogCapturingClip(int clipIndex, int startTick, int endTick, string playerName);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information,
        Message = "Clip {ClipIndex} captured successfully: {OutputPath}")]
    partial void LogClipCaptureSuccess(int clipIndex, string outputPath);

    [LoggerMessage(EventId = 207, Level = LogLevel.Error, Message = "Clip {ClipIndex} capture failed: {ErrorMessage}")]
    partial void LogClipCaptureFailed(int clipIndex, string? errorMessage);

    [LoggerMessage(EventId = 208, Level = LogLevel.Error, Message = "Exception while capturing clip {ClipIndex}")]
    partial void LogClipCaptureException(int clipIndex, Exception exception);

    [LoggerMessage(EventId = 300, Level = LogLevel.Information,
        Message = "Concatenating {ClipCount} clips -> {OutputPath}")]
    partial void LogConcatenatingClips(int clipCount, string outputPath);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "FFmpeg service is not configured")]
    partial void LogFfmpegServiceNotAvailable();

    [LoggerMessage(EventId = 302, Level = LogLevel.Warning, Message = "Concatenation failed: {ErrorMessage}")]
    partial void LogConcatenationFailed(string? errorMessage);

    [LoggerMessage(EventId = 303, Level = LogLevel.Debug, Message = "Deleted clip file {ClipIndex}: {FilePath}")]
    partial void LogClipFileDeleted(int clipIndex, string filePath);

    [LoggerMessage(EventId = 304, Level = LogLevel.Warning,
        Message = "Failed to delete clip file {ClipIndex} ({FilePath})")]
    partial void LogClipFileDeletionError(int clipIndex, string filePath, Exception ex);

    #endregion
}
