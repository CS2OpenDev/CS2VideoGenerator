namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     A running frame source for one clip: something that moves the engine's frames into the
///     encoder's stdin and reports how it went. Two implementations exist:
///     <see cref="FrameSequenceConsumer" /> streams the numbered files <c>startmovie</c> writes,
///     <see cref="PresentHookFrameConsumer" /> receives raw frames from the plugin's present hook
///     over a named pipe. <see cref="InEngineClipCapture" /> drives either through this
///     surface without caring which.
/// </summary>
internal interface IFrameStreamConsumer : IAsyncDisposable
{
    /// <summary>
    ///     Frames written to the encoder so far.
    /// </summary>
    long FramesConsumed { get; }

    /// <summary>
    ///     When the first frame was piped, or null while none has been - the honest start of the
    ///     render clock (everything before it is seek and arm preamble).
    /// </summary>
    DateTime? FirstFrameConsumedUtc { get; }

    /// <summary>
    ///     Bytes written to the encoder so far.
    /// </summary>
    long BytesConsumed { get; }

    /// <summary>
    ///     Frames known to exist but not yet piped. File-based consumption reports its staging
    ///     backlog; a pipe has no visible backlog and reports 0.
    /// </summary>
    int Backlog { get; }

    /// <summary>
    ///     Frames lost from the sequence, as far as this consumer can tell.
    /// </summary>
    long DroppedFrames { get; }

    /// <summary>
    ///     Completes when the stream has fully ended, was cancelled, or failed. Never faults:
    ///     failures are reported on the result.
    /// </summary>
    Task<FrameSequenceConsumeResult> Completion { get; }

    /// <summary>
    ///     Starts consuming. Idempotent one-shot.
    /// </summary>
    void Start(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Signals that recording is over and the tail should be drained, and returns
    ///     <see cref="Completion" />.
    /// </summary>
    Task<FrameSequenceConsumeResult> StopAsync();
}
