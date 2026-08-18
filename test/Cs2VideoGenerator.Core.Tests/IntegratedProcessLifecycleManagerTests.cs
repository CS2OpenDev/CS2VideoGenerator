#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.ProcessManagement;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Terminal-path behavior of <see cref="IntegratedProcessLifecycleManager{TConfig}" />. The
///     after-stop/teardown hook (which restores backups in real CS2 mode) must run exactly once per
///     launch, however the process terminates: explicit stop, crash, or user close. The hook
///     used to run only when StopAsync was called while the process was still alive, so crashes and
///     user-closes left game files modified.
/// </summary>
public class IntegratedProcessLifecycleManagerTests
{
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     An external kill stands in for a CS2 crash or the user closing the game. The hook still
    ///     runs, and a later StopAsync is a no-throw no-op that does not run it again.
    /// </summary>
    [Fact]
    public async Task ExternalProcessDeath_RunsAfterStopHook_AndSubsequentStopIsNoOpNoThrow()
    {
        (string exe, string[] args) = TestProcesses.SleeperCommand();
        await using FakeLifecycleManager manager = new(exe, args);

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);
        Assert.True(manager.IsRunning);
        Assert.NotNull(manager.ProcessId);

        // Kill the process externally - the manager did not initiate this stop.
        Process.GetProcessById(manager.ProcessId!.Value).Kill(true);

        using CancellationTokenSource cts = new(ExitWaitTimeout);
        await manager.WaitForExitAsync(cts.Token);

        Assert.Equal(1, manager.ProcessExitedCalls);
        Assert.Equal(1, manager.AfterStopCalls);
        Assert.False(manager.IsRunning);

        // stop after the process already died: no throw, no second teardown
        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, manager.AfterStopCalls);

        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, manager.AfterStopCalls);
    }

    [Fact]
    public async Task NaturalExit_RunsAfterStopHookOnce_AndStopAsyncRemainsSafe()
    {
        (string exe, string[] args) = TestProcesses.QuickExitCommand();
        await using FakeLifecycleManager manager = new(exe, args);

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);

        using CancellationTokenSource cts = new(ExitWaitTimeout);
        ProcessExitResult result = await manager.WaitForExitAsync(cts.Token);

        Assert.Equal(ProcessExitReason.Completed, result.Reason);
        Assert.Equal(1, manager.ProcessExitedCalls);
        Assert.Equal(1, manager.AfterStopCalls);

        await manager.StopAsync(TestContext.Current.CancellationToken);
        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, manager.AfterStopCalls);
    }

    /// <summary>
    ///     Explicit StopAsync while the process runs: the hook fires once, not once in the exit
    ///     monitor and once in StopAsync.
    /// </summary>
    [Fact]
    public async Task ExplicitStop_RunsAfterStopHookExactlyOnce()
    {
        (string exe, string[] args) = TestProcesses.SleeperCommand();
        await using FakeLifecycleManager manager = new(exe, args);

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);
        Assert.True(manager.IsRunning);

        await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(manager.IsRunning);
        Assert.Equal(1, manager.ProcessExitedCalls);
        Assert.Equal(1, manager.AfterStopCalls);
    }

    /// <summary>Nothing launched, nothing to tear down.</summary>
    [Fact]
    public async Task StopWithoutLaunch_DoesNotRunHook_AndDoesNotThrow()
    {
        (string exe, string[] args) = TestProcesses.SleeperCommand();
        await using FakeLifecycleManager manager = new(exe, args);

        await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, manager.AfterStopCalls);
        Assert.Equal(0, manager.ProcessExitedCalls);
    }

    /// <summary>
    ///     The exit monitor and StopSessionAsync racing each other: the hook still runs once and
    ///     nothing throws.
    /// </summary>
    [Fact]
    public async Task ConcurrentStops_RacingNaturalExit_RunHookExactlyOnce()
    {
        (string exe, string[] args) = TestProcesses.QuickExitCommand();
        await using FakeLifecycleManager manager = new(exe, args);

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);

        Task[] stops =
        [
            manager.StopAsync(TestContext.Current.CancellationToken),
            manager.StopAsync(TestContext.Current.CancellationToken),
            manager.StopAsync(TestContext.Current.CancellationToken)
        ];
        await Task.WhenAll(stops);

        using CancellationTokenSource cts = new(ExitWaitTimeout);
        await manager.WaitForExitAsync(cts.Token);

        Assert.Equal(1, manager.AfterStopCalls);
    }

    /// <summary>
    ///     Minimal concrete lifecycle manager that launches an arbitrary OS process and counts hook
    ///     invocations.
    /// </summary>
    private sealed class FakeLifecycleManager(string executablePath, string[] arguments)
        : IntegratedProcessLifecycleManager<object>(null)
    {
        private int _afterStopCalls;
        private int _processExitedCalls;

        public int AfterStopCalls => Volatile.Read(ref _afterStopCalls);

        public int ProcessExitedCalls => Volatile.Read(ref _processExitedCalls);

        protected override string LogPrefix => "FAKE";

        protected override string? DiscoverExecutable(object config) => executablePath;

        protected override IEnumerable<string> BuildArguments(object config) => arguments;

        protected override Task OnProcessExitedAsync(ProcessExitResult result)
        {
            Interlocked.Increment(ref _processExitedCalls);
            return Task.CompletedTask;
        }

        protected override Task OnAfterStopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _afterStopCalls);
            return Task.CompletedTask;
        }
    }
}
