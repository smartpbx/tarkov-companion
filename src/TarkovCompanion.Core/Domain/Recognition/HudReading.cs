namespace TarkovCompanion.Core.Domain.Recognition;

/// <summary>
/// Which of the two bars the game draws in the corner of the screen this is.
/// </summary>
/// <remarks>
/// Named by the colour a player can see rather than by what it measures, because what each one
/// measures has not been established. A vertical slice through a real frame found two bars
/// three pixels tall with a six pixel gap: the upper blue-dominant at 43,129,151 and the lower
/// green-dominant at 43,151,129. Calling one of them "stamina" would be a guess printed as a
/// fact, and a player looking at their own screen knows which bar is which by its colour.
/// </remarks>
public enum HudBarKind
{
    /// <summary>The upper bar, drawn blue over green.</summary>
    Blue,

    /// <summary>The lower bar, drawn green over blue.</summary>
    Green,
}

/// <summary>
/// What the game's own heads-up display said in one screenshot, where it said anything.
/// </summary>
/// <remarks>
/// The game fades the display out when nothing has changed recently, so a screenshot taken in
/// the middle of a raid may contain no display at all. Measured across 261 in-raid screenshots
/// on a real installation: 35 of them, 13.4%, had none drawn anywhere in the frame.
///
/// That is why absence is a value here rather than a zero. A companion that quietly drew a full
/// bar because the display had faded would be worse than one that says nothing: the player
/// would read a number off a frame that never contained one.
/// </remarks>
/// <param name="IsPresent">Whether the display was drawn in this frame at all.</param>
/// <param name="Detail">What was found, or why nothing was, in the player's own words.</param>
public sealed record HudReading(bool IsPresent, string Detail)
{
    /// <summary>No display in this frame, which is ordinary rather than a failure.</summary>
    public static HudReading Absent { get; } = new(
        false,
        "The game had faded its display out of this screenshot, so it carries no bars to read.");

    /// <summary>
    /// The bars the display drew, longest first.
    /// </summary>
    /// <remarks>
    /// Found by their own colour rather than at an assumed position, because the display is
    /// anchored to the frame's left edge and a region calibrated on one aspect ratio would sit
    /// somewhere else entirely on another. On a real installation the same bars landed on
    /// identical pixels across every screenshot that had them, so their colour locates them
    /// exactly.
    /// </remarks>
    public IReadOnlyList<HudBar> Bars { get; init; } = [];

    /// <summary>Where the whole cluster was found, for anything that wants to crop it.</summary>
    public PixelRect? Bounds { get; init; }

    /// <summary>Whether this frame's body silhouette could be read, and why not where it could not.</summary>
    public SilhouetteLegibility Silhouette { get; init; } = SilhouetteLegibility.NotLookedAt;

    /// <summary>The bar of one colour, where it was drawn.</summary>
    public HudBar? Bar(HudBarKind kind) => Bars.FirstOrDefault(bar => bar.Kind == kind);
}

/// <summary>
/// Whether the body silhouette in this frame is drawn brightly enough to read limbs from.
/// </summary>
/// <remarks>
/// <para>
/// Every way of reading limb health from a screenshot classifies the figure's outline: white
/// means healthy, red means hurt, absent means the limb is gone. That needs an outline bright
/// enough to tell from whatever is behind it, and on the installation this was measured against
/// there is not one. Over grass the brightest pixel anywhere in the silhouette reaches
/// luminance 83, the median is 31, and not a single pixel has all three channels above 150.
/// </para>
/// <para>
/// Run anyway, a classifier does not fail loudly; it answers confidently and wrongly in both
/// directions. Measured on real frames: over a sand bank three quarters of the region passes a
/// red test, because sand at 163,113,68 is redder than it is green or blue, and a healthy
/// player is reported hurt. Over grass nothing passes either test, so the limb is reported
/// destroyed. With the display faded out entirely the answer is the same destroyed, which means
/// "the game drew nothing" and "the leg is gone" are indistinguishable on 13% of frames.
/// </para>
/// <para>
/// So this is measured and refused rather than guessed at. Whether the outline is dim because
/// of a graphics setting, a game version or a monitor is not something a screenshot can say,
/// and somebody else's screenshots may well be legible. That is exactly why the answer is "this
/// frame's outline peaks at 83 and reading limbs needs about 150" rather than "limb health does
/// not work".
/// </para>
/// </remarks>
/// <param name="IsLegible">Whether a limb could be told from its background in this frame.</param>
/// <param name="Brightest">The brightest pixel found anywhere in the silhouette.</param>
/// <param name="Detail">What that means, in the player's own words.</param>
public sealed record SilhouetteLegibility(bool IsLegible, int Brightest, string Detail)
{
    /// <summary>How bright an outline has to be before a limb can be told from its background.</summary>
    /// <remarks>
    /// The threshold every classifier of this kind uses, and the one the measurement is against.
    /// A frame under it is refused; a frame over it has not been proven readable, only proven
    /// not to fail for this reason, which is why nothing yet acts on a legible answer.
    /// </remarks>
    public const int RequiredLuminance = 150;

    /// <summary>Nothing was looked at, because the display was not drawn in this frame.</summary>
    public static SilhouetteLegibility NotLookedAt { get; } = new(
        false,
        0,
        "The game drew no display in this screenshot, so there was no silhouette to look at.");
}

/// <summary>
/// One bar of the display, as drawn.
/// </summary>
/// <remarks>
/// A length in pixels rather than a percentage, and deliberately so. A percentage needs the
/// length of the empty track behind the bar, and a horizontal walk across a real frame found
/// the colour going straight from bar to scene with nothing between: the game draws the filled
/// part and no track at all. The only honest reading is how long this one is against the
/// longest ever seen, which is what <see cref="Fraction"/> is for, and it says so rather than
/// printing a number with an invented denominator.
///
/// The pixel count is not the fill level either. An earlier measurement read 427 against 438
/// pixels on two frames and took it for a difference in fill; both bars in fact ran from x=51
/// to x=196 exactly, and the counts differed only because antialiased edge rows fall in and out
/// of any colour test. Length is measured from the extent, never from a count.
/// </remarks>
/// <param name="Kind">Which of the two bars this is, by the colour a player can see.</param>
/// <param name="Bounds">Where the drawn part of the bar is.</param>
public sealed record HudBar(HudBarKind Kind, PixelRect Bounds)
{
    /// <summary>How long the drawn part is, which is the part that changes.</summary>
    public int Length => Bounds.Width;

    /// <summary>
    /// How long this is against the longest of its colour ever seen, or null before there is one.
    /// </summary>
    /// <remarks>
    /// Null rather than one on the first reading. A companion that answered "full" the first
    /// time it saw a bar would be right only by accident, and wrong in the one case that
    /// matters: the first screenshot a player takes after running themselves empty.
    /// </remarks>
    public double? Fraction(int longestSeen) =>
        longestSeen <= 0 || longestSeen < Length ? null : (double)Length / longestSeen;
}
