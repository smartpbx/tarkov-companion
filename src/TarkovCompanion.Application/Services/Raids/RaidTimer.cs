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

    /// <summary>
    /// The raid clock as every surface shows it: "20:56 left", or "14:03 elapsed" when no length is known.
    /// </summary>
    /// <remarks>
    /// Reported on 2026-09-20 as three clocks on one screen: the top bar said "14:03 elapsed" while
    /// the Raid plan card and the strip under the map both said "0:20:56". None was wrong. The top
    /// bar counted up from the raid's start and knew nothing of the map's length; the other two
    /// counted down from it, in another format, without a word saying which way they ran. Time left
    /// is the number a player acts on and the one the game itself draws, so it is the one shown,
    /// everywhere, from this one place, and it always says "left" or "elapsed". Minutes and
    /// seconds, because no raid runs longer than an hour.
    /// </remarks>
    public string ClockText(DateTimeOffset? startedUtc, DateTimeOffset nowUtc)
    {
        if (Remaining is { } left)
        {
            return $"{Minutes(left)} left";
        }

        return startedUtc is { } started ? $"{Minutes(nowUtc - started)} elapsed" : string.Empty;
    }

    private static string Minutes(TimeSpan span)
    {
        var shown = span > TimeSpan.Zero ? span : TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)shown.TotalMinutes:00}:{shown.Seconds:00}");
    }

    /// <summary>Which of the two this is, so a count is not read as a reading.</summary>
    public string Detail => Basis switch
    {
        RaidTimeBasis.Observed => "from screenshot",
        RaidTimeBasis.Counted => "counted from start",
        _ => "open extracts or set by hand",
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
    /// <summary>Whether this side may count a full raid from the observed player start.</summary>
    /// <remarks>
    /// A PMC confirmation begins the raid. A scav confirmation is only when that scav joined a
    /// raid already in progress, so even a catalog scav duration cannot turn it into a remaining
    /// clock. A start entered by the player is allowed because its basis stays visible as manual.
    /// </remarks>
    public static bool CanCountFromStart(string? side, bool startSetByHand = false) =>
        startSetByHand || side?.Trim().ToLowerInvariant() is "pmc" or "usec" or "bear";

    /// <summary>
    /// How long a raid runs on this map for the side it is being run as.
    /// </summary>
    /// <remarks>
    /// A scav raid starts partway through and is the shorter of the two. Both the shell and the
    /// coordinator read <c>PmcRaidDuration</c> whatever the side, so a scav counting down from
    /// the PMC length was promised time nobody has — on Customs, forty minutes instead of about
    /// twenty-five.
    ///
    /// A side that is not known, or a catalog that does not state that side's length, returns
    /// null rather than the other side's number. Null is a visible unknown; the wrong duration
    /// is a confident wrong answer that counts down convincingly.
    ///
    /// The side arrives as the string the logs and the wire carry, so it is matched
    /// case-insensitively rather than parsed into an enum nobody else here has.
    /// Selecting that duration does not make a scav join time a raid start: <see
    /// cref="ResolveForRaid"/> uses it only when a screenshot or hand entry supplies the clock.
    /// </remarks>
    public static TimeSpan? LengthFor(string? side, TimeSpan? pmc, TimeSpan? scav) =>
        side?.Trim().ToLowerInvariant() switch
        {
            "scav" or "savage" => scav,
            "pmc" or "usec" or "bear" => pmc,
            _ => null,
        };

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

    /// <summary>
    /// Whether this line is the game saying it does not know, rather than a time.
    /// </summary>
    /// <remarks>
    /// An exit whose timer is not yet decided is drawn "??:??:??", and OCR renders the question
    /// marks as digits — "22:22:22" is what a real screen produced. That was rejected only
    /// because it exceeds the one-hour cap, which is luck rather than a rule: the same marker
    /// read with a leading zero gives "0:22:22", which is a perfectly plausible raid clock and
    /// would have been taken as one.
    ///
    /// So a question mark anywhere on the line is conclusive: whatever else is on that row, it
    /// is not a time the game is claiming.
    ///
    /// Deliberately *only* that. The first attempt at this also rejected a clock whose digits
    /// were all the same character, on the theory that "0:22:22" was the same marker read with
    /// a leading zero. It is also a completely ordinary raid clock — twenty-two minutes and
    /// twenty-two seconds — and a raid passes through it every time. Throwing away a real
    /// reading once a raid to catch a marker that the one-hour cap already rejects at
    /// "22:22:22" is a bad trade, so the heuristic is gone and only the certain test remains.
    /// </remarks>
    private static bool IsUnknownMarker(string line) => line.Contains('?', StringComparison.Ordinal);

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
            if (string.IsNullOrWhiteSpace(line) || IsUnknownMarker(line))
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

    /// <summary>Resolves a live raid without treating a scav's join time as the raid's start.</summary>
    public static RaidTimeRemaining ResolveForRaid(
        (TimeSpan Clock, DateTimeOffset ReadUtc)? observed,
        DateTimeOffset? startedUtc,
        TimeSpan? length,
        string? side,
        bool startSetByHand,
        DateTimeOffset nowUtc) =>
        Resolve(
            observed,
            startedUtc,
            CanCountFromStart(side, startSetByHand) ? length : null,
            nowUtc);

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
