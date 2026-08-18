#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;

#endregion

namespace Cs2VideoGenerator.Core;

/// <summary>
///     The collaborators one <see cref="Cs2CompilationRunner.RunAsync" /> call works against. The
///     runner keeps no state between runs, so the engine session, the capture session and the
///     progress callbacks all arrive here rather than being held by the runner.
/// </summary>
public sealed class Cs2CompilationRunContext
{
    /// <summary>
    ///     The engine session that loads demos, applies per-clip options and drives playback.
    /// </summary>
    public required ICs2EngineSession Engine { get; init; }

    /// <summary>
    ///     The initialized capture session for <see cref="Engine" />, or null when capture is not up.
    ///     A null session is how "capture is not initialized" reaches the runner: it reports that as an
    ///     <see cref="InvalidOperationException" />, but only after the compilation itself validates.
    /// </summary>
    public ICaptureSession? Capture { get; init; }

    /// <summary>
    ///     Invoked and awaited before each clip is captured, with the clip's original index in the
    ///     compilation. The caller owns subscriber dispatch, and therefore subscriber exception
    ///     isolation; an exception thrown here fails the run like any other.
    /// </summary>
    public Func<int, Cs2CompilationClip, Task>? ClipStarted { get; init; }

    /// <summary>
    ///     Invoked and awaited after each clip completes, successfully or not, with the clip's
    ///     original index in the compilation. On a failed clip this runs before the run's failed
    ///     result is returned, so a caller sees the failure through this callback first.
    /// </summary>
    public Func<int, Cs2ClipResult, Task>? ClipCompleted { get; init; }
}
