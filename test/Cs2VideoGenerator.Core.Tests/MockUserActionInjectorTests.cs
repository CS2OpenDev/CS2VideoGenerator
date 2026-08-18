#region

using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     v1.1 mock user-action injection channel (Core hook): DI selection mirrors the
///     process-manager selection (mock mode → the mock manager doubles as the injector;
///     real mode → a no-op stub), and the injector fails fast when no mock_server runs.
/// </summary>
public class MockUserActionInjectorTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? postConfigure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddCs2VideoGeneratorCore(configuration);
        postConfigure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static MockCs2ProcessManager CreateManager()
        => new(
            new NativeAssetProvider(),
            Options.Create(new Cs2VideoGeneratorOptions()),
            NullLoggerFactory.Instance);

    [Fact]
    public async Task MockMode_ResolvesInjectorBackedByMockProcessManager()
    {
        await using ServiceProvider provider = BuildProvider(services =>
            services.Configure<Cs2VideoGeneratorOptions>(o => o.MockMode = true));

        IMockUserActionInjector injector = provider.GetRequiredService<IMockUserActionInjector>();

        Assert.Same(provider.GetRequiredService<ICs2ProcessManager>(), injector);
    }

    [Fact]
    public async Task RealMode_ResolvesNoOpInjector_UnavailableAndThrowing()
    {
        await using ServiceProvider provider = BuildProvider();

        IMockUserActionInjector injector = provider.GetRequiredService<IMockUserActionInjector>();

        Assert.False(injector.IsAvailable);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => injector.InjectAsync(MockUserActions.Pause, TestContext.Current.CancellationToken));
        Assert.Contains("mock mode is not active", ex.Message);
    }

    [Fact]
    public async Task MockManager_WithoutRunningProcess_IsUnavailableAndThrows()
    {
        await using MockCs2ProcessManager manager = CreateManager();

        Assert.False(((IMockUserActionInjector)manager).IsAvailable);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IMockUserActionInjector)manager).InjectAsync(MockUserActions.Pause,
                TestContext.Current.CancellationToken));
        Assert.Contains("not running", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InjectAsync_EmptyCommand_ThrowsArgumentException(string command)
    {
        await using MockCs2ProcessManager manager = CreateManager();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ((IMockUserActionInjector)manager).InjectAsync(command, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("user-pause\nuser-resume")]
    [InlineData("user-pause\r")]
    public async Task InjectAsync_MultiLineCommand_ThrowsArgumentException(string command)
    {
        await using MockCs2ProcessManager manager = CreateManager();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ((IMockUserActionInjector)manager).InjectAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MockUserActions_GrammarHelpers_FormatAsImplementedByTheMock()
    {
        Assert.Equal("user-pause", MockUserActions.Pause);
        Assert.Equal("user-resume", MockUserActions.Resume);
        Assert.Equal("user-stop", MockUserActions.Stop);
        Assert.Equal("end-demo", MockUserActions.EndDemo);
        Assert.Equal("user-seek 5000", MockUserActions.Seek(5000));
        Assert.Equal("user-playdemo /demos/match.dem", MockUserActions.PlayDemo("/demos/match.dem"));
        // Invariant culture: never a comma decimal separator on the wire.
        Assert.Equal("user-timescale 0.5", MockUserActions.Timescale(0.5f));
        Assert.Equal("user-timescale 2", MockUserActions.Timescale(2f));
    }
}
