#region

using Cs2VideoGenerator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Creates in-engine capture sessions whose frames come from the plugin's DXGI present hook
///     instead of the engine's frame files: same <c>startmovie</c> clock and audio, no synchronous
///     readback, several times faster (measured ~3.2x realtime at 1080p60 against the file path's
///     1.15x). Windows only, and needs a plugin build advertising
///     <see cref="CsvgCapabilities.PresentCapture" />, checked per clip, when the plugin is
///     actually connected and can answer.
/// </summary>
/// <remarks>
///     Configured by the same <c>Capture:InEngine</c> section as the file-based provider: the two
///     share staging, audio, encoder and timing settings, and differ only in where pixels come from.
/// </remarks>
public sealed class InEngineHookedCaptureSessionFactory : ICaptureSessionFactory
{
    /// <summary>
    ///     The provider name this factory registers under, and the DI key it is registered with.
    /// </summary>
    public const string ProviderKey = CaptureProviderNames.InEngineHooked;

    private readonly IOptions<InEngineCaptureConfiguration> _configuration;
    private readonly IFfmpegService _ffmpeg;
    private readonly InEngineCaptureSessionFactory _inner;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<string> _resolveDefaultAudioDirectory;
    private readonly Func<string> _resolveDefaultStagingDirectory;

    /// <summary>
    ///     Creates the factory. Parameters mirror <see cref="InEngineCaptureSessionFactory" />, whose
    ///     availability logic this factory reuses.
    /// </summary>
    public InEngineHookedCaptureSessionFactory(
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

        _inner = new InEngineCaptureSessionFactory(configuration, ffmpeg, resolveDefaultStagingDirectory,
            resolveDefaultAudioDirectory, loggerFactory);
    }

    /// <inheritdoc />
    public string ProviderName => ProviderKey;

    /// <inheritdoc />
    /// <remarks>
    ///     Windows only: the frame source is a DXGI swapchain hook, and no Vulkan analog ships yet
    ///     (the Linux design exists on paper - an implicit layer on vkQueuePresentKHR - but no code).
    /// </remarks>
    public bool IsSupportedOnCurrentPlatform => OperatingSystem.IsWindows();

    /// <inheritdoc />
    /// <remarks>
    ///     Everything the file-based provider checks applies here too (ffmpeg, configuration,
    ///     directories - the wav still lands on disk), plus the platform gate. Whether the connected
    ///     plugin can actually stream frames is only knowable per session, so that check lives at
    ///     clip start, not here.
    /// </remarks>
    public async Task<CaptureAvailabilityResult> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        CaptureAvailabilityResult inner = await _inner.CheckAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string> issues =
        [
            "The InEngineHooked provider streams raw frames from the plugin's DXGI present hook - it needs " +
            "a Windows plugin build advertising 'present-capture', which is verified when a session " +
            "connects rather than here.",
            .. inner.Issues
        ];

        bool available = inner.IsAvailable;
        if (!OperatingSystem.IsWindows())
        {
            available = false;
            issues.Add("InEngineHooked is Windows-only: its frame source is a DXGI swapchain hook.");
        }

        Dictionary<string, object> diagnostics = new(
            inner.DiagnosticInfo ?? new Dictionary<string, object>(StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            ["frameSource"] = InEngineFrameSource.PresentHook.ToString(),
            ["framePipeName"] = _configuration.Value.FramePipeName
        };

        return new CaptureAvailabilityResult
        {
            IsAvailable = available,
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
            _loggerFactory?.CreateLogger<InEngineCaptureSession>(),
            InEngineFrameSource.PresentHook,
            ProviderKey);
    }
}
