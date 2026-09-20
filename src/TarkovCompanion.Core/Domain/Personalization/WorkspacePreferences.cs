namespace TarkovCompanion.Core.Domain.Personalization;

/// <summary>Which palette the player asked for.</summary>
public enum AppearanceTheme
{
    /// <summary>Follow whatever the desktop is set to.</summary>
    System = 0,
    Light,
    Dark,
    HighContrast,
}

/// <summary>Which of the colour-vision-safe status palettes to use, if any.</summary>
public enum ColorVisionMode
{
    Standard = 0,
    RedGreenSafe,
    BlueYellowSafe,
    Monochrome,
}

/// <summary>How much whitespace the workspaces are drawn with.</summary>
public enum InterfaceDensity
{
    Standard = 0,
    Compact,
    Comfortable,
}

/// <summary>
/// Everything a player can choose about how the companion looks, in one versioned record.
/// </summary>
/// <remarks>
/// One record and one file, not a setting per feature. The design system shipped light,
/// high-contrast and three colour-vision palettes, and every one of them was unreachable because
/// nothing persisted a choice: <c>App.axaml</c> pinned Dark and <c>V2Appearance.Resolve</c> had no
/// caller. The fix is not another loose key in another JSON file — it is one record the appearance
/// applier reads and the Setup page writes.
///
/// <para>
/// <see cref="SchemaVersion"/> is written into the file so a later shape can be recognised rather
/// than guessed at. A file from a newer version is not read: the reader hands back
/// <see cref="Default"/>, because a half-understood appearance is worse than the one that has
/// always worked. Every other malformed value is clamped by <see cref="Normalized"/> instead,
/// since a hand-edited text scale of 900 should give a readable window, not an empty one.
/// </para>
/// <para>
/// Platform-neutral on purpose (#266's semantic manifest, #315's preference record): the Avalonia
/// adapter maps these to theme variants and resource values, and the later tablet implementation
/// can map the same names to its own.
/// </para>
/// </remarks>
/// <param name="Theme">The palette, or System to follow the desktop.</param>
/// <param name="ColorVision">Which status palette, for anyone who cannot separate the default ones.</param>
/// <param name="TextScalePercent">The type ramp as a percentage of the designed size.</param>
/// <param name="Density">How much whitespace the workspaces use.</param>
/// <param name="ReduceMotion">Whether transitions are dropped, keeping the state change.</param>
/// <param name="FocusAlwaysVisible">
/// Whether the keyboard focus ring draws after a pointer click too, not only after Tab. Off by
/// default because Fluent's own :focus-visible heuristic (no ring after a mouse click) is what
/// most players expect; on for anyone who tracks focus visually across both input methods.
/// </param>
public sealed record WorkspacePreferences(
    AppearanceTheme Theme = AppearanceTheme.Dark,
    ColorVisionMode ColorVision = ColorVisionMode.Standard,
    int TextScalePercent = 100,
    InterfaceDensity Density = InterfaceDensity.Standard,
    bool ReduceMotion = false,
    bool FocusAlwaysVisible = false)
{
    /// <summary>The shape this build writes and the only one it reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The text scales on offer, smallest first.
    /// </summary>
    /// <remarks>
    /// Five steps rather than a slider, for the reason the window scale beside it gives: the type
    /// ramp, the row heights and the fixed columns were designed against these, and a continuum of
    /// arbitrary multipliers is a continuum of layouts nobody has looked at. 100 to 200 is the
    /// range #266 asks to be proved.
    /// </remarks>
    public static IReadOnlyList<int> TextScales { get; } = [100, 125, 150, 175, 200];

    /// <summary>What a player who has never opened Appearance gets: exactly today's app.</summary>
    public static WorkspacePreferences Default { get; } = new();

    /// <summary>The nearest offered text scale, for a stored or stepped-past value.</summary>
    public static int NearestTextScale(int percent) =>
        TextScales.MinBy(offered => Math.Abs(offered - percent));

    /// <summary>The scale one step larger or smaller, stopping at the ends rather than wrapping.</summary>
    public static int StepTextScale(int from, int direction)
    {
        var index = TextScales.ToList().IndexOf(NearestTextScale(from));
        return TextScales[Math.Clamp(index + Math.Sign(direction), 0, TextScales.Count - 1)];
    }

    /// <summary>The same preferences with every value inside the range this build understands.</summary>
    public WorkspacePreferences Normalized() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppearanceTheme.Dark,
        ColorVision = Enum.IsDefined(ColorVision) ? ColorVision : ColorVisionMode.Standard,
        TextScalePercent = NearestTextScale(TextScalePercent),
        Density = Enum.IsDefined(Density) ? Density : InterfaceDensity.Standard,
    };

    /// <summary>The text scale as the multiplier a type ramp is measured in.</summary>
    public double TextScale => NearestTextScale(TextScalePercent) / 100d;
}
