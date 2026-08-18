#region

using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Tests.TestSupport;
using Xunit;

#endregion

namespace Cs2VideoGenerator.Core.Tests;

/// <summary>
///     Finding the engine's wav, deciding when it is finished, and deciding whether its length means
///     what it should. Each of the three is a place a wrong assumption about CS2 turns into a wrong
///     output, and the second one is subtle: a wav read too early reports a length that looks exactly
///     like the defect the third check exists to catch.
/// </summary>
public class InEngineAudioLocatorTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(10);
    private readonly InEngineAudioLocator _locator = new();
    private readonly TempDir _temp = new("csvg-inengine-audio-tests-");

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Discovery

    [Fact]
    public void FindWav_MatchesTheMovieName()
    {
        WavFixture.Write(_temp.Combine("csvg_000.wav"), 1);
        WavFixture.Write(_temp.Combine("csvg_001.wav"), 1);

        string? found = _locator.FindWav(_temp.Path, "csvg_001", new InEngineEngineOutput());

        Assert.Equal(_temp.Combine("csvg_001.wav"), found);
    }

    /// <summary>
    ///     The wav is expected in a timestamped subdirectory, so the search descends by default.
    /// </summary>
    [Fact]
    public void FindWav_DescendsIntoSubdirectories()
    {
        Directory.CreateDirectory(_temp.Combine("2026-07-30_113000"));
        WavFixture.Write(_temp.Combine("2026-07-30_113000", "csvg_000.wav"), 1);

        string? found = _locator.FindWav(_temp.Path, "csvg_000", new InEngineEngineOutput());

        Assert.Equal(_temp.Combine("2026-07-30_113000", "csvg_000.wav"), found);
    }

    [Fact]
    public void FindWav_CanBeRestrictedToTheTopDirectory()
    {
        Directory.CreateDirectory(_temp.Combine("nested"));
        WavFixture.Write(_temp.Combine("nested", "csvg_000.wav"), 1);

        Assert.Null(_locator.FindWav(_temp.Path, "csvg_000",
            new InEngineEngineOutput { AudioSearchRecursive = false }));
    }

    [Fact]
    public void FindWav_PrefersTheNewest()
    {
        Directory.CreateDirectory(_temp.Combine("old"));
        Directory.CreateDirectory(_temp.Combine("new"));
        WavFixture.Write(_temp.Combine("old", "csvg_000.wav"), 1);
        WavFixture.Write(_temp.Combine("new", "csvg_000.wav"), 1);

        File.SetLastWriteTimeUtc(_temp.Combine("old", "csvg_000.wav"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(_temp.Combine("new", "csvg_000.wav"), DateTime.UtcNow);

        Assert.Equal(_temp.Combine("new", "csvg_000.wav"),
            _locator.FindWav(_temp.Path, "csvg_000", new InEngineEngineOutput()));
    }

    [Fact]
    public void FindWav_ReturnsNullWhenNothingMatches()
    {
        WavFixture.Write(_temp.Combine("someone_elses.wav"), 1);

        Assert.Null(_locator.FindWav(_temp.Path, "csvg_000", new InEngineEngineOutput()));
    }

    [Fact]
    public void FindWav_ReturnsNullWhenTheDirectoryDoesNotExist()
    {
        Assert.Null(_locator.FindWav(_temp.Combine("no-such-directory"), "csvg_000", new InEngineEngineOutput()));
    }

    /// <summary>
    ///     Where the wav lands is unverified, so the glob is configuration.
    /// </summary>
    [Fact]
    public void FindWav_HonorsAConfiguredPattern()
    {
        WavFixture.Write(_temp.Combine("movie_csvg_000_audio.wav"), 1);

        Assert.Equal(_temp.Combine("movie_csvg_000_audio.wav"),
            _locator.FindWav(_temp.Path, "csvg_000",
                new InEngineEngineOutput { AudioFileSearchPattern = "movie_{0}_audio.wav" }));
    }

    #endregion

    #region Header reading

    [Fact]
    public void TryReadHeader_ReadsFormatAndDuration()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 2.5);

        WavHeader? header = InEngineAudioLocator.TryReadHeader(path);

        Assert.NotNull(header);
        Assert.Equal(2, header.Channels);
        Assert.Equal(48000, header.SampleRate);
        Assert.Equal(16, header.BitsPerSample);
        Assert.True(header.IsFinalized);
        Assert.Equal(2.5, header.Duration.TotalSeconds, 3);
    }

    /// <summary>
    ///     LIST and fact chunks can sit between fmt and data, and an odd-length chunk is padded to
    ///     even. A reader that assumed data followed fmt would find garbage.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public void TryReadHeader_WalksPastChunksBeforeData(int extraChunkBytes)
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 1.5, extraChunkBeforeData: extraChunkBytes);

        WavHeader? header = InEngineAudioLocator.TryReadHeader(path);

        Assert.NotNull(header);
        Assert.True(header.IsFinalized);
        Assert.Equal(1.5, header.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void TryReadHeader_ReturnsNullForSomethingThatIsNotAWav()
    {
        string path = _temp.Combine("not-a-wav.wav");
        File.WriteAllText(path, "this is not a RIFF file at all, not even close");

        Assert.Null(InEngineAudioLocator.TryReadHeader(path));
    }

    [Fact]
    public void TryReadHeader_ReturnsNullForAMissingFile()
    {
        Assert.Null(InEngineAudioLocator.TryReadHeader(_temp.Combine("absent.wav")));
    }

    /// <summary>
    ///     A zero declared data size is the placeholder an engine leaves while streaming, not a wav of
    ///     zero length.
    /// </summary>
    [Fact]
    public void ZeroDeclaredDataSize_IsUnfinished()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 2, finalized: false);

        WavHeader? header = InEngineAudioLocator.TryReadHeader(path);

        Assert.NotNull(header);
        Assert.False(header.IsFinalized);
        Assert.True(header.AvailableDataBytes > 0);
    }

    /// <summary>
    ///     The other placeholder shape: a size the writer reserved, larger than the file.
    /// </summary>
    [Fact]
    public void OverDeclaredDataSize_IsUnfinished()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.WriteOverDeclared(path, 2);

        WavHeader? header = InEngineAudioLocator.TryReadHeader(path);

        Assert.NotNull(header);
        Assert.False(header.IsFinalized);
    }

    #endregion

    #region Finalization wait

    [Fact]
    public async Task WaitForFinalizedHeader_ReturnsImmediatelyForAFinishedWav()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 3);

        WavHeader? header = await _locator.WaitForFinalizedHeaderAsync(
            path, ShortTimeout, FastPoll, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.True(header.IsFinalized);
        Assert.Equal(3, header.Duration.TotalSeconds, 3);
    }

    /// <summary>
    ///     The engine reopens the wav after endmovie to write the real sample count. Reading before
    ///     that lands is the mistake this wait exists to prevent.
    /// </summary>
    [Fact]
    public async Task WaitForFinalizedHeader_WaitsForTheEngineToCorrectTheSizes()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 4, finalized: false);

        Task<WavHeader?> waiting = _locator.WaitForFinalizedHeaderAsync(
            path, TimeSpan.FromSeconds(10), FastPoll, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        WavFixture.Write(path, 4);

        WavHeader? header = await waiting;
        Assert.NotNull(header);
        Assert.True(header.IsFinalized);
        Assert.Equal(4, header.Duration.TotalSeconds, 3);
    }

    /// <summary>
    ///     Timing out returns what was actually seen, so the caller can say the header was still a
    ///     placeholder rather than only that it gave up.
    /// </summary>
    [Fact]
    public async Task WaitForFinalizedHeader_ReturnsTheUnfinishedHeaderOnTimeout()
    {
        string path = _temp.Combine("csvg_000.wav");
        WavFixture.Write(path, 4, finalized: false);

        WavHeader? header = await _locator.WaitForFinalizedHeaderAsync(
            path, ShortTimeout, FastPoll, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
        Assert.False(header.IsFinalized);
    }

    [Fact]
    public async Task WaitForFinalizedHeader_ReturnsNullWhenTheFileIsNotAWav()
    {
        string path = _temp.Combine("csvg_000.wav");
        File.WriteAllText(path, "nope");

        Assert.Null(await _locator.WaitForFinalizedHeaderAsync(
            path, ShortTimeout, FastPoll, TestContext.Current.CancellationToken));
    }

    #endregion

    #region Duration guard

    [Theory]
    [InlineData(10.0, 10.0)]
    [InlineData(10.1, 10.0)]
    [InlineData(9.9, 10.0)]
    [InlineData(10.2, 10.0)]
    [InlineData(9.8, 10.0)]
    public void DurationsWithinTolerance_Match(double wavSeconds, double expectedSeconds)
    {
        AudioDurationCheck check = InEngineAudioLocator.CheckDuration(
            TimeSpan.FromSeconds(wavSeconds), expectedSeconds, 2.0);

        Assert.Equal(AudioDurationVerdict.Matches, check.Verdict);
    }

    /// <summary>
    ///     The regression shape: the wav comes out at wall-clock length while the video is
    ///     movie-length, which for an offline render is far longer than the video.
    /// </summary>
    [Fact]
    public void AWavLongerThanTheVideo_IsTooLong()
    {
        AudioDurationCheck check = InEngineAudioLocator.CheckDuration(
            TimeSpan.FromSeconds(43), 10, 2.0);

        Assert.Equal(AudioDurationVerdict.TooLong, check.Verdict);
        Assert.Equal(3.3, check.RelativeError, 2);
    }

    [Fact]
    public void AWavShorterThanTheVideo_IsTooShort()
    {
        AudioDurationCheck check = InEngineAudioLocator.CheckDuration(
            TimeSpan.FromSeconds(5), 10, 2.0);

        Assert.Equal(AudioDurationVerdict.TooShort, check.Verdict);
        Assert.Equal(-0.5, check.RelativeError, 3);
    }

    [Fact]
    public void ToleranceIsConfigurable()
    {
        AudioDurationCheck strict = InEngineAudioLocator.CheckDuration(TimeSpan.FromSeconds(10.15), 10, 1.0);
        AudioDurationCheck loose = InEngineAudioLocator.CheckDuration(TimeSpan.FromSeconds(10.15), 10, 5.0);

        Assert.Equal(AudioDurationVerdict.TooLong, strict.Verdict);
        Assert.Equal(AudioDurationVerdict.Matches, loose.Verdict);
    }

    /// <summary>
    ///     A clip with no video length to compare against cannot produce a meaningful ratio, and
    ///     reporting one would be a divide by zero dressed up as a finding.
    /// </summary>
    [Fact]
    public void AZeroLengthVideo_ProducesNoVerdict()
    {
        AudioDurationCheck check = InEngineAudioLocator.CheckDuration(TimeSpan.FromSeconds(5), 0, 2.0);

        Assert.Equal(AudioDurationVerdict.Matches, check.Verdict);
    }

    #endregion
}
