#region

using System.Globalization;
using Cs2VideoGenerator.Core.Capture;

#endregion

namespace Cs2VideoGenerator.Cli.Utils;

/// <summary>
///     Builds the default output file name for an ad-hoc <c>watch</c> recording: the demo and tick
///     range first (what was captured), then the capture configuration that produced it (how), then
///     a timestamp (which run). The point is that a directory of these reads as a record of
///     experiments - two runs with different settings can never collide or be confused, which is
///     exactly what a filename like <c>demo_49000-51000.mp4</c> could not promise.
/// </summary>
/// <example>
///     <c>match730_..._406_49000-51000.InEngineHooked.1920x1080.60fps.RawRgba.q16.20260731-004512</c>
/// </example>
public static class CaptureOutputNames
{
    /// <summary>
    ///     The descriptive name, without extension. <paramref name="inEngine" /> supplies the
    ///     provider-specific parts for the in-engine family and is ignored for other providers.
    /// </summary>
    public static string Describe(
        string demoPath,
        int startTick,
        int endTick,
        string providerName,
        int width,
        int height,
        int frameRate,
        InEngineCaptureConfiguration? inEngine,
        DateTime localTimestamp)
    {
        List<string> parts =
        [
            FormattableString.Invariant(
                $"{Path.GetFileNameWithoutExtension(demoPath)}_{startTick}-{endTick}"),
            providerName,
            FormattableString.Invariant($"{width}x{height}"),
            FormattableString.Invariant($"{frameRate}fps")
        ];

        bool isHooked = string.Equals(providerName, CaptureProviderNames.InEngineHooked,
            StringComparison.OrdinalIgnoreCase);
        bool isInEngineFamily = isHooked || string.Equals(providerName, CaptureProviderNames.InEngine,
            StringComparison.OrdinalIgnoreCase);

        if (isInEngineFamily && inEngine is not null)
        {
            // The frame source: raw hooked frames, or the startmovie dump format (with the engine's
            // jpeg quality when that is the lossy step).
            parts.Add(isHooked
                ? "RawRgba"
                : inEngine.FrameFormat == InEngineFrameFormat.Jpg && inEngine.JpegQuality is { } jpegQuality
                    ? FormattableString.Invariant($"Jpg{jpegQuality}")
                    : inEngine.FrameFormat.ToString());

            if (inEngine.IntermediateQuality is { } quality)
            {
                parts.Add(FormattableString.Invariant($"q{quality}"));
            }

            if (!string.IsNullOrWhiteSpace(inEngine.IntermediateEncoderPreset))
            {
                parts.Add(inEngine.IntermediateEncoderPreset);
            }
        }

        parts.Add(localTimestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        return string.Join('.', parts.Select(Sanitize));
    }

    /// <summary>
    ///     The printable characters a name part may never contain. Fixed rather than taken from
    ///     <see cref="Path.GetInvalidFileNameChars" />, because that set is per-platform: on Unix it
    ///     is only NUL and '/', so a name sanitized there would still carry ':' or '|' onto the
    ///     Windows share or SMB mount it gets copied to. This is the Windows set, a superset of both.
    /// </summary>
    private static readonly char[] InvalidNameChars = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    /// <summary>
    ///     Keeps every part filesystem-safe without inventing an escaping scheme: anything a
    ///     filesystem would reject becomes '-'. Parts come from paths and configuration, so in
    ///     practice this only ever touches a hostile preset string.
    /// </summary>
    private static string Sanitize(string part)
    {
        return string.Create(part.Length, part, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = source[i] < ' ' || Array.IndexOf(InvalidNameChars, source[i]) >= 0
                    ? '-'
                    : source[i];
            }
        });
    }
}
