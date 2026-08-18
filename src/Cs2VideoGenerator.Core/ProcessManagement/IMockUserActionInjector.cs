namespace Cs2VideoGenerator.Core.ProcessManagement;

/// <summary>
///     Sends user-action injection commands to a running mock_server over its stdin
///     injection channel (see docs/mock-cs2-mode.md, "User-Action Injection Channel").
///     Injected actions drive the mock engine directly — bypassing the gRPC command
///     path — so the plugin client observes them exactly like real in-game user
///     actions (DemoStateEvents attributed ORIGIN_USER, no command echoes).
/// </summary>
/// <remarks>
///     Resolvable from DI after <c>AddCs2VideoGeneratorCore</c>. In mock mode the
///     instance is backed by the mock process manager; in real-CS2 mode it is a
///     no-op stub whose <see cref="IsAvailable" /> is always false (a real game has
///     no injection channel). Check <see cref="IsAvailable" /> before injecting.
/// </remarks>
public interface IMockUserActionInjector
{
    /// <summary>
    ///     Whether an injection channel is currently available: mock mode is active
    ///     and the mock_server process is running.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     Sends one injection command line to the mock_server (see
    ///     <see cref="MockUserActions" /> for the grammar).
    /// </summary>
    /// <param name="command">A single command line, without trailing newline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The command is null/empty or contains a newline.</exception>
    /// <exception cref="InvalidOperationException">
    ///     No injection channel is available (<see cref="IsAvailable" /> is false).
    /// </exception>
    Task InjectAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>
///     The mock_server user-action injection grammar (one command per line), as
///     implemented by the v1.1 mock. Use these helpers instead of raw strings.
/// </summary>
public static class MockUserActions
{
    /// <summary>The user pauses the demo (tick advancement stops; no command echo).</summary>
    public const string Pause = "user-pause";

    /// <summary>The user resumes the demo.</summary>
    public const string Resume = "user-resume";

    /// <summary>The user stops the demo (playing state flips off).</summary>
    public const string Stop = "user-stop";

    /// <summary>The demo ends naturally (same effect as reaching CSVG_MOCK_DEMO_TICKS).</summary>
    public const string EndDemo = "end-demo";

    /// <summary>The user seeks to a demo tick (demo_gototick as the user).</summary>
    public static string Seek(int tick) => $"user-seek {tick}";

    /// <summary>
    ///     The user plays a demo from the console. The path need not exist: console use bypasses the
    ///     host client's exists-check by design.
    /// </summary>
    public static string PlayDemo(string demoPath) => $"user-playdemo {demoPath}";

    /// <summary>The user changes demo_timescale in the console.</summary>
    public static string Timescale(float timescale) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"user-timescale {timescale:0.###}");

    /// <summary>
    ///     A synthetic client-side game event, offered to the plugin client's telemetry pipeline
    ///     exactly as the real plugin's <c>FireEventClientSide</c> hook does. This is the mock's
    ///     stand-in for that hook. It reaches the host as <c>CsvgVideoSession.GameEventReceived</c> only
    ///     while a subscription is active (see
    ///     <see cref="Core.Engine.ICs2EngineSession.SetGameEventSubscriptionAsync" />).
    /// </summary>
    /// <param name="eventName">The event name, e.g. <c>player_death</c>. Must not contain spaces.</param>
    /// <param name="jsonPayload">
    ///     The JSON payload, passed through unvalidated (the real hook forwards whatever the game's
    ///     serializer produced). Must be a single line; defaults to <c>{}</c>.
    /// </param>
    public static string GameEvent(string eventName, string jsonPayload = "{}") =>
        $"game-event {eventName} {jsonPayload}";
}

/// <summary>
///     No-op injector used when mock mode is not active: a real CS2 instance has no
///     injection channel. <see cref="IsAvailable" /> is always false and
///     <see cref="InjectAsync" /> always throws.
/// </summary>
internal sealed class NoOpMockUserActionInjector : IMockUserActionInjector
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task InjectAsync(string command, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "No mock user-action injection channel is available: mock mode is not active. " +
            "Enable it via Cs2VideoGeneratorOptions.MockMode (or ExternalMockServerPath) — " +
            "a real CS2 instance has no injection channel.");
}
