namespace Cs2VideoGenerator.Core.Exceptions;

/// <summary>
///     An exception that is thrown when the Counter-Strike Global Offensive installation directory cannot be found in
///     common paths.
/// </summary>
/// <param name="counterStrikeGlobalOffensiveDirectory">
///     The path that was provided or auto-resolved for the Counter-Strike
///     Global Offensive instalation directory
/// </param>
public class CounterStrikeDirectoryNotFoundException(string[] counterStrikeGlobalOffensiveDirectory) : Exception(
    $"The provided path(s) does not exist or is not a valid file. The Counter-Strike Global Offensive directory must be provided or leave blank for auto-discovery.\nSearched paths:\n- {string.Join("\n- ", counterStrikeGlobalOffensiveDirectory)}");
