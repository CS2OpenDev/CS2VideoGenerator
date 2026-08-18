#region

using System.Diagnostics;
using System.Globalization;
using Cs2VideoGenerator.Cli.Options;
using Cs2VideoGenerator.Cli.Tasks;
using Cs2VideoGenerator.Cli.Utils;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Exceptions;
using Cs2VideoGenerator.Core.Grpc;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

#endregion

namespace Cs2VideoGenerator.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;

        // CommandLineParser is the single source of truth for argv, and it writes the
        // help/usage text itself.
        CliParseResult parseResult = CliArgumentParser.Parse(args, Console.Error);
        if (parseResult.Task is null)
        {
            return parseResult.ExitCode;
        }

        // Only session-launching verbs (generate, watch) host the gRPC endpoint the CS2
        // plugin connects to. Other verbs (doctor, backup, restore, cleanup) never use gRPC
        // and must not bind any port, so they run on a plain generic host.
        bool hostGrpc = RequiresGrpcHost(parseResult.Task);

        // Content root is the directory the binary runs from, not the current working directory.
        // csvg may be installed as a dotnet tool or shipped as a self-contained binary and run
        // from any directory; the bundled appsettings.json ships next to the binary and must be
        // found regardless of the caller's CWD.
        IHostApplicationBuilder builder = hostGrpc
            ? WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory })
            : Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory
            });

        // An appsettings.json in the current working directory (when it isn't the binary's own
        // directory) overrides the bundled defaults, so users can keep per-project configuration
        // without editing the installed tool.
        string bundledAppSettings = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        string cwdAppSettings = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));
        if (!string.Equals(cwdAppSettings, bundledAppSettings, StringComparison.OrdinalIgnoreCase))
        {
            builder.Configuration.AddJsonFile(cwdAppSettings, optional: true, reloadOnChange: false);
        }

        // Must run before AddCs2VideoGeneratorCore(): --mock and friends decide which services get
        // registered, so they have to be in configuration by then. The values come from
        // CommandLineParser rather than AddCommandLine, so a bare `--mock` behaves like `--mock=true`.
        ((IConfigurationBuilder)builder.Configuration).Add(
            new CommandLineOverridesConfigurationSource(
                CommandLineConfigurationOverrides.Build(parseResult.Task, args)));

        // Telemetry is inert unless a "Telemetry" config section enables it.
        bool telemetryEnabled = builder.AddSharedTelemetry();
        builder.AddCs2VideoGeneratorCore();

        builder.Services.AddSingleton<ExitCodeHolder>();

        parseResult.Task.RegisterTaskAndOptions(builder);

        int grpcPort = CommonOptions.DefaultGrpcPort;
        IHost app;

        if (builder is WebApplicationBuilder webBuilder)
        {
            // --grpc-port wins over the config key over the default.
            grpcPort = webBuilder.Configuration.GetValue(
                CommonOptions.GrpcPortConfigurationPath, CommonOptions.DefaultGrpcPort);
            int port = grpcPort;

            webBuilder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http2);
            });
            webBuilder.Services.AddGrpc();

            WebApplication webApp = webBuilder.Build();
            webApp.MapGrpcService<Cs2GameService>();
            app = webApp;
        }
        else
        {
            app = ((HostApplicationBuilder)builder).Build();
        }

        try
        {
            return await RunHostAsync(app, hostGrpc, grpcPort, telemetryEnabled);
        }
        finally
        {
            if (app is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                app.Dispose();
            }
        }
    }

    /// <summary>
    ///     Whether the given verb's task needs the gRPC server hosted. Only verbs that launch
    ///     CS2/mock sessions (generate, watch — options deriving from <see cref="CommonOptions" />)
    ///     do; all other verbs must not bind any port.
    /// </summary>
    public static bool RequiresGrpcHost(IRegisterTaskAndOptions task) => task is CommonOptions;

    /// <summary>
    ///     Starts the host, waits for the task to request shutdown, and tears down,
    ///     translating known failures into friendly messages and an exit code.
    /// </summary>
    private static async Task<int> RunHostAsync(IHost app, bool hostGrpc, int grpcPort, bool telemetryEnabled)
    {
        ActivitySource activitySource = app.Services.GetRequiredService<ActivitySource>();
        using Activity? mainActivity = activitySource.StartActivity(nameof(Program));

        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        ExitCodeHolder exitCodeHolder = app.Services.GetRequiredService<ExitCodeHolder>();

        bool started = false;
        int? startupFailureExitCode = null;

        try
        {
            await app.StartAsync();
            started = true;

            if (hostGrpc)
            {
                Console.WriteLine($"gRPC server started on port {grpcPort}.");
                if (grpcPort != CommonOptions.DefaultGrpcPort)
                {
                    Console.Error.WriteLine(
                        $"Warning: the bundled CS2 plugin and mock server connect to port {CommonOptions.DefaultGrpcPort}; " +
                        $"sessions on port {grpcPort} require a plugin build targeting that port.");
                }
            }

            TaskCompletionSource shutdownTcs = new();
            lifetime.ApplicationStopping.Register(() => shutdownTcs.TrySetResult());

            await shutdownTcs.Task;
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested &&
                                                 exitCodeHolder.IsSet)
        {
            // A fast task completed (and requested shutdown) while the host was still starting up,
            // cancelling the remainder of startup. Not a failure — the task's exit code applies.
        }
        catch (Exception ex)
        {
            // Known environment failures get a single actionable line instead of a stack trace.
            string? friendlyMessage = GetFriendlyStartupError(ex, grpcPort);
            if (friendlyMessage is not null)
            {
                Console.Error.WriteLine($"Error: {friendlyMessage}");
            }
            else
            {
                Console.Error.WriteLine(ex);
            }

            // Recorded separately so a task racing to a successful completion cannot overwrite it.
            startupFailureExitCode = ExitCodes.OperationFailure;
        }
        finally
        {
            mainActivity?.Stop();

            if (telemetryEnabled)
            {
                Console.WriteLine("Flushing telemetry...");
                TracerProvider? tracerProvider = app.Services.GetService<TracerProvider>();
                MeterProvider? meterProvider = app.Services.GetService<MeterProvider>();
                LoggerProvider? loggerProvider = app.Services.GetService<LoggerProvider>();

                tracerProvider?.ForceFlush();

                meterProvider?.ForceFlush();

                loggerProvider?.ForceFlush();

                // give the exports a moment to complete
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            try
            {
                await app.StopAsync();
                if (started && hostGrpc)
                {
                    Console.WriteLine("gRPC server stopped.");
                }
            }
            catch (Exception stopEx) when (!started)
            {
                // Stopping a host whose startup failed can fault on its own (double-fault);
                // the startup error has already been reported, so swallow teardown noise.
                _ = stopEx;
            }
            catch (Exception stopEx)
            {
                Console.Error.WriteLine($"Warning: error while stopping host: {stopEx.Message}");
                startupFailureExitCode ??= ExitCodes.OperationFailure;
            }
        }

        return startupFailureExitCode ?? exitCodeHolder.ExitCode;
    }

    /// <summary>Null for anything not recognized, which the caller prints as a stack trace.</summary>
    private static string? GetFriendlyStartupError(Exception exception, int grpcPort)
    {
        foreach (Exception ex in Flatten(exception))
        {
            if (ex is CounterStrikeDirectoryNotFoundException)
            {
                return "CS2 installation not found — run `csvg doctor` to diagnose, " +
                       "or set \"Cs2VideoGenerator:Cs2RootInstallationDirectory\" in appsettings.json. " +
                       "For testing without CS2, use --mock.";
            }

            if (ex is AddressInUseException ||
                (ex is IOException && ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase) &&
                 ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)))
            {
                return $"Port {grpcPort} is already in use — is another csvg instance running? " +
                       "Use --grpc-port to select a different port.";
            }
        }

        return null;
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        Queue<Exception> queue = new();
        queue.Enqueue(exception);

        while (queue.Count > 0)
        {
            Exception current = queue.Dequeue();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    queue.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                queue.Enqueue(current.InnerException);
            }
        }
    }
}
