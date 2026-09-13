namespace TarkovCompanion.Core.Domain.Recognition;

/// <summary>
/// What the game's own heads-up display said in one screenshot, where it said anything.
/// </summary>
/// <remarks>
/// The game fades the HUD out when nothing has changed recently, so a screenshot taken in the
/// middle of a raid may contain no health or stamina at all. Measured on a real installation:
/// two of five in-raid screenshots had no HUD drawn anywhere in the frame.
///
/// That is why absence is a value here rather than a zero. A companion that quietly drew a
/// healthy player because the display had faded would be worse than one that says nothing: the
/// player would read a full bar off a frame that never contained one.
/// </remarks>
/// <param name="IsPresent">Whether the display was drawn in this frame at all.</param>
/// <param name="Detail">What was found, or why nothing was, in the player's own words.</param>
public sealed record HudReading(bool IsPresent, string Detail)
{
    /// <summary>No display in this frame, which is ordinary rather than a failure.</summary>
    public static HudReading Absent { get; } = new(
        false,
        "The game had faded its display out of this screenshot, so it carries no health or stamina.");

    /// <summary>
    /// The bars the display draws for stamina, longest first, in pixels.
    /// </summary>
    /// <remarks>
    /// Found by their own colour rather than at an assumed position, because the display is
    /// anchored to the frame's left edge and a region calibrated on one aspect ratio would sit
    /// somewhere else entirely on another. On a real installation the same bar landed on
    /// identical pixels across three screenshots, so its own colour locates it exactly.
    /// </remarks>
    public IReadOnlyList<HudBar> Bars { get; init; } = [];

    /// <summary>Where the whole display cluster was found, for anything that wants to crop it.</summary>
    public PixelRect? Bounds { get; init; }
}

/// <summary>
/// One bar of the heads-up display, as drawn.
/// </summary>
/// <remarks>
/// The length is reported rather than a percentage. A percentage needs the length of the empty
/// track behind the bar, and the game does not draw one in a colour this can find, so the only
/// honest reading is "this many pixels wide" plus whatever the widest one ever seen was.
/// Printing a made-up denominator would be a number a player would believe.
/// </remarks>
/// <param name="Bounds">Where the filled part of the bar is.</param>
/// <param name="FilledPixels">How many pixels of it are drawn, which varies with fill.</param>
public sealed record HudBar(PixelRect Bounds, int FilledPixels)
{
    /// <summary>How long the drawn part is, which is the part that changes.</summary>
    public int Length => Bounds.Width;
}
