#region

using Cs2VideoGenerator.Core.ProcessManagement;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     The <see cref="IntegratedProcessLifecycleManager{TConfig}.LaunchAsync" /> TOCTOU fix. The
///     already-running check and the process-handle assignment used to live in separate
///     lock regions with async hooks in between, so two concurrent LaunchAsync calls could both slip
///     past the check and launch two processes. A second concurrent launch must now fail fast, and
///     the launch slot must be released on failure so later launches still work.
/// </summary>
public class IntegratedProcessLifecycleManagerLaunchRaceTests
{
    private static readonly TimeSpan HookWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The first launch is parked inside OnBeforeLaunchAsync, between the old check and the old
    ///     assignment. A launch arriving there must fail with "already running", not start a second
    ///     process.
    /// </summary>
    [Fact]
    public async Task ConcurrentLaunch_WhileFirstLaunchInFlight_FailsFast()
    {
        (string exe, string[] args) = TestProcesses.SleeperCommand();
        await using GatedLifecycleManager manager = new(exe, args);

        Task firstLaunch = manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);

        // Wait until the first launch is inside the pre-launch hook - exactly the window the
        // old code left unguarded.
        await manager.BeforeLaunchEntered.Task
            .WaitAsync(HookWaitTimeout, TestContext.Current.CancellationToken);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.LaunchAsync(new object(), TestContext.Current.CancellationToken));
        Assert.Contains("already running", ex.Message);
        Assert.Equal(1, manager.BeforeLaunchCalls);

        // The first launch must complete unaffected.
        manager.ReleaseBeforeLaunch.TrySetResult();
        await firstLaunch;
        Assert.True(manager.IsRunning);

        // Teardown semantics must be preserved: explicit stop runs the hook exactly once.
        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(manager.IsRunning);
        Assert.Equal(1, manager.AfterStopCalls);
    }

    /// <summary>
    ///     A failed launch (executable missing) must release the launch slot: a retry must surface
    ///     the same underlying error, not a bogus "already running".
    /// </summary>
    [Fact]
    public async Task FailedLaunch_ReleasesLaunchSlot_SoRetryIsPossible()
    {
        string missingExe = Path.Combine(Path.GetTempPath(), "csvg-missing-" + Path.GetRandomFileName());
        await using GatedLifecycleManager manager = new(missingExe, []);
        manager.ReleaseBeforeLaunch.TrySetResult();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => manager.LaunchAsync(new object(), TestContext.Current.CancellationToken));

        // Not InvalidOperationException("already running") - the slot was released.
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => manager.LaunchAsync(new object(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SequentialLaunchStopLaunch_Succeeds()
    {
        (string exe, string[] args) = TestProcesses.SleeperCommand();
        await using GatedLifecycleManager manager = new(exe, args);
        manager.ReleaseBeforeLaunch.TrySetResult();

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);
        Assert.True(manager.IsRunning);
        await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(manager.IsRunning);

        await manager.LaunchAsync(new object(), TestContext.Current.CancellationToken);
        Assert.True(manager.IsRunning);
        await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, manager.BeforeLaunchCalls);
        Assert.Equal(2, manager.AfterStopCalls);
    }

    /// <summary>
    ///     Lifecycle manager whose pre-launch hook can be parked, widening the historical race
    ///     window deterministically.
    /// </summary>
    private sealed class GatedLifecycleManager(string executablePath, string[] arguments)
        : IntegratedProcessLifecycleManager<object>(null)
    {
        private int _afterStopCalls;
        private int _beforeLaunchCalls;

        public TaskCompletionSource BeforeLaunchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBeforeLaunch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BeforeLaunchCalls => Volatile.Read(ref _beforeLaunchCalls);

        public int AfterStopCalls => Volatile.Read(ref _afterStopCalls);

        protected override string LogPrefix => "FAKE";

        protected override string? DiscoverExecutable(object config) => executablePath;

        protected override IEnumerable<string> BuildArguments(object config) => arguments;

        protected override async Task OnBeforeLaunchAsync(object config, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _beforeLaunchCalls);
            BeforeLaunchEntered.TrySetResult();
            await ReleaseBeforeLaunch.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override Task OnAfterStopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _afterStopCalls);
            return Task.CompletedTask;
        }
    }
}
