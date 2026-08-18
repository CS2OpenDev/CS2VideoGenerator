#region

using Cs2VideoGenerator.Core.Models;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Everything a backend needs to know about one clip, plus the fault veto it uses to bail out.
///     Single-use: exactly one <see cref="ClipCaptureCoordinator.CaptureClipAsync" /> call consumes a
///     context, and that call disposes the fault token when it returns.
/// </summary>
public sealed class ClipCaptureContext
{
    private readonly ClipFaultToken _faultToken = new();

    /// <summary>
    ///     First demo tick of the clip. Recording starts here.
    /// </summary>
    public required int StartTick { get; init; }

    /// <summary>
    ///     Last demo tick of the clip. Playback stops (and the engine auto-pauses) here.
    /// </summary>
    public required int EndTick { get; init; }

    /// <summary>
    ///     What the output should look like and where it goes.
    /// </summary>
    public required CaptureMetadata Metadata { get; init; }

    /// <summary>
    ///     Zero-based index of this clip within the run, used for output naming and diagnostics.
    /// </summary>
    public int ClipIndex { get; init; }

    /// <summary>
    ///     The player the clip spectates, when the caller knows it. Non-null enables the spectator
    ///     re-assert scheduled just before the recording tick, which works around the CSDM issue
    ///     where the spectator target is lost during a seek.
    /// </summary>
    public string? SpectatorTarget { get; init; }

    /// <summary>
    ///     Demo playback options (xray, hud, ...) already applied for this clip, for backends that
    ///     need to know about them. Purely informational; the coordinator does not apply them.
    /// </summary>
    public Cs2ClipOptions? ClipOptions { get; init; }

    /// <summary>
    ///     Explicit playback timeout, or null to use the tick-range estimate.
    /// </summary>
    public TimeSpan? PlaybackTimeout { get; init; }

    /// <summary>
    ///     The backend's fault veto: cancels the coordinator's playback wait so a clip whose backend
    ///     has died fails in seconds instead of burning the full playback timeout. The first fault
    ///     wins and becomes the failed <see cref="CaptureResult" />'s error message.
    /// </summary>
    public void FailClip(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        _faultToken.Fail(fault);
    }

    /// <summary>
    ///     The fault veto's cancellation side, linked into the playback wait by the coordinator.
    /// </summary>
    internal ClipFaultToken FaultToken => _faultToken;
}

/// <summary>
///     The cancellation half of <see cref="ClipCaptureContext.FailClip" />: remembers the first fault
///     and cancels so a linked playback wait unblocks immediately.
/// </summary>
internal sealed class ClipFaultToken : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Exception? _fault;
    private int _disposed;

    /// <summary>
    ///     Cancelled as soon as a fault is recorded.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    ///     The fault that vetoed the clip, or null when the backend never complained.
    /// </summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    ///     Records the fault (first one wins) and cancels. Safe to call after disposal, so a
    ///     backend racing the coordinator's return never throws from its own failure path.
    /// </summary>
    public void Fail(Exception fault)
    {
        Interlocked.CompareExchange(ref _fault, fault, null);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; the fault is recorded either way and there is nothing left to wake.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Dispose();
    }
}
