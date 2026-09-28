using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.Shell;

public enum GameWindowPresentationMode
{
    Windowed = 1,
    Borderless,
}

/// <summary>A calibration belongs to one monitor/DPI, game-window resolution and window mode.</summary>
public sealed record CaptureTargetCalibrationKey(
    string MonitorKey,
    int WindowWidthPixels,
    int WindowHeightPixels,
    GameWindowPresentationMode WindowMode);

/// <summary>Pixels removed from the externally visible game-window rectangle.</summary>
public sealed record CaptureTargetInsets(int Left, int Top, int Right, int Bottom)
{
    public static CaptureTargetInsets None { get; } = new(0, 0, 0, 0);
}

public sealed record CaptureTargetCalibrationProfile(
    CaptureTargetCalibrationKey Key,
    CaptureTargetInsets Insets,
    DateTimeOffset UpdatedUtc);

public sealed record CaptureTargetCalibrationState(IReadOnlyList<CaptureTargetCalibrationProfile> Profiles)
{
    public static CaptureTargetCalibrationState Empty { get; } = new([]);
}

public interface ICaptureTargetCalibrationStore
{
    Task<CaptureTargetCalibrationState> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(CaptureTargetCalibrationProfile profile, CancellationToken cancellationToken);

    Task DeleteAsync(CaptureTargetCalibrationKey key, CancellationToken cancellationToken);
}

public sealed record CaptureTargetPreview(
    CaptureTargetCalibrationKey Key,
    PixelRect WindowBounds,
    PixelRect? CaptureBounds,
    string? RefusalCode)
{
    public bool IsValid => CaptureBounds is not null;
}

/// <summary>Pure, bounded target math. It observes a visible window and never reads game memory or input.</summary>
public static class CaptureTargetCalibration
{
    public const int MaximumInsetPixels = 4096;
    public const int MinimumCaptureDimensionPixels = 64;

    public static CaptureTargetCalibrationKey KeyFor(
        DisplayDescriptor display,
        WindowDescriptor window,
        GameWindowPresentationMode mode)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(window);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return new(
            DesktopWindowPlacement.MonitorKey(display),
            window.Bounds.Width,
            window.Bounds.Height,
            mode);
    }

    public static CaptureTargetPreview Preview(
        DisplayDescriptor display,
        WindowDescriptor window,
        GameWindowPresentationMode mode,
        CaptureTargetInsets insets)
    {
        ArgumentNullException.ThrowIfNull(insets);
        var key = KeyFor(display, window, mode);
        if (window.IsMinimized)
        {
            return new(key, window.Bounds, null, "capture.window-minimized");
        }

        if (!Valid(insets.Left) || !Valid(insets.Top) || !Valid(insets.Right) || !Valid(insets.Bottom))
        {
            return new(key, window.Bounds, null, "capture.insets-out-of-range");
        }

        var width = window.Bounds.Width - insets.Left - insets.Right;
        var height = window.Bounds.Height - insets.Top - insets.Bottom;
        if (width < MinimumCaptureDimensionPixels || height < MinimumCaptureDimensionPixels)
        {
            return new(key, window.Bounds, null, "capture.target-too-small");
        }

        var capture = new PixelRect(
            checked(window.Bounds.X + insets.Left),
            checked(window.Bounds.Y + insets.Top),
            width,
            height);
        if (!Contains(window.Bounds, capture))
        {
            return new(key, window.Bounds, null, "capture.target-outside-window");
        }

        return new(key, window.Bounds, capture, null);
    }

    public static bool IsUsable(CaptureTargetCalibrationProfile profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Key.MonitorKey) ||
            profile.Key.MonitorKey.Length > 256 || profile.Key.WindowWidthPixels < MinimumCaptureDimensionPixels ||
            profile.Key.WindowHeightPixels < MinimumCaptureDimensionPixels || !Enum.IsDefined(profile.Key.WindowMode) ||
            profile.UpdatedUtc == default || profile.UpdatedUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var insets = profile.Insets;
        return Valid(insets.Left) && Valid(insets.Top) && Valid(insets.Right) && Valid(insets.Bottom) &&
               profile.Key.WindowWidthPixels - insets.Left - insets.Right >= MinimumCaptureDimensionPixels &&
               profile.Key.WindowHeightPixels - insets.Top - insets.Bottom >= MinimumCaptureDimensionPixels;
    }

    private static bool Valid(int value) => value is >= 0 and <= MaximumInsetPixels;

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
}
