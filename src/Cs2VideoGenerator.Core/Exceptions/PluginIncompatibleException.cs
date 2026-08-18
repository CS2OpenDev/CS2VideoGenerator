namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     Thrown when the plugin/game version pair reported during the session handshake is on the
///     known-broken compatibility list and <see cref="Cs2VideoGeneratorOptions.ForceIncompatiblePlugin" />
///     (<c>--force</c>) is not set. Deliberately the only blocking outcome of the compatibility check;
///     newer or unknown CS2 versions merely produce a warning.
/// </summary>
public class PluginIncompatibleException(string message) : Exception(message);
