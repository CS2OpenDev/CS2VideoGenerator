namespace Cs2VideoGenerator.Core.Engine;

/// <summary>
///     Creates and tracks <see cref="ICs2EngineSession" /> instances. One engine session exists
///     per CS2 run; the host is the singleton entry point that hands them out.
/// </summary>
public interface ICs2EngineHost
{
    /// <summary>
    ///     The most recently created engine session, or null if none has been created yet.
    /// </summary>
    ICs2EngineSession? Current { get; }

    /// <summary>
    ///     Creates a new engine session with the given per-session options. The returned session
    ///     becomes <see cref="Current" />.
    /// </summary>
    ICs2EngineSession CreateSession(EngineSessionOptions options);
}
