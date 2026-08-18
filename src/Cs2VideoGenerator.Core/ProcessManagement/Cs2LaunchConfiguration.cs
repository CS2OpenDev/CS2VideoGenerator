namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Configuration for launching a real CS2 process. Mock mode uses MockLaunchConfiguration.
/// </summary>
public record Cs2LaunchConfiguration
{
    /// <summary>
    ///     The width of the game window in pixels. If null, uses CS2's default.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     The height of the game window in pixels. If null, uses CS2's default.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    ///     Whether to launch in fullscreen mode. If false, launches in windowed mode.
    /// </summary>
    public bool Fullscreen { get; init; }
}
