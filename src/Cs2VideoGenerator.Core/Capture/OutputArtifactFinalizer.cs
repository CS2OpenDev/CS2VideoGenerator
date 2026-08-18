#region

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Turns a file a capture backend just finished writing into the file the caller asked for.
///     Backends name their output on their own terms (OBS uses a wall-clock timestamp, a frame-dump
///     encoder writes to a staging path), so every backend needs the same two steps: wait until the
///     writer has actually let go of the file, then move it into place without tripping over a handle
///     that lingers a moment longer.
/// </summary>
public sealed partial class OutputArtifactFinalizer
{
    /// <summary>
    ///     How many times to poll for the file to settle, at
    ///     <see cref="FileReadyPollDelayMs" /> intervals (~5 seconds in total).
    /// </summary>
    private const int FileReadyMaxAttempts = 20;

    private const int FileReadyPollDelayMs = 250;

    /// <summary>
    ///     Consecutive polls that must report the same non-zero size before the file counts as
    ///     settled.
    /// </summary>
    private const int StableSizeThreshold = 2;

    private const int RenameMaxAttempts = 5;
    private const int RenameBaseDelayMs = 200;

    private readonly ILogger _logger;

    /// <summary>
    ///     Creates a finalizer. A null logger is supported and falls back to a no-op logger.
    /// </summary>
    public OutputArtifactFinalizer(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>
    ///     Waits for a file to exist, become openable exclusively, and stop growing. Returns when the
    ///     file has settled or when the attempt budget runs out. A timeout is logged as a warning, not
    ///     an error: the caller can usually still work with the file.
    /// </summary>
    public async Task WaitForFileReadyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        long lastSize = -1;
        int stableSizeCount = 0;

        for (int attempt = 0; attempt < FileReadyMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                await Task.Delay(FileReadyPollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                // FileShare.None fails while the writer still holds the file, which is exactly the
                // signal wanted here: an exclusive open means the backend has closed it.
                using (FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    long currentSize = stream.Length;

                    if (currentSize == lastSize && currentSize > 0)
                    {
                        stableSizeCount++;
                        if (stableSizeCount >= StableSizeThreshold)
                        {
                            LogFileReady(filePath, currentSize, attempt + 1);
                            return;
                        }
                    }
                    else
                    {
                        stableSizeCount = 0;
                    }

                    lastSize = currentSize;
                }
            }
            catch (IOException)
            {
                // Still locked by the writer: wait and retry.
                stableSizeCount = 0;
            }

            await Task.Delay(FileReadyPollDelayMs, cancellationToken).ConfigureAwait(false);
        }

        LogFileReadyTimeout(filePath, FileReadyMaxAttempts);
    }

    /// <summary>
    ///     Moves a file to its destination, replacing an existing destination, retrying with
    ///     exponential backoff while the source is still locked. Returns false when the move keeps
    ///     failing; callers then keep the backend's own filename rather than losing the recording.
    /// </summary>
    public async Task<bool> TryRenameWithRetryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        for (int attempt = 1; attempt <= RenameMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.Move(sourcePath, destinationPath);
                return true;
            }
            catch (IOException ex) when (attempt < RenameMaxAttempts)
            {
                LogRenameRetry(sourcePath, attempt, ex.Message);
                await Task.Delay(RenameBaseDelayMs * (1 << (attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCouldNotRenameOutputFile(ex, sourcePath, destinationPath);
                return false;
            }
        }

        return false;
    }

    #region Logging

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "File ready: {FilePath} ({Size} bytes, {Attempts} attempts)")]
    partial void LogFileReady(string filePath, long size, int attempts);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Timeout waiting for file to be ready: {FilePath} (after {MaxAttempts} attempts)")]
    partial void LogFileReadyTimeout(string filePath, int maxAttempts);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Rename retry for {FilePath} (attempt {Attempt}): {Error}")]
    partial void LogRenameRetry(string filePath, int attempt, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Could not rename output file from {SourcePath} to {DestPath}")]
    partial void LogCouldNotRenameOutputFile(Exception exception, string sourcePath, string destPath);

    #endregion
}
