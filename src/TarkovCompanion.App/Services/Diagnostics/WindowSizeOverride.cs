using System.Globalization;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>A deterministic startup size requested by a desktop verification tool.</summary>
/// <remarks>
/// The Windows gallery sizes the real native window after it opens. Naming the same size here
/// tells the application that the tool owns placement for this launch, so an asynchronous saved
/// placement restore cannot overwrite the gallery's later native resize.
/// </remarks>
public sealed record WindowSizeOverride(int Width, int Height)
{
    public static WindowSizeOverride Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var lowerSeparator = value.IndexOf('x');
        var upperSeparator = value.IndexOf('X');
        var separator = lowerSeparator >= 0 ? lowerSeparator : upperSeparator;
        var lastSeparator = Math.Max(value.LastIndexOf('x'), value.LastIndexOf('X'));
        if (separator <= 0
            || separator != lastSeparator
            || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            throw new ArgumentException("--window-size requires positive dimensions such as 1920x1080.", nameof(value));
        }

        return new(width, height);
    }
}
