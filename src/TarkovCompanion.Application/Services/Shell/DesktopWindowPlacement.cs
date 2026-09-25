using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.Shell;

/// <summary>A normal window rectangle remembered for one physical display configuration.</summary>
/// <remarks>
/// Width and height are physical pixels, not Avalonia device-independent units. A 1,200-pixel-wide
/// companion therefore remains about 1,200 physical pixels after it crosses from a 100% display to
/// a 150% display, instead of becoming half a monitor wider. Offsets are relative to the work area,
/// so rearranging monitors in Windows does not strand a valid saved rectangle at an old desktop coordinate.
/// </remarks>
public sealed record MonitorWindowPlacement(
    string MonitorKey,
    double WidthPixels,
    double HeightPixels,
    double LeftOffsetPixels,
    double TopOffsetPixels,
    bool IsMaximized);

public sealed record DesktopWindowPlacementState(
    string? ActiveMonitorKey,
    IReadOnlyList<MonitorWindowPlacement> Monitors)
{
    public static DesktopWindowPlacementState Empty { get; } = new(null, []);
}

/// <summary>A placement to apply to an Avalonia window.</summary>
public sealed record WindowPlacementTarget(
    string MonitorKey,
    string MonitorId,
    double Width,
    double Height,
    int Left,
    int Top,
    bool IsMaximized);

public interface IDesktopWindowPlacementStore
{
    Task<DesktopWindowPlacementState> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(DesktopWindowPlacementState state, CancellationToken cancellationToken);
}

/// <summary>Pure placement math shared by startup, hot-plug recovery and Setup's move buttons.</summary>
public static class DesktopWindowPlacement
{
    public const double MinimumWidth = 360;
    public const double MinimumHeight = 480;

    public static string MonitorKey(DisplayDescriptor display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return $"{display.Id}|{display.Bounds.Width}x{display.Bounds.Height}";
    }

    public static DisplayDescriptor? DisplayAt(
        IReadOnlyList<DisplayDescriptor> displays,
        double left,
        double top,
        double widthPixels,
        double heightPixels)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var centreX = left + (widthPixels / 2);
        var centreY = top + (heightPixels / 2);
        return displays.FirstOrDefault(display => Contains(display.Bounds, centreX, centreY));
    }

    public static MonitorWindowPlacement Capture(
        DisplayDescriptor display,
        double width,
        double height,
        double left,
        double top,
        bool isMaximized)
    {
        ArgumentNullException.ThrowIfNull(display);
        var scale = ValidScale(display.Scale);
        var work = display.UsableBounds;
        return new(
            MonitorKey(display),
            width * scale,
            height * scale,
            left - work.X,
            top - work.Y,
            isMaximized);
    }

    /// <param name="frameWidthPixels">The window's borders beside the client, in the display's pixels.</param>
    /// <param name="frameHeightPixels">Its title bar and borders above and below the client.</param>
    /// <remarks>
    /// [#881 follow-up] The size kept is the client's and the position is the frame's, so the
    /// client is fitted into the work area less the frame. Fitting the client alone put a window
    /// saved at the screen's size a title bar lower than the work area, with the rail's gear under
    /// the taskbar. #882 fixed that in the preview store's restore, but this controller runs after
    /// it and put the unfitted rectangle back on every launch that had a saved placement.
    /// </remarks>
    public static WindowPlacementTarget Restore(
        MonitorWindowPlacement placement,
        DisplayDescriptor display,
        double frameWidthPixels = 0,
        double frameHeightPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(display);
        var scale = ValidScale(display.Scale);
        var work = display.UsableBounds;
        var frameWidth = double.IsFinite(frameWidthPixels) ? Math.Clamp(frameWidthPixels, 0, work.Width / 2.0) : 0;
        var frameHeight = double.IsFinite(frameHeightPixels) ? Math.Clamp(frameHeightPixels, 0, work.Height / 2.0) : 0;
        var roomWidth = work.Width - frameWidth;
        var roomHeight = work.Height - frameHeight;
        var minimumWidthPixels = Math.Min(roomWidth, MinimumWidth * scale);
        var minimumHeightPixels = Math.Min(roomHeight, MinimumHeight * scale);
        var widthPixels = Math.Clamp(FiniteOr(placement.WidthPixels, minimumWidthPixels), minimumWidthPixels, roomWidth);
        var heightPixels = Math.Clamp(FiniteOr(placement.HeightPixels, minimumHeightPixels), minimumHeightPixels, roomHeight);
        var wantedLeft = work.X + FiniteOr(placement.LeftOffsetPixels, 40);
        var wantedTop = work.Y + FiniteOr(placement.TopOffsetPixels, 40);
        var left = Math.Clamp(wantedLeft, work.X, work.X + roomWidth - widthPixels);
        var top = Math.Clamp(wantedTop, work.Y, work.Y + roomHeight - heightPixels);

        return new(
            MonitorKey(display),
            display.Id,
            widthPixels / scale,
            heightPixels / scale,
            (int)Math.Round(left),
            (int)Math.Round(top),
            placement.IsMaximized);
    }

    public static DisplayDescriptor? PreferredDisplay(
        IReadOnlyList<DisplayDescriptor> displays,
        string? wantedMonitorKey)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return displays.FirstOrDefault(display => MonitorKey(display) == wantedMonitorKey)
               ?? displays.FirstOrDefault(display => display.IsPrimary)
               ?? displays.FirstOrDefault();
    }

    public static MonitorWindowPlacement ForFallback(
        MonitorWindowPlacement? previous,
        DisplayDescriptor display,
        double defaultWidth,
        double defaultHeight)
    {
        ArgumentNullException.ThrowIfNull(display);
        var scale = ValidScale(display.Scale);
        return new(
            MonitorKey(display),
            previous?.WidthPixels ?? defaultWidth * scale,
            previous?.HeightPixels ?? defaultHeight * scale,
            40,
            40,
            previous?.IsMaximized ?? false);
    }

    private static bool Contains(PixelRect bounds, double x, double y) =>
        x >= bounds.X && x < bounds.X + bounds.Width &&
        y >= bounds.Y && y < bounds.Y + bounds.Height;

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;

    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
