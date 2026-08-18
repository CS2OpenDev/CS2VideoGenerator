namespace Cs2VideoGenerator.Core.Steam;

/// <summary>
///     Information about a Steam user account.
/// </summary>
public record SteamUserInfo
{
    /// <summary>
    ///     The Steam ID (64-bit) of the user.
    /// </summary>
    public required long SteamId64 { get; init; }

    /// <summary>
    ///     The account name used for login.
    /// </summary>
    public required string AccountName { get; init; }

    /// <summary>
    ///     The display name (persona name) of the user.
    /// </summary>
    public string? PersonaName { get; init; }

    /// <summary>
    ///     Whether this is the most recently logged in user.
    /// </summary>
    public bool IsMostRecent { get; init; }

    /// <summary>
    ///     The timestamp of the last login, if available.
    /// </summary>
    public DateTimeOffset? LastLoginTimestamp { get; init; }

    /// <summary>
    ///     Gets the Steam ID in the standard format (STEAM_X:Y:Z).
    /// </summary>
    public string SteamId
    {
        get
        {
            // SteamID64 = 76561197960265728 + AuthServer + AccountID * 2
            const long steamId64Base = 76561197960265728L;
            long accountId = SteamId64 - steamId64Base;
            int authServer = (int)(accountId % 2);
            int accountNumber = (int)(accountId / 2);
            return $"STEAM_0:{authServer}:{accountNumber}";
        }
    }

    /// <summary>
    ///     Gets the Steam ID 3 format ([U:1:AccountID]).
    /// </summary>
    public string SteamId3
    {
        get
        {
            const long steamId64Base = 76561197960265728L;
            long accountId = SteamId64 - steamId64Base;
            return $"[U:1:{accountId}]";
        }
    }

    /// <summary>
    ///     Gets the 32-bit account ID used in Steam userdata folder paths.
    /// </summary>
    public long AccountId32
    {
        get
        {
            const long steamId64Base = 76561197960265728L;
            return SteamId64 - steamId64Base;
        }
    }

    /// <summary>
    ///     Returns a human-readable identifier for logs and diagnostics: the persona name
    ///     (falling back to the account name when no persona is known) followed by the
    ///     SteamID64 in parentheses, e.g. <c>"PlayerName (76561197960265729)"</c>.
    /// </summary>
    public override string ToString() => $"{PersonaName ?? AccountName} ({SteamId64})";
}
