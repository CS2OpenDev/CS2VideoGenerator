namespace Cs2VideoGenerator.Cli;

/// <summary>
///     Process exit-code contract for the CLI.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>
    ///     The operation failed at runtime (generation failed, doctor checks failed,
    ///     startup/environment failure, restore/backup/cleanup errors).
    /// </summary>
    public const int OperationFailure = 1;

    /// <summary>
    ///     Usage error: unknown verb, bad arguments, or invalid user input
    ///     (e.g. missing/invalid compilation file, conflicting flags).
    /// </summary>
    public const int UsageError = 2;
}

/// <summary>
///     Singleton holder that lets hosted-service tasks communicate their exit code back to
///     <see cref="Program.Main" />. Tasks set <see cref="ExitCode" /> when they complete;
///     Main reads it after the host shuts down and returns it from the process.
/// </summary>
public sealed class ExitCodeHolder
{
    private int? _exitCode;

    /// <summary>The exit code the process should terminate with. Defaults to <see cref="ExitCodes.Success" />.</summary>
    public int ExitCode
    {
        get => _exitCode ?? ExitCodes.Success;
        set => _exitCode = value;
    }

    public bool IsSet => _exitCode.HasValue;
}
