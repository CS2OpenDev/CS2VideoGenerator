#region

using System.Globalization;

#endregion

namespace Cs2VideoGenerator.Core.Capture;

/// <summary>
///     How the frames of an offline render are named on disk.
///     Every part of this is a parameter rather than a constant on purpose: what CS2's
///     <c>startmovie</c> actually writes has not been observed on a real machine yet, and a guess
///     baked into control flow would have to be dug back out. Correcting it later means passing
///     different values here, not changing the consumer.
/// </summary>
internal sealed record FrameSequencePattern
{
    /// <summary>
    ///     Text before the frame number. For a frame dump this is the movie name the engine was
    ///     given, possibly with a separator.
    /// </summary>
    // Unverified. Confirm on a machine with CS2 whether the engine appends anything of its
    // own between the movie name and the number.
    public required string Prefix { get; init; }

    /// <summary>
    ///     How many digits the zero-padded frame number occupies.
    /// </summary>
    // Unverified. Confirm the padding width the engine writes; a sequence long enough to
    // overflow it would also need confirming (does the engine widen, or wrap?).
    public required int DigitCount { get; init; }

    /// <summary>
    ///     File extension without the dot.
    /// </summary>
    // Unverified. tga is certain, jpg likely, png unknown; confirm which formats the engine
    // can be asked for, and that the extension matches the format.
    public required string Extension { get; init; }

    /// <summary>
    ///     The number the engine gives its first frame. Used as the starting point for gap
    ///     detection; a sequence that turns out to start elsewhere is adopted rather than reported
    ///     as a run of dropped frames.
    /// </summary>
    // Unverified. Confirm whether numbering is 0- or 1-based.
    public required int FirstFrameNumber { get; init; }

    /// <summary>
    ///     Extension with any leading dot removed, so callers may pass either form.
    /// </summary>
    private string NormalizedExtension => Extension.TrimStart('.');

    /// <summary>
    ///     Directory search pattern matching this sequence and nothing else: one <c>?</c> per digit
    ///     rather than a <c>*</c>, so a stale file with a different width is not swept up.
    /// </summary>
    public string SearchPattern => $"{Prefix}{new string('?', DigitCount)}.{NormalizedExtension}";

    /// <summary>
    ///     The file name this pattern gives frame <paramref name="frameNumber" />.
    /// </summary>
    public string FileName(int frameNumber) =>
        $"{Prefix}{frameNumber.ToString(new string('0', DigitCount), CultureInfo.InvariantCulture)}." +
        NormalizedExtension;

    /// <summary>
    ///     Reads the frame number out of a file name, rejecting anything that is not exactly this
    ///     pattern (wrong prefix, wrong digit width, wrong extension). File-name comparison is
    ///     case-insensitive because the same staging directory has to behave the same on Windows
    ///     and Linux.
    /// </summary>
    public bool TryGetFrameNumber(string fileName, out int frameNumber)
    {
        frameNumber = 0;

        string suffix = "." + NormalizedExtension;
        int expectedLength = Prefix.Length + DigitCount + suffix.Length;

        if (fileName.Length != expectedLength
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> digits = fileName.AsSpan(Prefix.Length, DigitCount);

        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out frameNumber);
    }
}
