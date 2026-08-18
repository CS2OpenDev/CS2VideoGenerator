namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Shared test helper: harmless OS commands used as stand-ins for CS2 in
///     process-lifecycle tests.
/// </summary>
internal static class TestProcesses
{
    /// <summary>
    ///     Returns an executable + arguments that stay alive for a long time (until killed).
    /// </summary>
    public static (string Executable, string[] Args) SleeperCommand() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "120", "127.0.0.1"])
            : ("/bin/sleep", ["120"]);

    /// <summary>
    ///     Returns an executable + arguments that exit almost immediately on their own.
    /// </summary>
    public static (string Executable, string[] Args) QuickExitCommand() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "exit", "0"])
            : ("/bin/sleep", ["0"]);
}
