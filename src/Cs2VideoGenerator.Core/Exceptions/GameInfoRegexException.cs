namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     This exception is thrown when the expected SearchPaths section cannot be found in the gameinfo.gi file.
/// </summary>
/// <param name="cs2GameInfoFilePath">The path to the gameinfo.gi file</param>
/// <param name="matchValue">The regex value for debugging</param>
public class GameInfoRegexException(string cs2GameInfoFilePath, string matchValue) : Exception(
    $"The gameinfo.gi file at `{cs2GameInfoFilePath}` could not be modified because the expected SearchPaths section could not be found. The regex match value was: `{matchValue}`");
