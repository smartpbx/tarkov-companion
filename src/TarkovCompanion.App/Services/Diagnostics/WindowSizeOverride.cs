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

    /// <summary>
    /// [#858] The client size that makes the whole window, frame included, this size.
    /// </summary>
    /// <remarks>
    /// The gallery's MoveWindow sizes the outer window (GetWindowRect, invisible resize borders
    /// included), while Window.Width sizes the client: 1920 asked of each gave a 1904 px and a
    /// 1920 px client. Whichever landed last won, so 2 of 41 captures per Windows run came out
    /// 16 px wider, the Raid plan 15 px further right and the map framed differently (15.7 to
    /// 16.9% pixel diffs). Asking for the frame's size instead makes both agree, whatever the order.
    /// A window without a frame (frame no larger than the client) keeps the requested size.
    /// </remarks>
    public (double Width, double Height) ClientSizeFor(double frameWidth, double frameHeight, double clientWidth, double clientHeight)
    {
        static double Fit(int requested, double frame, double client) =>
            frame > client && double.IsFinite(frame) && double.IsFinite(client)
                ? Math.Max(1, requested - (frame - client))
                : requested;

        return (Fit(Width, frameWidth, clientWidth), Fit(Height, frameHeight, clientHeight));
    }
}
