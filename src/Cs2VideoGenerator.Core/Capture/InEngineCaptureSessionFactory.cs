#region

using System.Diagnostics;
using Cs2VideoGenerator.Core.Diagnostics;
using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Creates <see cref="InEngineCaptureSession" /> instances and answers what can be asked about
///     offline capture before CS2 exists: is ffmpeg there, is the configuration coherent, and can the
///     staging directory be written.
/// </summary>
public sealed class InEngineCaptureSessionFactory : ICaptureSessionFactory
{
    /// <summary>
    ///     The provider name this factory registers under, and the DI key it is registered with.
    /// </summary>
    public const string ProviderKey = CaptureProviderNames.InEngine;

    /// <summary>
    ///     The banner every availability check leads with. The provider's frame pipeline has now been
    ///     observed working against a real CS2 (build 2000879, 2026-07-30): startmovie recorded a
    ///     tick-exact jpg sequence into its timestamped movie directory and the consumer streamed it.
    ///     The parts that run did not reach are still assumptions, and the notice says which.
    /// </summary>
    public const string UnverifiedNotice =
        "The InEngine capture provider has been validated against a real CS2 build (2000879): startmovie's " +
        "location, naming, jpg mode, jpeg_quality, tick-exact frame pacing and game-time wav audio " +
        "(written under csgo/movie/<timestamp>/) are observed facts. Targa mode and the cvar restore " +
        "values below are still assumptions.";

    private readonly IOptions<InEngineCaptureConfiguration> _configuration;
    private readonly IFfmpegService _ffmpeg;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<string> _resolveDefaultAudioDirectory;
    private readonly Func<string> _resolveDefaultStagingDirectory;

