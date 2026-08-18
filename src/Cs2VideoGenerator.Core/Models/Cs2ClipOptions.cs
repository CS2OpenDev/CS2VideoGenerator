namespace Cs2VideoGenerator.Core.Models;

/// <summary>
///     Flags for demo playback options.
/// </summary>
[Flags]
public enum Cs2ClipOptions
{
    /// <summary>
    ///     All options disabled: no x-ray, no True View, no HUD, no assists in the kill feed,
    ///     and no death-notices-only overlay. Each flag maps to an explicit console command,
    ///     so an unset flag actively turns the feature off rather than leaving it unchanged.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Shows player silhouettes through walls during playback (<c>spec_show_xray 1</c>),
    ///     as seen in the standard broadcast/spectator view. When unset, x-ray is turned off
    ///     so only directly visible players are rendered.
    /// </summary>
    XrayEnabled = 1 << 0,

    /// <summary>
    ///     Enables demo prediction (<c>cl_demo_predict 1</c>), Valve's "True View": first-person
    ///     playback re-predicts the player's view to match what they actually saw, instead of the
    ///     slightly smoothed/interpolated demo view. Recommended for first-person POV clips.
    /// </summary>
    TrueViewEnabled = 1 << 1,

    /// <summary>
    ///     Draws the in-game HUD (<c>cl_drawhud 1</c>): health, ammo, money, round timer,
    ///     scoreboard elements, and the kill feed. When unset the entire HUD is hidden,
    ///     producing a clean frame (combine with <see cref="ShowOnlyDeathNotices" /> to keep
    ///     just the kill feed).
    /// </summary>
    HudEnabled = 1 << 2,

    /// <summary>
    ///     Includes assisting players in kill-feed entries (<c>mp_display_kill_assists 1</c>).
    ///     Only visible when the kill feed itself is drawn (via <see cref="HudEnabled" /> or
    ///     <see cref="ShowOnlyDeathNotices" />).
    /// </summary>
    ShowAssistsInFeed = 1 << 3,

    /// <summary>
    ///     Hides all HUD elements except death notices (<c>cl_draw_only_deathnotices 1</c>),
    ///     keeping the kill feed on an otherwise clean screen. This is the classic
    ///     "frag movie" overlay; it takes effect even when <see cref="HudEnabled" /> is unset.
    /// </summary>
    ShowOnlyDeathNotices = 1 << 4,

    /// <summary>
    ///     The standard spectator experience: x-ray, True View, full HUD, and assists in the
    ///     kill feed. Matches what a viewer would see watching the demo in-game with default
    ///     settings.
    /// </summary>
    Default = XrayEnabled | TrueViewEnabled | HudEnabled | ShowAssistsInFeed,

    /// <summary>
    ///     <see cref="Default" /> with the HUD disabled but death notices kept: the full HUD is
    ///     hidden while the kill feed (including assists) remains visible via
    ///     <see cref="ShowOnlyDeathNotices" />. The typical choice for clean highlight clips.
    /// </summary>
    NoHudDefault = (Default & ~HudEnabled) | ShowOnlyDeathNotices
}
