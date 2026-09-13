using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>Where a raid's remaining time came from, because the two are not equal claims.</summary>
public enum RaidTimeBasis
{
    /// <summary>Nothing to go on.</summary>
    Unknown,

    /// <summary>Counted from when the game confirmed the raid, against the map's own length.</summary>
    Counted,

    /// <summary>Read off the timer in a screenshot, which is the game's own number.</summary>
    Observed,
}

/// <summary>How long is left, and how that is known.</summary>
public sealed record RaidTimeRemaining(TimeSpan? Remaining, RaidTimeBasis Basis)
{
    public static RaidTimeRemaining Unknown { get; } = new(null, RaidTimeBasis.Unknown);

    /// <summary>The clock, in the shape the game shows it.</summary>
    public string Display => Remaining is not { } left
        ? "Unknown"
        : left <= TimeSpan.Zero
            ? "0:00:00"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}");

    /// <summary>Which of the two this is, so a count is not read as a reading.</summary>
    public string Detail => Basis switch
    {
        RaidTimeBasis.Observed => "From the extract list",
        RaidTimeBasis.Counted => "Counted from the start",
        _ => "No raid start observed",
    };
}

/// <summary>
/// How long is left in a raid.
/// </summary>
/// <remarks>
/// Two sources and they are not equal. Counting from the game's own confirmation against the
/// map's stated length works from the moment a raid begins and needs nothing from the player,
/// but it drifts and it knows nothing at all when the companion was started mid-raid.
///
/// The timer on the extract list screen is the game's own number. It is exact, and it arrives
/// only when the player photographs that screen, so it is a correction rather than a source.
///
/// A reading always wins over a count, and the answer says which it is. A count presented as a
/// reading would be a claim the companion cannot make.
/// </remarks>
public static partial class RaidTimer
{
    /// <summary>
    /// The raid clock as the game draws it: hours, minutes, seconds.
    /// </summary>
    /// <remarks>
    /// Anchored to a word boundary at each end so that a coordinate or a price cannot be read
    /// as a time. Minutes and seconds are two digits and hours are one or two, which is what
    /// the game writes and what the OCR returns: "0:28:10" verbatim from a real screenshot.
    /// </remarks>
    [GeneratedRegex(@"\b(\d{1,2}):([0-5]\d):([0-5]\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Clock();

    /// <summary>The longest a raid runs, as a bound on what can be read as one.</summary>
    /// <remarks>
    /// No map runs longer than an hour, so a larger reading is something else on the screen
    /// that happens to be shaped like a clock, and taking it would replace a good count with
    /// nonsense.
    /// </remarks>
    private static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>
    /// Reads the raid clock out of what was recognised on the extract list.
    /// </summary>
    /// <remarks>
    /// The largest match wins. The screen carries the raid timer and, on some maps, a second
    /// shorter countdown beside an exit; the raid is the longer of the two by construction.
    /// </remarks>
    public static TimeSpan? Read(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        TimeSpan? best = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (var match in Clock().EnumerateMatches(line))
            {
                var text = line.AsSpan(match.Index, match.Length);
                if (!TimeSpan.TryParseExact(text, @"h\:mm\:ss", CultureInfo.InvariantCulture, out var value) ||
                    value > Longest ||
                    value <= TimeSpan.Zero)
                {
                    continue;
                }

                if (best is null || value > best)
                {
                    best = value;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// How long is left now, from whichever source has the better claim.
    /// </summary>
    /// <param name="observed">The clock last read off a screenshot, and when it was read.</param>
    /// <param name="startedUtc">When the game confirmed the raid, where that was seen.</param>
    /// <param name="length">How long this map's raids run, where the catalog says.</param>
    /// <param name="nowUtc">Now.</param>
    public static RaidTimeRemaining Resolve(
        (TimeSpan Clock, DateTimeOffset ReadUtc)? observed,
        DateTimeOffset? startedUtc,
        TimeSpan? length,
        DateTimeOffset nowUtc)
    {
        if (observed is { } reading)
        {
            var since = nowUtc - reading.ReadUtc;
            var left = reading.Clock - (since > TimeSpan.Zero ? since : TimeSpan.Zero);
            return new(left > TimeSpan.Zero ? left : TimeSpan.Zero, RaidTimeBasis.Observed);
        }

        if (startedUtc is { } started && length is { } total && total > TimeSpan.Zero)
        {
            var elapsed = nowUtc - started;
            var left = total - (elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero);
            return new(left > TimeSpan.Zero ? left : TimeSpan.Zero, RaidTimeBasis.Counted);
        }

        return RaidTimeRemaining.Unknown;
    }
}
