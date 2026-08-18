#region

using Cs2VideoGenerator.Core.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     In-memory fake of the OBS audio-input surface <see cref="ObsAudioMuteController" /> talks to.
///     Records every GetInputMute/SetInputMute call, so a test can assert exactly which
///     obs-websocket requests would have gone out.
/// </summary>
internal sealed class FakeObsAudioClient : IObsAudioClient
{
    /// <summary>Inputs returned by GetInputList (name + unversioned kind).</summary>
    public List<ObsAudioInputInfo> Inputs { get; } = [];

    /// <summary>Names returned by GetSpecialInputs (desktop audio / mic slots).</summary>
    public List<string> SpecialInputNames { get; } = [];

    /// <summary>Current mute state per input; inputs absent from this map report null (unknown).</summary>
    public Dictionary<string, bool> MuteStates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inputs whose GetInputMute call throws (simulates an obs-websocket error).</summary>
    public HashSet<string> GetMuteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every GetInputMute call, in order.</summary>
    public List<string> GetMuteCalls { get; } = [];

    /// <summary>Every SetInputMute call, in order.</summary>
    public List<(string Name, bool Muted)> SetMuteCalls { get; } = [];

    public Task<IReadOnlyList<ObsAudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ObsAudioInputInfo>>(Inputs);

    public Task<IReadOnlyList<string>> GetSpecialInputNamesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(SpecialInputNames);

    public Task<bool?> GetInputMuteAsync(string inputName, CancellationToken cancellationToken)
    {
        GetMuteCalls.Add(inputName);
        if (GetMuteFailures.Contains(inputName))
        {
            throw new InvalidOperationException($"Simulated obs-websocket failure for '{inputName}'");
        }

        return Task.FromResult<bool?>(MuteStates.TryGetValue(inputName, out bool muted) ? muted : null);
    }

    public Task SetInputMuteAsync(string inputName, bool muted, CancellationToken cancellationToken)
    {
        SetMuteCalls.Add((inputName, muted));
        MuteStates[inputName] = muted;
        return Task.CompletedTask;
    }
}

/// <summary>
///     <see cref="ObsAudioMuteController" /> under captureAudio=false. Every OBS audio input
///     is muted and its prior state restored afterwards (already-muted inputs stay muted), non-audio
///     inputs are never touched, and one input failing does not stop the rest being muted and
///     restored.
/// </summary>
public class ObsAudioMuteControllerTests
{
    private static FakeObsAudioClient CreateFakeWithTypicalInputs()
    {
        FakeObsAudioClient fake = new();

        // special (global) audio inputs: desktop audio unmuted, mic already muted by the user
        fake.SpecialInputNames.AddRange(["Desktop Audio", "Mic/Aux"]);
        fake.MuteStates["Desktop Audio"] = false;
        fake.MuteStates["Mic/Aux"] = true;

        // input list: the CSVG game capture source (emits audio via "Capture Audio"), a duplicate
        // of a special input, and a non-audio input that must never be touched
        fake.Inputs.AddRange([
            new ObsAudioInputInfo("Counter-Strike 2", "game_capture"),
            new ObsAudioInputInfo("Desktop Audio", "wasapi_output_capture"),
            new ObsAudioInputInfo("Webcam Overlay", "color_source")
        ]);
        fake.MuteStates["Counter-Strike 2"] = false;

        return fake;
    }

    private static ObsAudioMuteController CreateController(FakeObsAudioClient fake) =>
        new(fake, NullLogger.Instance);

    [Fact]
    public async Task MuteAll_MutesAllUnmutedAudioInputs_AndSkipsAlreadyMutedAndNonAudioInputs()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        ObsAudioMuteController controller = CreateController(fake);

        await controller.MuteAllAsync(TestContext.Current.CancellationToken);

        // Unmuted audio inputs got muted exactly once each (special input deduplicated).
        Assert.Equal(
            new[] { ("Desktop Audio", true), ("Counter-Strike 2", true) },
            fake.SetMuteCalls);

        Assert.Contains("Mic/Aux", fake.GetMuteCalls);
        Assert.DoesNotContain("Webcam Overlay", fake.GetMuteCalls);
        Assert.True(fake.MuteStates["Mic/Aux"]);
        Assert.True(controller.HasSavedStates);
    }

    [Fact]
    public async Task MuteAll_WhenAlreadyApplied_IsIdempotent()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        ObsAudioMuteController controller = CreateController(fake);

        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        int callsAfterFirst = fake.SetMuteCalls.Count + fake.GetMuteCalls.Count;

        // A second apply (e.g. next clip prepared while a failed clip never restored) must not
        // re-read our own mutes as the "prior" state or issue further requests.
        await controller.MuteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(callsAfterFirst, fake.SetMuteCalls.Count + fake.GetMuteCalls.Count);
    }

    [Fact]
    public async Task Restore_RestoresPriorStates_AlreadyMutedInputStaysMuted()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        ObsAudioMuteController controller = CreateController(fake);

        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        fake.SetMuteCalls.Clear();

        await controller.RestoreAsync(TestContext.Current.CancellationToken);

        // only the inputs we muted get unmuted; the user's mic gets no call and stays muted
        Assert.Equal(2, fake.SetMuteCalls.Count);
        Assert.Contains(("Desktop Audio", false), fake.SetMuteCalls);
        Assert.Contains(("Counter-Strike 2", false), fake.SetMuteCalls);
        Assert.DoesNotContain(fake.SetMuteCalls, call => call.Name == "Mic/Aux");

        Assert.False(fake.MuteStates["Desktop Audio"]);
        Assert.False(fake.MuteStates["Counter-Strike 2"]);
        Assert.True(fake.MuteStates["Mic/Aux"]);
        Assert.False(controller.HasSavedStates);
    }

    [Fact]
    public async Task Restore_IsIdempotent_SecondCallIssuesNoRequests()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        ObsAudioMuteController controller = CreateController(fake);

        await controller.MuteAllAsync(TestContext.Current.CancellationToken);
        await controller.RestoreAsync(TestContext.Current.CancellationToken);
        fake.SetMuteCalls.Clear();

        await controller.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fake.SetMuteCalls);
    }

    [Fact]
    public async Task Restore_WithoutMute_IssuesNoRequests()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        ObsAudioMuteController controller = CreateController(fake);

        await controller.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fake.SetMuteCalls);
        Assert.Empty(fake.GetMuteCalls);
    }

    [Fact]
    public async Task MuteAll_PerInputFailure_OtherInputsStillMutedAndRestored()
    {
        FakeObsAudioClient fake = CreateFakeWithTypicalInputs();
        fake.GetMuteFailures.Add("Desktop Audio");
        ObsAudioMuteController controller = CreateController(fake);

        await controller.MuteAllAsync(TestContext.Current.CancellationToken);

        // The failing input is skipped; the rest are still muted.
        Assert.Equal(new[] { ("Counter-Strike 2", true) }, fake.SetMuteCalls);

        fake.SetMuteCalls.Clear();
        await controller.RestoreAsync(TestContext.Current.CancellationToken);

        // Restore covers exactly what was recorded - no attempt to unmute the failed input.
        Assert.Equal(new[] { ("Counter-Strike 2", false) }, fake.SetMuteCalls);
        Assert.False(controller.HasSavedStates);
    }
}
