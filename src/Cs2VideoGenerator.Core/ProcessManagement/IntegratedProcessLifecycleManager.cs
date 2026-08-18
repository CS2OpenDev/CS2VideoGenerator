#region

using System.Diagnostics;
using CliWrap;
using Cs2VideoGenerator.Core.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Base for anything that owns an external application's process: discovery, launch through
///     CliWrap, stdout/stderr logging, non-blocking execution, graceful shutdown.
/// </summary>
/// <typeparam name="TConfig">Configuration type for the specific process manager.</typeparam>
public abstract class IntegratedProcessLifecycleManager<TConfig> : IAsyncDisposable, IDisposable
    where TConfig : class
{
    private readonly Lock _gate = new();
    private readonly ILogger? _logger;
    private readonly ILogger? _processOutputLogger;

    // 1 while the after-stop/teardown hook is still owed for the current launch, 0 otherwise.
    // consumed with Interlocked.Exchange, so the hook runs exactly once per launch whether the
    // process exits on its own (crash/user-close), is stopped through StopAsync, or both race.
    private int _afterStopPending;

    private bool _disposed;
    private TaskCompletionSource<ProcessExitResult>? _exitedTcs;

    // True while a LaunchAsync call sits between its already-running check and the _processCts
    // assignment. Guarded by _gate. Closes the TOCTOU window where two concurrent launches both
    // slip past the check.
    private bool _launchInProgress;

    private Task? _monitorTask;

    private CancellationTokenSource? _processCts;

    /// <summary>
    ///     Initializes a new instance of the process lifecycle manager.
    /// </summary>
    /// <param name="logger">Logger for the process manager itself.</param>
    /// <param name="processOutputLogger">Optional separate logger for process stdout/stderr output.</param>
    protected IntegratedProcessLifecycleManager(
        ILogger? logger,
        ILogger? processOutputLogger = null)
    {
        _logger = logger;
        _processOutputLogger = processOutputLogger ?? logger;
    }

    #region IAsyncDisposable

    /// <summary>
    ///     Stops the managed process via <see cref="StopAsync" /> (graceful shutdown first, force
    ///     kill on timeout) and runs any still-owed teardown hook (<see cref="OnAfterStopAsync" />,
    ///     e.g. backup restore), swallowing stop errors so disposal never throws.
    ///     Idempotent: subsequent calls return immediately.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // StopAsync is safe to call even when the process already exited (or never started):
        // it runs any still-owed teardown (e.g. backup restore) and otherwise no-ops.
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors during disposal
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Synchronous disposal bridge. Microsoft.Extensions.DependencyInjection throws when a
    ///     service provider is disposed synchronously while holding a service that only implements
    ///     <see cref="IAsyncDisposable" /> — so a plain <c>using var provider = ...</c> in a
    ///     consumer app would crash without this. Runs <see cref="DisposeAsync" /> on the thread
    ///     pool (off any ambient SynchronizationContext), bounded to 40 seconds. That covers the
    ///     slowest subclass teardown: ObsProcessManager's 30-second graceful shutdown, which lets OBS
    ///     finalize an in-progress recording, plus the post-kill monitor wait. Prefer
    ///     <see cref="DisposeAsync" />, which awaits the full teardown without a time bound.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(40)))
            {
                _logger?.LogWarning(
                    "Synchronous Dispose timed out after 40s with process teardown still in flight; " +
                    "the managed process may be left running. Prefer DisposeAsync for a complete teardown.");
            }
        }
        catch
        {
            // Ignore errors during disposal (mirrors DisposeAsync)
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Process Monitoring

    private async Task MonitorProcessAsync(CommandTask<CommandResult> commandTask)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessExitResult result;

        try
        {
            CommandResult commandResult = await commandTask.ConfigureAwait(false);
            stopwatch.Stop();

            result = new ProcessExitResult(commandResult.ExitCode, ProcessExitReason.Completed)
            {
                Duration = stopwatch.Elapsed
            };

            LogProcessExited(commandResult.ExitCode, "completed");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            result = new ProcessExitResult(-1, ProcessExitReason.Canceled) { Duration = stopwatch.Elapsed };

            LogProcessExited(-1, "cancelled");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result = new ProcessExitResult(-1, ProcessExitReason.Error)
            {
                Duration = stopwatch.Elapsed, Exception = ex
            };

            LogProcessError(ex);
        }
        finally
        {
            lock (_gate)
            {
                _processCts?.Dispose();
                _processCts = null;
                ProcessId = null;
            }
        }

        try
        {
            await OnProcessExitedAsync(result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnExitedError(ex);
        }

        // Run the after-stop/teardown hook (e.g. backup restore) here too, so it executes even when
        // the process exits on its own (crash/user-close) and StopAsync is never called while the
        // process is alive. RunAfterStopHookOnceAsync keeps it to at most once per launch.
        try
        {
            await RunAfterStopHookOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnAfterStopError(ex);
        }

        _exitedTcs?.TrySetResult(result);
        Exited?.Invoke(this, new ProcessExitedEventArgs(result));
    }

    #endregion

    #region Abstract Members - Must be implemented by derived classes

    /// <summary>
    ///     The prefix used for log messages (e.g., "CS2", "OBS").
    /// </summary>
    protected abstract string LogPrefix { get; }

    /// <summary>
    ///     Discovers the executable path for the process.
    /// </summary>
    /// <param name="config">The configuration for this launch.</param>
    /// <returns>The full path to the executable, or null if not found.</returns>
    protected abstract string? DiscoverExecutable(TConfig config);

    /// <summary>
    ///     Builds the command line arguments for the process.
    /// </summary>
    /// <param name="config">The configuration for this launch.</param>
    /// <returns>Enumerable of command line arguments.</returns>
    protected abstract IEnumerable<string> BuildArguments(TConfig config);

    #endregion

    #region Virtual Members - Override for custom behavior

    /// <summary>
    ///     Called before the process is launched. Override for pre-launch setup (e.g., plugin install).
    /// </summary>
    protected virtual Task OnBeforeLaunchAsync(TConfig config, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    ///     Called after the process has started successfully. Override for post-launch initialization.
    /// </summary>
    protected virtual Task OnAfterLaunchAsync(TConfig config, int processId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    ///     Called when the process exits. Override to handle exit conditions (e.g., cleanup, metrics).
    /// </summary>
    /// <param name="result">Information about how the process exited.</param>
    protected virtual Task OnProcessExitedAsync(ProcessExitResult result)
        => Task.CompletedTask;

    /// <summary>
    ///     Called after the process has terminated, whatever the terminal path: explicit
    ///     <see cref="StopAsync" />, crash, or the user closing the process. At most once per launch.
    ///     Override for final cleanup, e.g. restoring backups. Make the implementation idempotent
    ///     anyway (safe against already-cleaned-up state), as defense in depth.
    /// </summary>
    protected virtual Task OnAfterStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    ///     Gets the working directory for the process. Defaults to the executable's directory.
    /// </summary>
    protected virtual string? GetWorkingDirectory(TConfig config, string executablePath)
        => Path.GetDirectoryName(executablePath);

    /// <summary>
    ///     Gets the standard-input pipe for the process, or null for CliWrap's default
    ///     (an empty stdin — the child sees immediate EOF). Called once per launch, before
    ///     the process starts. Override to feed the child's stdin for its whole lifetime
    ///     (e.g. the mock_server user-action injection channel).
    /// </summary>
    protected virtual PipeSource? GetStandardInputPipe(TConfig config)
        => null;

    /// <summary>
    ///     Gets the graceful shutdown timeout. Defaults to 5 seconds.
    /// </summary>
    protected virtual TimeSpan GracefulShutdownTimeout => TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Whether <see cref="StopAsync" /> first requests a graceful shutdown (window close /
    ///     SIGTERM) before force-killing. Defaults to true. Subclasses whose graceful exit is
    ///     problematic — and whose important state is already persisted by the time StopAsync
    ///     runs — can override this to false to skip straight to a force kill.
    /// </summary>
    protected virtual bool AttemptGracefulShutdown => true;

    /// <summary>
    ///     How long <see cref="LaunchAsync" /> waits after the process starts before returning. Zero
    ///     by default; override it for apps that need a moment to come up.
    /// </summary>
    protected virtual TimeSpan PostLaunchDelay => TimeSpan.Zero;

    #endregion

    #region Public State

    /// <summary>
    ///     The process ID of the running process, or null if not running.
    /// </summary>
    public int? ProcessId { get; private set; }

    /// <summary>
    ///     Whether the process is currently running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _processCts != null && !_processCts.IsCancellationRequested;
            }
        }
    }

    /// <summary>
    ///     Raised when the process exits.
    /// </summary>
    public event EventHandler<ProcessExitedEventArgs>? Exited;

    /// <summary>
    ///     The configuration used for the current/last launch. Available during exit handlers.
    /// </summary>
    protected TConfig? CurrentConfig { get; private set; }

    #endregion

    #region Public Operations

    /// <summary>
    ///     Launches the process with the provided configuration.
    /// </summary>
    /// <param name="config">Configuration for this launch.</param>
    /// <param name="cancellationToken">Token to cancel the launch operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if a process is already running or executable not found.</exception>
    public async Task LaunchAsync(TConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        using Activity? activity = CsvgTracing.StartActivity($"{GetType().Name}.Launch");

        // Claim the launch slot atomically with the already-running check so concurrent
        // LaunchAsync calls cannot both pass the check while the first is still between
        // this check and the _processCts assignment below.
        lock (_gate)
        {
            if (_processCts != null || _launchInProgress)
            {
                throw new InvalidOperationException($"{LogPrefix} process is already running. Call StopAsync first.");
            }

            _launchInProgress = true;
        }

        try
        {
            await LaunchCoreAsync(config, activity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _launchInProgress = false;
            }
        }
    }

    // split out of LaunchAsync so the launch slot is always released exactly once, via try/finally.
    private async Task LaunchCoreAsync(TConfig config, Activity? activity, CancellationToken cancellationToken)
    {
        string? executablePath = DiscoverExecutable(config);
        activity?.SetTag($"{LogPrefix.ToLowerInvariant()}.exePath", executablePath);

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException($"{LogPrefix} executable not found.");
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"{LogPrefix} executable not found at: {executablePath}", executablePath);
        }

        LogLaunching(executablePath);

        // exit handlers read this
        CurrentConfig = config;

        // From here on the after-stop/teardown hook is owed for this launch attempt.
        // OnBeforeLaunchAsync may take backups or modify game files, and those have to be undone
        // (via OnAfterStopAsync) no matter how the process terminates, or even if launch fails.
        Interlocked.Exchange(ref _afterStopPending, 1);

        await OnBeforeLaunchAsync(config, cancellationToken).ConfigureAwait(false);

        List<string> arguments = BuildArguments(config).ToList();
        string? workingDirectory = GetWorkingDirectory(config, executablePath);

        LogArguments(string.Join(" ", arguments));

        PipeTarget stdOutPipe = _processOutputLogger != null
            ? PipeTarget.ToDelegate(LogProcessStdOut)
            : PipeTarget.Null;
        PipeTarget stdErrPipe = _processOutputLogger != null
            ? PipeTarget.ToDelegate(LogProcessStdErr)
            : PipeTarget.Null;

        // Standalone CTS, deliberately not linked to any external token, so the process is only ever
        // stopped by an explicit StopAsync and gets its graceful shutdown. Linked to the caller's
        // token, application shutdown would kill the process before DisposeAsync could ask nicely.
        CancellationTokenSource processCts = new();

        Command cmd = Cli.Wrap(executablePath)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory ?? "")
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(stdOutPipe)
            .WithStandardErrorPipe(stdErrPipe);

        // Optional stdin feed (default null = CliWrap's empty-stdin default, immediate EOF).
        if (GetStandardInputPipe(config) is { } stdInPipe)
        {
            cmd = cmd.WithStandardInputPipe(stdInPipe);
        }

        CommandTask<CommandResult> commandTask;
        lock (_gate)
        {
            _processCts = processCts;
            _exitedTcs =
                new TaskCompletionSource<ProcessExitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            commandTask = cmd.ExecuteAsync(processCts.Token);
            ProcessId = commandTask.ProcessId;
        }

        LogProcessStarted(commandTask.ProcessId);
        activity?.SetTag($"{LogPrefix.ToLowerInvariant()}.pid", commandTask.ProcessId);

        _monitorTask = MonitorProcessAsync(commandTask);

        // the hook and the delay below both take the caller's token, so launch stays cancellable
        await OnAfterLaunchAsync(config, commandTask.ProcessId, cancellationToken).ConfigureAwait(false);

        if (PostLaunchDelay > TimeSpan.Zero)
        {
            await Task.Delay(PostLaunchDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Stops the process and waits for cleanup to complete.
    ///     First attempts graceful shutdown, then force kills if the process doesn't exit within the timeout.
    ///     Safe to call when the process already exited on its own (or never started): any still-owed
    ///     teardown (<see cref="OnAfterStopAsync" />) is executed exactly once, then this returns.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the stop operation.</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity($"{GetType().Name}.Stop");

        CancellationTokenSource? cts;
        Task? monitor;
        int? processId;

        lock (_gate)
        {
            cts = _processCts;
            monitor = _monitorTask;
            processId = ProcessId;
        }

        if (cts == null)
        {
            // Not running: either never launched, or the process already exited on its own.
            // The exit monitor normally runs the teardown hook; wait for it to finish so we don't
            // return while cleanup is still in flight, then run the hook ourselves if it is still
            // owed (e.g. launch failed after OnBeforeLaunchAsync). No-ops when nothing is owed.
            if (monitor != null)
            {
                try
                {
                    await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Monitor failures are already logged inside MonitorProcessAsync
                }
            }

            await RunAfterStopHookOnceAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        LogStopping();

        bool gracefullyExited = false;

        // Unless the subclass opts out via AttemptGracefulShutdown — e.g. OBS, whose graceful exit
        // path crashes and pops an interaction-blocking dialog. It is force-killed instead, which is
        // safe because its recording is already finalized over the WebSocket before StopAsync runs.
        if (processId.HasValue && AttemptGracefulShutdown)
        {
            gracefullyExited = await TryGracefulShutdownAsync(processId.Value, monitor, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!gracefullyExited)
        {
            LogForceKilling();
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation errors
            }

            if (monitor != null)
            {
                try
                {
                    await monitor.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LogShutdownTimeout();
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during shutdown is ok
                }
            }
        }

        // Post-stop hook. When graceful shutdown succeeded, the monitor task already ran the hook
        // (we awaited it above), so this no-ops; it only runs here when the monitor is still stuck
        // after a force kill.
        await RunAfterStopHookOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    // At most once per launch. Safe to call concurrently from the exit monitor and from StopAsync:
    // whichever consumes the pending flag first does the teardown.
    private async Task RunAfterStopHookOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _afterStopPending, 0) != 1)
        {
            return; // Teardown already ran (or was never owed)
        }

        await OnAfterStopAsync(cancellationToken).ConfigureAwait(false);
    }

    // false on timeout or failure; the caller then force-kills.
    private async Task<bool> TryGracefulShutdownAsync(int processId, Task? monitor, CancellationToken cancellationToken)
    {
        try
        {
            Process process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return true; // Already exited
            }

            LogRequestingGracefulShutdown();

            // Windows: CloseMainWindow() sends WM_CLOSE to the main window.
            // macOS/Linux: CloseMainWindow() does not work for every app, so SIGTERM is the fallback.
            bool closeRequested = process.CloseMainWindow();

            if (!closeRequested)
            {
                if (OperatingSystem.IsWindows())
                {
                    // CloseMainWindow fails when the app is minimized to the tray; taskkill still
                    // gets WM_CLOSE there.
                    await SendWindowsCloseSignalAsync(processId).ConfigureAwait(false);
                }
                else
                {
                    await SendSigTermAsync(processId).ConfigureAwait(false);
                }
            }

            using CancellationTokenSource gracefulCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gracefulCts.CancelAfter(GracefulShutdownTimeout);

            try
            {
                if (monitor != null)
                {
                    await monitor.WaitAsync(gracefulCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await process.WaitForExitAsync(gracefulCts.Token).ConfigureAwait(false);
                }

                LogGracefulShutdownComplete();
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Graceful shutdown timed out
                LogGracefulShutdownTimeout();
                return false;
            }
        }
        catch (ArgumentException)
        {
            // Process already exited
            return true;
        }
        catch (InvalidOperationException)
        {
            // Process already exited
            return true;
        }
        catch (Exception ex)
        {
            LogGracefulShutdownError(ex);
            return false;
        }
    }

    private static async Task SendSigTermAsync(int processId)
    {
        try
        {
            using Process killProcess = new();
            killProcess.StartInfo = new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-TERM {processId}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            killProcess.Start();
            await killProcess.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore; force kill is the fallback
        }
    }

    // taskkill reaches tray-minimized apps that CloseMainWindow cannot.
    private static async Task SendWindowsCloseSignalAsync(int processId)
    {
        try
        {
            // taskkill without /F sends WM_CLOSE to the process, requesting graceful shutdown
            using Process taskkillProcess = new();
            taskkillProcess.StartInfo = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {processId}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            taskkillProcess.Start();
            await taskkillProcess.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore; force kill is the fallback
        }
    }

    /// <summary>
    ///     Waits for the process to exit naturally.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    /// <returns>Information about how the process exited.</returns>
    public async Task<ProcessExitResult> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<ProcessExitResult>? tcs;

        lock (_gate)
        {
            tcs = _exitedTcs;
        }

        if (tcs == null)
        {
            return new ProcessExitResult(-1, ProcessExitReason.NotStarted);
        }

        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Logging

    private void LogLaunching(string path)
        => _logger?.LogInformation("[{Prefix}] Launching: {Path}", LogPrefix, path);

    private void LogArguments(string args)
        => _logger?.LogDebug("[{Prefix}] Arguments: {Args}", LogPrefix, args);

    private void LogProcessStarted(int pid)
        => _logger?.LogInformation("[{Prefix}] Process started (PID: {Pid})", LogPrefix, pid);

    private void LogStopping()
        => _logger?.LogInformation("[{Prefix}] Stopping process...", LogPrefix);

    private void LogShutdownTimeout()
        => _logger?.LogWarning("[{Prefix}] Process did not exit within shutdown timeout", LogPrefix);

    private void LogRequestingGracefulShutdown()
        => _logger?.LogInformation("[{Prefix}] Requesting graceful shutdown...", LogPrefix);

    private void LogGracefulShutdownComplete()
        => _logger?.LogInformation("[{Prefix}] Process exited gracefully", LogPrefix);

    private void LogGracefulShutdownTimeout()
        => _logger?.LogWarning("[{Prefix}] Graceful shutdown timed out, will force kill", LogPrefix);

    private void LogGracefulShutdownError(Exception ex)
        => _logger?.LogWarning(ex, "[{Prefix}] Error during graceful shutdown attempt", LogPrefix);

    private void LogForceKilling()
        => _logger?.LogWarning("[{Prefix}] Force killing process...", LogPrefix);

    private void LogProcessExited(int exitCode, string reason)
        => _logger?.LogInformation("[{Prefix}] Process exited (code: {ExitCode}, reason: {Reason})", LogPrefix,
            exitCode, reason);

    private void LogProcessError(Exception ex)
        => _logger?.LogError(ex, "[{Prefix}] Process error", LogPrefix);

    private void LogOnExitedError(Exception ex)
        => _logger?.LogWarning(ex, "[{Prefix}] Error in OnProcessExitedAsync callback", LogPrefix);

    private void LogOnAfterStopError(Exception ex)
        => _logger?.LogWarning(ex, "[{Prefix}] Error in OnAfterStopAsync callback", LogPrefix);

    private void LogProcessStdOut(string line)
        => _processOutputLogger?.LogInformation("[{Prefix}] {Line}", LogPrefix, line);

    private void LogProcessStdErr(string line)
        => _processOutputLogger?.LogWarning("[{Prefix}-ERR] {Line}", LogPrefix, line);

    #endregion
}

