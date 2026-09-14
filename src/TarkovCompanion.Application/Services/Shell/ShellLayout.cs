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
public sealed record ShellLayout(
    double Width,
    double Height,
    double? Left,
    double? Top,
    bool IsMaximized,
    bool IsRailCollapsed)
{
    /// <summary>The size the window has always opened at, for anybody who has not moved it.</summary>
    public static ShellLayout Default { get; } = new(1500, 900, null, null, false, false);

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
