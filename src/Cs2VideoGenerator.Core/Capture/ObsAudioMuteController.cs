#region

using Microsoft.Extensions.Logging;
using Obs.NET;
using Obs.NET.Requests;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     Minimal abstraction over the obs-websocket v5 audio-input calls
///     (GetInputList / GetSpecialInputs / GetInputMute / SetInputMute) used to disable audio
///     capture, so the mute/restore logic is unit-testable without a live OBS connection.
/// </summary>
internal interface IObsAudioClient
{
    /// <summary>
    ///     Returns all inputs known to OBS as (name, unversioned input kind) pairs.
    /// </summary>
    Task<IReadOnlyList<ObsAudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the names of the configured special (global) audio inputs:
    ///     desktop audio 1/2 and mic/aux 1-4. Unconfigured slots are omitted.
    /// </summary>
    Task<IReadOnlyList<string>> GetSpecialInputNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the current mute state of the input, or null when it could not be determined.
    /// </summary>
    Task<bool?> GetInputMuteAsync(string inputName, CancellationToken cancellationToken);

    /// <summary>
    ///     Sets the mute state of the input.
    /// </summary>
    Task SetInputMuteAsync(string inputName, bool muted, CancellationToken cancellationToken);
}

/// <summary>
///     An OBS input as seen by the audio-mute logic.
/// </summary>
/// <param name="Name">The input name.</param>
/// <param name="Kind">The unversioned input kind (e.g. "wasapi_output_capture", "game_capture").</param>
internal readonly record struct ObsAudioInputInfo(string Name, string Kind);

