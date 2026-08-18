#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.ProcessManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Creates <see cref="ObsCaptureSession" /> instances and answers the pre-launch question OBS can
///     be asked without starting anything: is the executable there, and can we write where we were
///     told to.
/// </summary>
public sealed class ObsCaptureSessionFactory : ICaptureSessionFactory
{
    /// <summary>
    ///     The provider name this factory registers under, and the DI key it is registered with.
    /// </summary>
    public const string ProviderKey = CaptureProviderNames.Obs;

    private readonly IOptions<ObsCaptureConfiguration> _configuration;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ObsProcessManager _processManager;

    /// <summary>
    ///     Creates the factory.
    /// </summary>
    /// <param name="configuration">The bound <c>Capture:OBS</c> configuration.</param>
    /// <param name="processManager">
    ///     The OBS process manager, shared with the sessions this factory creates. Owned by the
    ///     container: sessions come and go with CS2 runs and must not dispose it.
    /// </param>
    /// <param name="loggerFactory">
    ///     Optional logger factory used to build session loggers; null yields sessions with no-op
    ///     loggers.
    /// </param>
    public ObsCaptureSessionFactory(
        IOptions<ObsCaptureConfiguration> configuration,
        ObsProcessManager processManager,
        ILoggerFactory? loggerFactory = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string ProviderName => ProviderKey;

    /// <inheritdoc />
    /// <remarks>
    ///     OBS Studio itself runs everywhere CSVG does, so this is always true. Whether the
    ///     <c>game_capture</c> input kind that CSVG's scene relies on exists is an OBS-side question
    ///     answered when the scene is created, not a platform gate.
    /// </remarks>
    public bool IsSupportedOnCurrentPlatform => true;

    /// <inheritdoc />
    /// <remarks>
    ///     Verifies that the OBS executable can be resolved (configured path or discovery) and that the
    ///     working directory is creatable. It does not launch OBS or open a WebSocket connection - that
    ///     is <see cref="ObsCaptureSession.InitializeAsync" />'s job.
    /// </remarks>
    public Task<CaptureAvailabilityResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("ObsCaptureSessionFactory.CheckAvailability");
        ObsCaptureConfiguration config = _configuration.Value;
        activity?.SetTag("obs.host", config.Host);
        activity?.SetTag("obs.port", config.Port);
        activity?.SetTag("obs.autoStart", config.AutoStart);

        List<string> issues = new();

        try
        {
            // Resolve only - the process manager can tell us where OBS would be launched from without
            // launching it.
            string? obsPath = _processManager.ResolveExecutablePath(config);
            if (string.IsNullOrWhiteSpace(obsPath))
            {
                issues.Add(
                    "OBS Studio executable not found. Please install OBS Studio or specify the executable path.");
            }
            else if (!File.Exists(obsPath))
            {
                issues.Add($"OBS Studio executable not found at: {obsPath}");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"Error checking OBS availability: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            try
            {
                if (!Directory.Exists(config.WorkingDirectory))
                {
                    Directory.CreateDirectory(config.WorkingDirectory);
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Cannot access working directory: {ex.Message}");
            }
        }

        if (issues.Count > 0)
        {
            activity?.SetTag("obs.issues", string.Join(";", issues));
            return Task.FromResult(CaptureAvailabilityResult.Failed([.. issues]));
        }

        return Task.FromResult(CaptureAvailabilityResult.Succeeded());
    }

    /// <inheritdoc />
    public ICaptureSession Create(CaptureSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ObsCaptureSession(
            _configuration.Value,
            context,
            _processManager,
            _loggerFactory?.CreateLogger<ObsCaptureSession>());
    }
}
