namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     The capture providers CSVG ships: the name each one is selected by, the DI key its
///     <see cref="ICaptureSessionFactory" /> is registered under, and the configuration subsection it
///     binds (<c>Capture:&lt;name&gt;</c>).
/// </summary>
/// <remarks>
///     A new provider joins <see cref="All" /> in the same change that registers its factory, so the
///     option validation, the DI lookup and the diagnostics all learn about it at once.
/// </remarks>
public static class CaptureProviderNames
{
    /// <summary>
    ///     The OBS Studio provider: records the screen in real time through obs-websocket.
    /// </summary>
    public const string Obs = "OBS";

    /// <summary>
    ///     The in-engine provider: renders offline through CS2's own <c>startmovie</c> at a fixed
    ///     timestep, and encodes the resulting frame dump with ffmpeg. The default provider,
    ///     validated against a real CS2 build (2000879).
    /// </summary>
    public const string InEngine = "InEngine";

    /// <summary>
    ///     The hooked in-engine provider: the same offline <c>startmovie</c> timing and audio as
    ///     <see cref="InEngine" />, but frames are captured from the swapchain by the CSVG plugin's
    ///     present hook and streamed to ffmpeg as raw RGBA - no synchronous engine readback and no
    ///     frame files, which is what makes it several times faster. Windows only, and needs a plugin
    ///     build advertising <see cref="CsvgCapabilities.PresentCapture" />. Configured by the same
    ///     <c>Capture:InEngine</c> section as <see cref="InEngine" />.
    /// </summary>
    public const string InEngineHooked = "InEngineHooked";

    /// <summary>
    ///     Root configuration section holding one subsection per provider (<c>Capture:OBS</c>).
    /// </summary>
    public const string ConfigurationSectionName = "Capture";

    /// <summary>
    ///     Every provider name that ships, in the order diagnostics list them.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Obs, InEngine, InEngineHooked];

    /// <summary>
    ///     The known provider names as a display string, for messages that have to list the valid
    ///     values.
    /// </summary>
    public static string Format() => string.Join(", ", All);

    /// <summary>
    ///     Resolves a configured provider name to its canonical spelling, ignoring case. Returns false
    ///     when no provider answers to that name.
    /// </summary>
    public static bool TryResolve(string? name, out string canonicalName)
    {
        foreach (string known in All)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                canonicalName = known;
                return true;
            }
        }

        canonicalName = string.Empty;
        return false;
    }

    /// <summary>
    ///     Whether a provider answers to the given name, ignoring case.
    /// </summary>
    public static bool IsKnown(string? name) => TryResolve(name, out _);
}
