using System.Text.RegularExpressions;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Matches one line off the extract screen to one exit in the catalog.
/// </summary>
/// <remarks>
/// <para>
/// The screen and the catalog do not write an exit's name the same way, and the gap between
/// them was throwing away most of a screen. A raid on Woods matched one exit out of a list of
/// many, which is what prompted this.
/// </para>
/// <para>
/// Three differences are handled, and each is a comparison added rather than a threshold
/// lowered, so nothing that matched before stops matching:
/// </para>
/// <list type="bullet">
/// <item>The catalog qualifies a name in brackets -- "Power Line Passage (Flare)" -- and the
/// screen does not. Both forms are scored and the better one wins.</item>
/// <item>The screen puts a countdown or a distance after the name and the catalog has none.
/// A trailing run of digits, clock or units is dropped before scoring.</item>
/// <item>The screen abbreviates. Where every word of the shorter name appears in the longer in
/// the same order, and the shorter is substantial enough to mean something, that is a match on
/// its own terms rather than a near miss on an edit distance dominated by the missing word.</item>
/// </list>
/// <para>
/// And the one that was actually doing the damage: every row on the screen begins with a slot
/// label, "EXFIL01" or "TRANSIT02", on the same line as the name. Read verbatim off a real
/// screen:
/// </para>
/// <code>
/// EXFILO1 Friendship Bridge (Co-Op)
/// EXFIL@2 ZB-214
/// EXFIL@3 Bridge V-Ex
/// </code>
/// <para>
/// Eight to ten characters of dead weight against a catalog name that has none, and the shorter
/// the real name the more the prefix dominates the score. On the raid that prompted this, the
/// only row that matched was Power Line Passage -- the longest name on the screen, and the only
/// one where the prefix was small enough relative to the name to stay above the threshold. Note
/// the zero: it reads as a letter O or an at-sign far more often than as a digit, so the
/// pattern has to accept all three.
/// </para>
/// </remarks>
public static class ExtractLineMatcher
{
    /// <summary>
    /// The slot label every row on the extract screen begins with.
    /// </summary>
    /// <remarks>
    /// The digits are the part that goes wrong. Zero reads as a letter O or an at-sign on most
    /// rows of a real screen, so the character class has to take all of them, and the label may
    /// carry one or two of them. The trailing separator is optional because a row whose label
    /// ran into its name is still a row.
    /// </remarks>
    private static readonly Regex RowPrefix = new(
        @"^\s*(?<kind>EXFIL|TRANSIT)\s*[0-9Oo@]{1,2}\s*[:.\-]?\s*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    /// <summary>A trailing clock, count or distance the screen puts after a name.</summary>
    /// <remarks>
    /// Anchored at the end and required to be its own word, so an exit whose name ends in a
    /// number keeps it. Woods has ZB-014 and ZB-016, which differ only there.
    /// </remarks>
    private static readonly Regex TrailingMeasure = new(
        @"(?:\s+\d{1,2}\s*[:.]\s*\d{2}(?:\s*[:.]\s*\d{2})?|\s+\d+\s*(?:m|km|s|sec|secs|min|mins))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    /// <summary>The fewest characters an abbreviation has to have before it means anything.</summary>
    /// <remarks>
    /// "Gate" appears in more than one exit name on more than one map, so a four-character
    /// fragment matching by containment would send somebody to the wrong side of the map. Two
    /// words, or eight characters, is the point at which a fragment stops being ambiguous in
    /// practice against the names these maps actually have.
    /// </remarks>
    private const int ShortestMeaningfulFragment = 8;

    /// <summary>What a containment match is worth: strong, but under an exact reading.</summary>
    private const double ContainmentScore = 0.86;

    /// <summary>
    /// What kind of row this is, from the slot label the screen puts in front of it.
    /// </summary>
    public enum RowKind
    {
        /// <summary>No slot label, so this is not one of the panel's rows at all.</summary>
        Unlabelled,

        /// <summary>An exit from this map.</summary>
        Extract,

        /// <summary>A way to another map, which is not in any extract catalog.</summary>
        Transit,
    }

    /// <summary>
    /// Takes the slot label off the front of a row and says what it was.
    /// </summary>
    /// <remarks>
    /// Run on the raw line before normalisation. The label and the name share one line on
    /// screen and therefore one OCR line, and against a catalog name that has no label the
    /// label is dead weight that sinks every short name below the threshold.
    /// </remarks>
    public static (string Text, RowKind Kind) StripRowPrefix(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = RowPrefix.Match(line);
        if (!match.Success)
        {
            return (line.Trim(), RowKind.Unlabelled);
        }

        var kind = match.Groups["kind"].Value.StartsWith("TRANSIT", StringComparison.OrdinalIgnoreCase)
            ? RowKind.Transit
            : RowKind.Extract;
        var rest = line[match.Length..].Trim();
        // A label with nothing after it is a row whose name did not read. Reported as a row of
        // its kind with no text rather than as an unlabelled line, because "the screen had a
        // fifth exit and it did not read" is a different fact from "there were four".
        return (rest, kind);
    }

    /// <summary>
    /// Strips whatever the screen printed after the name.
    /// </summary>
    /// <remarks>
    /// Run before normalisation, on the raw line, because the normaliser folds punctuation away
    /// and a clock becomes indistinguishable from part of a name once its colon is gone.
    /// </remarks>
    public static string StripTrailingMeasure(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimEnd();
        var stripped = TrailingMeasure.Replace(trimmed, string.Empty).TrimEnd();
        // A line that is nothing but a measurement is a clock, not an exit whose name went
        // missing, and returning an empty string would have the caller discard it. Keep it
        // whole so it lands in the unmatched lines, where the raid timer reads it.
        return stripped.Length == 0 ? trimmed : stripped;
    }

    /// <summary>
    /// The catalog name with its bracketed qualifier removed, or null when it has none.
    /// </summary>
    public static string? WithoutQualifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var open = name.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0)
        {
            return null;
        }

        var trimmed = name[..open].TrimEnd();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// How well an observed line matches one catalog name, on the best of several readings.
    /// </summary>
    /// <param name="observed">The screen's line, already normalised for lookup.</param>
    /// <param name="normalizedFull">The catalog name, normalised.</param>
    /// <param name="normalizedShort">
    /// The catalog name without its bracketed qualifier, normalised, or null where it had none.
    /// </param>
    public static double Score(string observed, string normalizedFull, string? normalizedShort)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(normalizedFull);
        var best = FuzzyTextSimilarity.Score(observed, normalizedFull);
        if (normalizedShort is { Length: > 0 })
        {
            best = Math.Max(best, FuzzyTextSimilarity.Score(observed, normalizedShort));
        }

        if (Contains(observed, normalizedFull) ||
            (normalizedShort is { Length: > 0 } && Contains(observed, normalizedShort)))
        {
            best = Math.Max(best, ContainmentScore);
        }

        return best;
    }

    /// <summary>
    /// Whether one name is the other's words, in order, with words missing from the front or back.
    /// </summary>
    /// <remarks>
    /// "un roadblock" against "northern un roadblock" is the case. Word order matters, so
    /// "gate factory" does not match "factory gate": an exit read backwards is a misreading and
    /// treating it as a hit would be inventing a match out of two words in common.
    /// </remarks>
    public static bool Contains(string observed, string candidate)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(candidate);
        var (shorter, longer) = observed.Length <= candidate.Length ? (observed, candidate) : (candidate, observed);
        var shorterWords = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (shorterWords.Length == 0 ||
            (shorterWords.Length < 2 && shorter.Length < ShortestMeaningfulFragment))
        {
            return false;
        }

        var longerWords = longer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        foreach (var word in shorterWords)
        {
            var found = Array.IndexOf(longerWords, word, at);
            if (found < 0)
            {
                return false;
            }

            at = found + 1;
        }

        return true;
    }
}
