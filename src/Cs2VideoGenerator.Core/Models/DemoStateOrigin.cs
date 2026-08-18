namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Attribution for a <see cref="DemoState" /> change: whether the plugin observed the change as
///     the result of a host-issued command or as a user action inside the game client.
/// </summary>
public enum DemoStateOrigin
{
    /// <summary>
    ///     The origin could not be determined (pre-upgrade plugin build or indeterminate timing).
    /// </summary>
    Unknown,

    /// <summary>
    ///     The change matches a recently executed host command (echo of something this client sent).
    /// </summary>
    HostCommand,

    /// <summary>
    ///     The change was observed in the engine with no matching host command, so a user acted
    ///     inside the game client (in-game pause, seek, demo change, ...).
    /// </summary>
    User
}
