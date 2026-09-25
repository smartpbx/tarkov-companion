namespace TarkovCompanion.Application.Services.Shell;

/// <summary>
/// Where the window was and how the rail was left.
/// </summary>
/// <remarks>
/// The window opened at 1500 by 900 every time, in whatever place the operating system chose,
/// and no store had an entry for it — so a companion that shares a 3840×1080 screen with the
/// game was dragged back into its slot on every launch. The original handoff spec asked for
/// placement memory and it was never built.
/// </remarks>
/// <param name="Width">The window's width in device-independent pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="Left">Its left edge on the virtual desktop, or null if it was never placed.</param>
/// <param name="Top">Its top edge.</param>
/// <param name="IsMaximized">Whether it was left maximized, which outranks the bounds.</param>
/// <param name="IsRailCollapsed">Whether the navigation rail was left as a glyph column.</param>
/// <param name="Scale">How large everything is drawn, as a multiple of the designed size.</param>
public sealed record ShellLayout(
    double Width,
    double Height,
    double? Left,
    double? Top,
    bool IsMaximized,
    bool IsRailCollapsed,
    double Scale = 1)
{
    /// <summary>
    /// The sizes on offer, smallest first.
    /// </summary>
    /// <remarks>
    /// Steps, not a slider. The type scale, the row heights and the fixed columns were all
    /// designed against one of these, and a continuum of arbitrary multipliers is a continuum
    /// of layouts nobody has ever looked at. Somebody who wants more room has one press to find
    /// it and one press to undo it.
    ///
    /// It stopped at 130% until #266: past that, 150% and 200% of the whole window were only
    /// reachable through the operating system's display scaling, which also scales the game on
    /// the same machine. At 200% a 1920x1080 window lays the shell out in 960x540, so the V2
    /// shell's side panels have to collapse or scroll there rather than clip; the renders at
    /// 150% and 200% are the proof, not this list.
    /// </remarks>
    public static IReadOnlyList<double> Scales { get; } = [0.9, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0];

    /// <summary>The size the window has always opened at, for anybody who has not moved it.</summary>
    public static ShellLayout Default { get; } = new(1500, 900, null, null, false, false);

    /// <summary>
    /// The nearest offered size, for a value that has been stored or stepped past the end.
    /// </summary>
    /// <remarks>
    /// The settings file is one somebody can open, and a hand-typed 4 would draw the rail
    /// alone wider than a monitor with no way to press anything that would undo it.
    /// </remarks>
    public static double NearestScale(double scale) => double.IsFinite(scale)
        ? Scales.MinBy(offered => Math.Abs(offered - scale))
        : 1;

    /// <summary>The size one step larger or smaller, stopping at the ends.</summary>
    /// <remarks>
    /// Stopping rather than wrapping, for the same reason the floors do: a key held down should
    /// not come back round to where it started without saying so, and there is no size beyond
    /// the largest.
    /// </remarks>
    public static double StepScale(double from, int direction)
    {
        var index = Scales.ToList().IndexOf(NearestScale(from));
        return Scales[Math.Clamp(index + Math.Sign(direction), 0, Scales.Count - 1)];
    }

    /// <summary>The width the shell is laid out in: the window's own width at the chosen scale.</summary>
    /// <remarks>
    /// The scale is a layout transform on the whole shell, so at 200% a 1920-wide window is a
    /// 960-wide shell. Everything below is decided on this number, never on the window's.
    /// </remarks>
    public static double LayoutWidth(double windowWidth, double scale) =>
        double.IsFinite(windowWidth) && double.IsFinite(scale) && scale > 0
            ? windowWidth / scale
            : windowWidth;

    /// <summary>The narrowest shell whose navigation rail keeps its words beside its icons.</summary>
    /// <remarks>
    /// Below this the rail draws icons only, whatever was chosen, and goes back to words when
    /// there is room again. 168 of a 960-wide shell (1920 at 200%) is a sixth of the width for
    /// five words the icons already say, taken from a map that was left 390 wide.
    /// </remarks>
    public const double RailLabelsMinimumWidth = 1100;

    /// <summary>The narrowest shell whose top bar shows the product name and the freshness text.</summary>
    /// <remarks>
    /// Measured at 200%: the full bar needs about 1180, and a 960-wide shell cut the Ready pill,
    /// search and capture off the right-hand edge. The name and "Data updated ..." are the two
    /// things nobody acts on; the freshness keeps its icon and says the words as its tooltip.
    /// </remarks>
    public const double TopBarFullMinimumWidth = 1200;

    /// <summary>The narrowest shell whose top bar keeps the mode line, the freshness icon and the status words.</summary>
    /// <remarks>
    /// [#882 follow-up] 1120x720 at 200% is a 560-wide shell, the narrowest the window's minimum
    /// allows, and even the short bar cut search, capture and Ready off there. Below this the bar
    /// keeps the map, the raid clock, search, capture and the status dot, with half the gaps.
    /// </remarks>
    public const double TopBarShortMinimumWidth = 900;

    /// <summary>Whether a shell this wide keeps the short top bar rather than the compact one.</summary>
    public static bool TopBarFitsShort(double layoutWidth) =>
        !double.IsFinite(layoutWidth) || layoutWidth >= TopBarShortMinimumWidth;

    /// <summary>The narrowest map column whose control strip fits on one row.</summary>
    /// <remarks>
    /// About 1300 is what the clock, the presentation switches, the traffic chip, Follow, View
    /// and Layers need side by side on Customs. At 150% the strip was 1060 wide and the floor
    /// switches were squeezed to nothing; below this they get a row of their own.
    /// </remarks>
    public const double ControlStripOneRowMinimumWidth = 1250;

    /// <summary>The narrowest map column whose control strip spells the map modes out in full.</summary>
    /// <remarks>
    /// [#838] In raid at 1920x1080 the column is about 1330 and the strip wrapped on Windows: the
    /// clock, the traffic chip, the off-plan chip and "2D plan / Floor stack / 3D interior" left
    /// it under 30 pixels to spare headless and less than none on Windows, and the map lost 32
    /// pixels of height. "2D / Stack / 3D" gives back about 125. Above this width the full words
    /// fit with the same margin, so a wider window, or the Raid plan put away, keeps them.
    /// </remarks>
    public const double ControlStripFullModeLabelsMinimumWidth = 1480;

    /// <summary>Whether a map column this wide spells the map modes out in full.</summary>
    public static bool ControlStripFitsFullModeLabels(double columnWidth) =>
        !double.IsFinite(columnWidth) || columnWidth >= ControlStripFullModeLabelsMinimumWidth;

    /// <summary>Whether a shell this wide keeps the rail's words.</summary>
    public static bool RailFitsLabels(double layoutWidth) =>
        !double.IsFinite(layoutWidth) || layoutWidth >= RailLabelsMinimumWidth;

    /// <summary>Whether a shell this wide shows the whole top bar.</summary>
    public static bool TopBarFitsInFull(double layoutWidth) =>
        !double.IsFinite(layoutWidth) || layoutWidth >= TopBarFullMinimumWidth;

    /// <summary>Whether a map column this wide keeps its control strip on one row.</summary>
    public static bool ControlStripFitsOneRow(double columnWidth) =>
        !double.IsFinite(columnWidth) || columnWidth >= ControlStripOneRowMinimumWidth;

    /// <summary>What the main content keeps before a side panel beside it gives way.</summary>
    public const double MainContentMinimumWidth = 600;

    /// <summary>
    /// The most a side panel may take of the workspace it sits in.
    /// </summary>
    /// <remarks>
    /// Whatever leaves the main content <see cref="MainContentMinimumWidth"/>. The Raid plan's
    /// 360 was chosen at 1920 wide, where it is a fifth; at 200% the same 360 was 40% of an
    /// 870-wide cockpit and left the map 390 wide. At 100% this is over a thousand and the
    /// player's own dragged width is never touched. The panel's minimum still wins, because a
    /// panel narrower than it can hold is clipped rather than smaller — and it scrolls and can
    /// be put away with its handle, so nothing is lost below it.
    /// </remarks>
    public static double SidePanelMaximum(double workspaceWidth, double panelMinimum) =>
        double.IsFinite(workspaceWidth) && workspaceWidth > 0
            ? Math.Max(panelMinimum, workspaceWidth - MainContentMinimumWidth)
            : double.PositiveInfinity;

    /// <summary>
    /// Whether these bounds are worth restoring at all.
    /// </summary>
    /// <remarks>
    /// A window smaller than the shell's own minimum, or one whose size is not a number, is a
    /// file somebody has edited or a save that caught the window mid-collapse. Opening at the
    /// default is a recoverable answer; opening at nought by nought is not.
    /// </remarks>
    public bool IsUsable =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width >= 640 && Height >= 480;

    /// <summary>
    /// The same layout moved back onto a screen that exists.
    /// </summary>
    /// <remarks>
    /// The reason this is not simply "restore what was saved". Somebody who left the companion
    /// on a second monitor and then unplugged it would otherwise get a window positioned in
    /// empty space, with no title bar to drag it back by — the one failure that cannot be
    /// recovered from inside the application.
    ///
    /// The test is overlap rather than containment: a window deliberately hanging off the edge
    /// of a screen is somebody's arrangement, and moving it would be undoing a choice. Only a
    /// window with no usable overlap at all has nowhere to be.
    /// </remarks>
    public ShellLayout ClampTo(IReadOnlyList<ScreenBounds> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0 || Left is not { } left || Top is not { } top)
        {
            return this;
        }

        // A strip of the title bar is enough to drag the window back by, and demanding more
        // would move windows their owner had placed off an edge on purpose.
        const double Grabbable = 80;
        var reachable = screens.Any(screen =>
            left + Width - Grabbable > screen.Left &&
            left + Grabbable < screen.Right &&
            top + Height > screen.Top &&
            top + Grabbable < screen.Bottom);
        if (reachable)
        {
            return this;
        }

        // Onto the first screen at its corner plus a margin, rather than centred: centring a
        // saved size larger than the screen would put the title bar above the top edge, which
        // is the same unrecoverable state by a different route.
        var home = screens[0];
        return this with
        {
            Left = home.Left + 40,
            Top = home.Top + 40,
            Width = Math.Min(Width, home.Right - home.Left),
            Height = Math.Min(Height, home.Bottom - home.Top),
        };
    }
}

/// <summary>One screen's working area, in the same units the window's bounds are in.</summary>
public readonly record struct ScreenBounds(double Left, double Top, double Right, double Bottom);

/// <summary>Where the shell layout is kept.</summary>
public interface IShellLayoutStore
{
    Task<ShellLayout> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ShellLayout layout, CancellationToken cancellationToken);
}
