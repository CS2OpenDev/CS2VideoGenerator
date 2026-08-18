#region

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Grpc;
using Cs2VideoGenerator.Core.ProcessManagement;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     A library consumer whose host never mapped the gRPC service
///     (AddGrpc()/MapGrpcService&lt;Cs2GameService&gt;()) gets a fast, descriptive failure from
///     StartAsync, before CS2 or the mock server is launched, instead of a silent multi-minute
///     connect timeout. Also covers the loopback robustness of the TCP probe.
/// </summary>
public class GrpcHostingFastFailTests
{
    /// <summary>
    ///     The DI container a plain console-host consumer builds: AddCs2VideoGeneratorCore with
    ///     in-memory configuration and logging, and no gRPC server hosting at all.
    /// </summary>
    private static ServiceProvider BuildConsoleHostContainer(
        Dictionary<string, string?> configValues,
        Action<IServiceCollection>? postConfigure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddCs2VideoGeneratorCore(configuration);
        postConfigure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Reserves a TCP port that is guaranteed to have no listener by binding an ephemeral
    ///     port and immediately releasing it.
    /// </summary>
    private static int GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     The console-host acceptance scenario: mock mode, nothing listening on the configured
    ///     gRPC port. StartAsync fails well under 2 seconds, names the exact wiring the consumer
    ///     needs, and fails before launching any process - the probe precedes the launch.
    /// </summary>
    [Fact]
    public async Task StartSession_ConsoleHostWithoutGrpcServer_FailsFastWithWiringInstructions()
    {
        int freePort = GetFreePort();
        Mock<ICs2ProcessManager> processManager = new();

        await using ServiceProvider provider = BuildConsoleHostContainer(
            new Dictionary<string, string?>
            {
                ["Cs2VideoGenerator:MockMode"] = "true",
                ["Cs2VideoGenerator:GrpcPort"] = freePort.ToString()
            },
            // Override the process manager so we can prove the probe fires before any launch.
            services => services.AddSingleton(processManager.Object));

        CsvgVideoSession client = provider.GetRequiredService<CsvgVideoSession>();

        Stopwatch stopwatch = Stopwatch.StartNew();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TestContext.Current.CancellationToken));
        stopwatch.Stop();

        // Fast fail: well under the 2s acceptance bound (and nowhere near the 2-minute timeout).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected fast failure, took {stopwatch.Elapsed}.");

        Assert.Contains($"localhost:{freePort}", ex.Message);
        Assert.Contains("AddGrpc", ex.Message);
        Assert.Contains("MapGrpcService<Cs2GameService>", ex.Message);
        Assert.Contains("Cs2VideoGenerator:GrpcPort", ex.Message);
        Assert.Contains("Using Core as a library", ex.Message);

        // ordering: probe first, then launch
        processManager.Verify(m => m.LaunchAsync(
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(CsvgSessionState.Faulted, client.State);
    }

    /// <summary>
    ///     With something listening the probe passes and the start sequence reaches the launch
    ///     phase, whether the server is bound to the IPv4 loopback or the IPv6 one. Address-family
    ///     mismatches must not produce a false failure.
    /// </summary>
    [Theory]
    [InlineData("v4")]
    [InlineData("v6")]
    public async Task StartSession_GrpcPortListening_ProceedsToLaunch(string family)
    {
        IPAddress address = family == "v4" ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        TcpListener listener = new(address, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Mock<ICs2ProcessManager> processManager = new();
            processManager
                .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("launch reached"));

            await using ServiceProvider provider = BuildConsoleHostContainer(
                new Dictionary<string, string?>
                {
                    ["Cs2VideoGenerator:MockMode"] = "true",
                    ["Cs2VideoGenerator:GrpcPort"] = port.ToString()
                },
                services => services.AddSingleton(processManager.Object));

            CsvgVideoSession client = provider.GetRequiredService<CsvgVideoSession>();

            // The probe passes; the failure we observe comes from the (stubbed) launch phase,
            // proving the endpoint check did not false-fail.
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.StartAsync(new EngineSessionOptions(), initializeCapture: false,
                    TestContext.Current.CancellationToken));

            Assert.Equal("launch reached", ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    ///     Direct probe behavior: no listener → false; caller cancellation → OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task TcpProbe_NoListener_ReturnsFalse()
    {
        TcpGrpcEndpointProbe probe = new();
        Assert.False(await probe.IsListeningAsync(GetFreePort(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("v4")]
    [InlineData("v6")]
    public async Task TcpProbe_ListenerOnEitherLoopbackFamily_ReturnsTrue(string family)
    {
        IPAddress address = family == "v4" ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        TcpListener listener = new(address, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TcpGrpcEndpointProbe probe = new();
            Assert.True(await probe.IsListeningAsync(port, TestContext.Current.CancellationToken));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    ///     A session built without a probe (e.g. tests or manual wiring) keeps the old behavior:
    ///     no endpoint verification is attempted.
    /// </summary>
    [Fact]
    public async Task StartSession_NoProbeInjected_SkipsEndpointVerification()
    {
        await using CsvgVideoSession client =
            SessionHarness.CreateVideoSession(out Mock<ICs2ProcessManager> processManager);
        processManager
            .Setup(m => m.LaunchAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("launch reached"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("launch reached", ex.Message);
    }
}