/// <summary>
///     <see cref="IObsAudioClient" /> adapter over the real <see cref="ObsWsClient" />.
/// </summary>
internal sealed class ObsWsAudioClient(ObsWsClient client) : IObsAudioClient
{
    public async Task<IReadOnlyList<ObsAudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken)
    {
        GetInputListResponse? response = await client
            .GetInputListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return response?.Inputs?
            .Where(input => !string.IsNullOrEmpty(input.InputName))
            .Select(input => new ObsAudioInputInfo(
                input.InputName,
                string.IsNullOrEmpty(input.UnversionedInputKind) ? input.InputKind : input.UnversionedInputKind))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<string>> GetSpecialInputNamesAsync(CancellationToken cancellationToken)
    {
        GetSpecialInputsResponse? response = await client
            .GetSpecialInputsAsync(cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            return [];
        }

        // Unconfigured special-input slots come back null/empty from obs-websocket.
        return new[]
            {
                response.Desktop1, response.Desktop2,
                response.Mic1, response.Mic2, response.Mic3, response.Mic4
            }
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
    }

    public async Task<bool?> GetInputMuteAsync(string inputName, CancellationToken cancellationToken)
    {
        GetInputMuteResponse? response = await client
            .GetInputMuteAsync(inputName, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response?.InputMuted;
    }

    public async Task SetInputMuteAsync(string inputName, bool muted, CancellationToken cancellationToken) =>
        await client.SetInputMuteAsync(muted, inputName, cancellationToken: cancellationToken).ConfigureAwait(false);
}

/// <summary>
///     Disables audio capture by muting the OBS audio inputs at runtime and restoring each
///     input's prior mute state afterwards. The user's OBS profile is never permanently
///     modified: only runtime mute state is touched, and inputs that were already muted are
///     left untouched (and therefore stay muted after restore).
/// </summary>
internal sealed partial class ObsAudioMuteController(IObsAudioClient client, ILogger logger)
{
    /// <summary>
    ///     Unversioned OBS input kinds that produce audio and therefore must be muted when
    ///     audio capture is disabled. Covers the dedicated audio capture sources on all
    ///     platforms plus source kinds that can emit audio into the mix (game capture with
    ///     "Capture Audio" enabled, media/browser sources).
    /// </summary>
    private static readonly HashSet<string> AudioInputKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "wasapi_input_capture",
        "wasapi_output_capture",
        "wasapi_process_output_capture",
        "dshow_input",
        // macOS
        "coreaudio_input_capture",
        "coreaudio_output_capture",
        "sck_audio_capture",
        "av_capture_input",
        // Linux
        "pulse_input_capture",
        "pulse_output_capture",
        "alsa_input_capture",
        "jack_output_capture",
        "pipewire-audio-capture-source",
        "pipewire-audio-capture-sink",
        // Sources that can emit audio into the mix
        "game_capture",
        "ffmpeg_source",
        "vlc_source",
        "browser_source"
    };

    /// <summary>
    ///     Prior mute state per input name, captured when the mutes were applied.
    ///     Null when no mutes are currently applied.
    /// </summary>
    private Dictionary<string, bool>? _savedMuteStates;

    /// <summary>
    ///     True while mutes are applied and not yet restored.
    /// </summary>
    public bool HasSavedStates => _savedMuteStates != null;

    /// <summary>
    ///     Enumerates the OBS audio inputs (special/global inputs plus inputs with an
    ///     audio-producing kind), records each input's current mute state, and mutes the ones
    ///     that are not already muted. Idempotent: a no-op while mutes are already applied.
    /// </summary>
    public async Task MuteAllAsync(CancellationToken cancellationToken)
    {
        if (_savedMuteStates != null)
        {
            // Already applied (e.g. the previous clip failed before restore ran). Keep the
            // originally captured prior states rather than re-reading our own mutes as "prior".
            return;
        }

        List<string> targets = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in await client.GetSpecialInputNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(name))
            {
                targets.Add(name);
            }
        }

        foreach (ObsAudioInputInfo input in await client.GetInputsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (AudioInputKinds.Contains(input.Kind) && seen.Add(input.Name))
            {
                targets.Add(input.Name);
            }
        }

        // Assign before muting so a partial failure mid-loop still restores what was muted.
        Dictionary<string, bool> saved = new(StringComparer.OrdinalIgnoreCase);
        _savedMuteStates = saved;

        foreach (string name in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bool? priorMuted = await client.GetInputMuteAsync(name, cancellationToken).ConfigureAwait(false);
                if (priorMuted == null)
                {
                    continue;
                }

                saved[name] = priorMuted.Value;

                if (!priorMuted.Value)
                {
                    await client.SetInputMuteAsync(name, muted: true, cancellationToken).ConfigureAwait(false);
                    LogMutedInput(name);
                }
                else
                {
                    LogInputAlreadyMuted(name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogErrorMutingInput(ex, name);
            }
        }

        LogAudioInputsMuted(saved.Count);
    }

    /// <summary>
    ///     Restores the prior mute state of every input changed by <see cref="MuteAllAsync" />.
    ///     Inputs that were already muted before are left untouched (they stay muted).
    ///     Idempotent: a no-op when no mutes are applied. Per-input failures are logged and do
    ///     not prevent the remaining inputs from being restored.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, bool>? saved = _savedMuteStates;
        if (saved == null)
        {
            return;
        }

        _savedMuteStates = null;

        foreach ((string name, bool priorMuted) in saved)
        {
            if (priorMuted)
            {
                // We never changed this input; leave it muted, exactly as the user had it.
                continue;
            }

            try
            {
                await client.SetInputMuteAsync(name, muted: false, cancellationToken).ConfigureAwait(false);
                LogRestoredInput(name);
            }
            catch (Exception ex)
            {
                LogErrorRestoringInput(ex, name);
            }
        }

        LogAudioInputsRestored(saved.Count);
    }

    #region Logging

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Muted OBS audio input: {InputName}")]
    private partial void LogMutedInput(string inputName);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "OBS audio input already muted, leaving untouched: {InputName}")]
    private partial void LogInputAlreadyMuted(string inputName);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Error muting OBS audio input: {InputName}")]
    private partial void LogErrorMutingInput(Exception exception, string inputName);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Restored prior mute state of OBS audio input: {InputName}")]
    private partial void LogRestoredInput(string inputName);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Error restoring mute state of OBS audio input: {InputName}")]
    private partial void LogErrorRestoringInput(Exception exception, string inputName);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Audio capture disabled: muted OBS audio inputs ({Count} tracked)")]
    private partial void LogAudioInputsMuted(int count);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Restored OBS audio input mute states ({Count} tracked)")]
    private partial void LogAudioInputsRestored(int count);

    #endregion
}
