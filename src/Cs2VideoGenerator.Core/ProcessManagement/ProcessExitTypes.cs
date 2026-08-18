namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Reason why a process exited.
/// </summary>
public enum ProcessExitReason
{
    /// <summary>Process was never started.</summary>
    NotStarted,

    /// <summary>Process completed normally.</summary>
    Completed,

    /// <summary>Process was canceled via StopAsync or cancellation token.</summary>
    Canceled,

    /// <summary>Process exited due to an error.</summary>
    Error
}

/// <summary>
///     Information about how a process exited.
/// </summary>
public record ProcessExitResult(int ExitCode, ProcessExitReason Reason)
{
    /// <summary>How long the process ran.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Exception if the process exited due to an error.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Whether the process exited successfully (completed with exit code 0).</summary>
    public bool Success => Reason == ProcessExitReason.Completed && ExitCode == 0;
}

/// <summary>
///     Event args for process exit events.
/// </summary>
public class ProcessExitedEventArgs(ProcessExitResult result) : EventArgs
{
    /// <summary>Information about how the process exited.</summary>
    public ProcessExitResult Result { get; } = result;
}
