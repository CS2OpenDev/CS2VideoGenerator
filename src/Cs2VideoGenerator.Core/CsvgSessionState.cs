namespace Cs2VideoGenerator.Core;

/// <summary>
///     Represents the state of a <see cref="CsvgVideoSession" />.
/// </summary>
public enum CsvgSessionState
{
    /// <summary>
    ///     No active session. StartAsync can be called.
    /// </summary>
    Disconnected,

    /// <summary>
    ///     StartAsync is in progress. CS2 is launching and waiting for plugin connection.
    /// </summary>
    Connecting,

    /// <summary>
    ///     Session is active and ready to send commands.
    /// </summary>
    Connected,

    /// <summary>
    ///     StopAsync is in progress. Graceful shutdown underway.
    /// </summary>
    Disconnecting,

    /// <summary>
    ///     An unexpected error or disconnect occurred. The CS2 process may still be running and
    ///     configuration backups may not have been restored. Call <c>StopAsync()</c> to
    ///     recover: it stops any remaining CS2 process, restores backups, and transitions back to
    ///     <see cref="Disconnected" />, after which a new session can be started.
    /// </summary>
    Faulted
}