    /// <summary>
    ///     Creates the factory.
    /// </summary>
    /// <param name="configuration">The bound <c>Capture:InEngine</c> configuration.</param>
    /// <param name="ffmpeg">The ffmpeg service the sessions encode and mux with.</param>
    /// <param name="resolveDefaultStagingDirectory">
    ///     Supplies the default staging directory. Deliberately a function: working it out needs the
    ///     CS2 installation, which throws when there is none, and registering a capture provider must
    ///     not require a game to be installed.
    /// </param>
    /// <param name="resolveDefaultAudioDirectory">Supplies the default movie-audio directory, likewise.</param>
    /// <param name="loggerFactory">Optional logger factory used to build session loggers.</param>
    public InEngineCaptureSessionFactory(
        IOptions<InEngineCaptureConfiguration> configuration,
        IFfmpegService ffmpeg,
        Func<string> resolveDefaultStagingDirectory,
        Func<string> resolveDefaultAudioDirectory,
        ILoggerFactory? loggerFactory = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _resolveDefaultStagingDirectory = resolveDefaultStagingDirectory
                                          ?? throw new ArgumentNullException(nameof(resolveDefaultStagingDirectory));
        _resolveDefaultAudioDirectory = resolveDefaultAudioDirectory
                                        ?? throw new ArgumentNullException(nameof(resolveDefaultAudioDirectory));
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string ProviderName => ProviderKey;

    /// <inheritdoc />
    /// <remarks>
    ///     Always true. Nothing here is platform-specific: the backend is CS2's own console plus
    ///     ffmpeg, both of which exist wherever CSVG runs. Whether CS2 is installed is an availability
    ///     question, answered by <see cref="CheckAvailabilityAsync" />, not a platform gate. Gating
    ///     the platform would make the provider unresolvable rather than unavailable, which is a much
    ///     blunter failure.
    /// </remarks>
    public bool IsSupportedOnCurrentPlatform => true;

    /// <inheritdoc />
    /// <remarks>
    ///     Reports the unverified status first, then checks the configuration, ffmpeg, and the staging
    ///     directory. Launches nothing and needs no engine.
    /// </remarks>
    public async Task<CaptureAvailabilityResult> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = CsvgTracing.StartActivity("InEngineCaptureSessionFactory.CheckAvailability");
        InEngineCaptureConfiguration config = _configuration.Value;
        activity?.SetTag("csvg.inengine.frameFormat", config.FrameFormat.ToString());

        // The unverified notice and the open questions ride in Issues even on the available path:
        // that list is the only channel a caller renders.
        List<string> issues = [UnverifiedNotice, ..UnverifiedFacts];
        IReadOnlyList<string> configurationIssues = config.Validate();
        issues.AddRange(configurationIssues);
        bool blocked = configurationIssues.Count > 0;

        Dictionary<string, object> diagnostics = new(StringComparer.Ordinal)
        {
            ["verifiedAgainstCs2"] = false,
            ["frameFormat"] = config.FrameFormat.ToString(),
            ["intermediateEncoder"] = config.IntermediateEncoder.ToString(),
            ["missingAudioBehavior"] = config.MissingAudioBehavior.ToString()
        };

        FfmpegAvailabilityResult ffmpeg = await _ffmpeg.CheckAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        diagnostics["ffmpegAvailable"] = ffmpeg.IsAvailable;
        if (ffmpeg.IsAvailable)
        {
            diagnostics["ffmpegVersion"] = ffmpeg.Version ?? "unknown";
        }
        else
        {
            blocked = true;
            issues.Add(
                "ffmpeg is required by the InEngine provider - it is what turns the engine's frame dump into a " +
                $"video, and there is no fallback: {ffmpeg.ErrorMessage}");
        }

        string? staging = TryResolve(config.StagingDirectory, _resolveDefaultStagingDirectory, out string? stagingError);
        if (staging is null)
        {
            blocked = true;
            issues.Add(
                "Could not work out where the engine's frames would be staged " +
                $"({stagingError}). Set \"Capture:InEngine:StagingDirectory\".");
        }
        else
        {
            diagnostics["stagingDirectory"] = staging;

            try
            {
                Directory.CreateDirectory(staging);
            }
            catch (Exception ex)
            {
                blocked = true;
                issues.Add($"The staging directory '{staging}' cannot be created: {ex.Message}");
            }
        }

        string? audio = TryResolve(config.AudioDirectory, _resolveDefaultAudioDirectory, out string? audioError);
        if (audio is null)
        {
            issues.Add(
                "Could not work out where the engine's movie audio would land " +
                $"({audioError}). Set \"Capture:InEngine:AudioDirectory\" if clips need audio.");
        }
        else
        {
            diagnostics["audioDirectory"] = audio;
        }

        activity?.SetTag("csvg.inengine.available", !blocked);

        return new CaptureAvailabilityResult
        {
            IsAvailable = !blocked,
            Issues = issues,
            DiagnosticInfo = diagnostics
        };
    }

    /// <inheritdoc />
    public ICaptureSession Create(CaptureSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new InEngineCaptureSession(
            _configuration.Value,
            context,
            _ffmpeg,
            _resolveDefaultStagingDirectory,
            _resolveDefaultAudioDirectory,
            _loggerFactory?.CreateLogger<InEngineCaptureSession>());
    }

    /// <summary>
    ///     The specific things about CS2's <c>startmovie</c> that have not been observed, listed so a
    ///     doctor run says which questions are open rather than only that questions exist. Each has a
    ///     matching setting under <c>Capture:InEngine</c> or <c>Capture:InEngine:EngineOutput</c>;
    ///     the fuller story is in docs/in-engine-capture.md.
    /// </summary>
    public static IReadOnlyList<string> UnverifiedFacts { get; } =
    [
        "Unverified: the engine's own engine_no_focus_sleep default, which is what gets restored.",
        "Unverified: that the tick-96 command clamp still applies to current CS2 builds."
    ];

    private static string? TryResolve(string? configured, Func<string> fallback, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        try
        {
            return Path.GetFullPath(fallback());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
