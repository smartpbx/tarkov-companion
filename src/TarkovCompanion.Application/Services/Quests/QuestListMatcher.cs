using System.Globalization;
using System.Text;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public enum QuestListLineKind
{
    Matched,
    Ambiguous,
    Unmatched,
}

public sealed record QuestListCandidate(
    string TaskId,
    string QuestName,
    double Confidence);

public sealed record QuestListLineMatch(
    string OcrLine,
    QuestListLineKind Kind,
    IReadOnlyList<QuestListCandidate> Candidates)
{
    public QuestListCandidate? Confirmed => Kind == QuestListLineKind.Matched
        ? Candidates.FirstOrDefault()
        : null;
}

public sealed record QuestListMatchResult(IReadOnlyList<QuestListLineMatch> Lines)
{
    public IReadOnlyList<QuestListLineMatch> Matched =>
        Lines.Where(line => line.Kind == QuestListLineKind.Matched).ToArray();

    public IReadOnlyList<QuestListLineMatch> Ambiguous =>
        Lines.Where(line => line.Kind == QuestListLineKind.Ambiguous).ToArray();

    public IReadOnlyList<QuestListLineMatch> Unmatched =>
        Lines.Where(line => line.Kind == QuestListLineKind.Unmatched).ToArray();
}

/// <summary>Matches the OCR lines from a quest list to catalog quest names.</summary>
/// <remarks>
/// Quest names are short and several differ only by a part number. A broad contains search
/// silently turns a damaged line into the wrong quest, so matching uses the whole line and
/// keeps close runners-up for the player to resolve. Common glyph swaps are made equivalent
/// before edit distance is measured; missing characters still carry a cost and therefore lower
/// the confidence instead of disappearing from the evidence.
/// </remarks>
public sealed class QuestListMatcher
{
    private const double MatchThreshold = 0.78;
    private const double CandidateThreshold = 0.50;
    private const double AmbiguityMargin = 0.06;
    private const int CandidateLimit = 5;
    private const int MaximumTagLength = 32;

    public QuestListMatchResult Match(
        IEnumerable<string> ocrLines,
        IEnumerable<QuestTaskDefinition> catalogTasks)
    {
        ArgumentNullException.ThrowIfNull(ocrLines);
        ArgumentNullException.ThrowIfNull(catalogTasks);

        var catalog = catalogTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Name))
            .Select(task => new CatalogName(task, Normalize(task.Name)))
            .Where(candidate => candidate.Normalized.Length > 0)
            .ToArray();

        var lines = ocrLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();
        var results = new List<QuestListLineMatch>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var single = Score(line, catalog);
            if (index + 1 < lines.Length)
            {
                var next = lines[index + 1];
                var joined = $"{line} {next}";
                if (IsWrappedTail(line, next))
                {
                    results.Add(Score(joined, catalog));
                    index++;
                    continue;
                }

                // A title wrapped somewhere other than before "Part N": join only when neither
                // half is a quest on its own and together they are one, clearly.
                if (single.Kind != QuestListLineKind.Matched)
                {
                    var together = Score(joined, catalog);
                    if (together.Kind == QuestListLineKind.Matched &&
                        Score(next, catalog).Kind != QuestListLineKind.Matched &&
                        together.Candidates[0].Confidence > TopConfidence(single))
                    {
                        results.Add(together);
                        index++;
                        continue;
                    }
                }
            }

            results.Add(single);
        }

        return new(results);
    }

    private static double TopConfidence(QuestListLineMatch match) =>
        match.Candidates.Count == 0 ? 0 : match.Candidates[0].Confidence;

    private static QuestListLineMatch Score(string line, IReadOnlyList<CatalogName> catalog)
    {
        var normalized = Normalize(line);
        var ranked = catalog
            .Select(candidate => new QuestListCandidate(
                candidate.Task.Id,
                candidate.Task.Name,
                Similarity(normalized, candidate.Normalized)))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.QuestName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TaskId, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .ToArray();

        if (ranked.Length == 0 || ranked[0].Confidence < CandidateThreshold)
        {
            return new(line, QuestListLineKind.Unmatched, ranked);
        }

        var top = ranked[0].Confidence;
        var margin = ranked.Length == 1 ? 1 : top - ranked[1].Confidence;
        // A line equal to exactly one name, once normalized, is that quest even when a sibling
        // differs by one character. Without this, "Uninvited Guests - Part 1" read perfectly
        // still tied with Part 2 on the margin and was put to the player as a guess (#989).
        var exact = top >= 1 && (ranked.Length == 1 || ranked[1].Confidence < 1);
        var kind = exact || (top >= MatchThreshold && margin >= AmbiguityMargin)
            ? QuestListLineKind.Matched
            : QuestListLineKind.Ambiguous;
        return new(line, kind, ranked);
    }

    internal static string Normalize(string value)
    {
        var bracket = value.LastIndexOf('[');
        if (bracket >= 0 &&
            value.IndexOf(']', bracket) is var close && close > bracket &&
            string.IsNullOrWhiteSpace(value[(close + 1)..]))
        {
            // Seasonal builds relabel the same event quest (for example [Season PvP] versus
            // the catalog's [PVP ZONE]). The stable quest name before the trailing tag is the
            // identity; the changing event label is not a spelling difference.
            value = value[..bracket];
        }

        value = WithoutLeadingTag(value);

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (!char.IsLetterOrDigit(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            var lowered = char.ToLowerInvariant(character);
            builder.Append(lowered switch
            {
                '0' => 'o',
                '1' or 'i' => 'l',
                _ => lowered,
            });
        }

        // OCR commonly splits the two stems of an m into rn. Treating that as a spelling
        // difference would cost two edits even though it is one visual confusion.
        return builder.ToString().Replace("rn", "m", StringComparison.Ordinal);
    }

    /// <summary>Drops a leading event tag such as "[KORD BREACH] ".</summary>
    /// <remarks>
    /// Season quests carry their tag in front, and OCR damages it in ways edit distance cannot
    /// forgive on a short name: "[KORD BREACH] Unanswered Calls" came back as
    /// "KORD BREACH] Unanswered Calls" and "KORO BREACH] Uninvited Guests" (#989), the "[" lost
    /// and D read as O. The closing bracket survives, so everything up to it is the tag. The tag
    /// is a label for the season, not part of the quest's identity, exactly like the trailing
    /// [PVP ZONE] tag above; it is removed from catalog names the same way.
    /// </remarks>
    private static string WithoutLeadingTag(string value)
    {
        var close = value.IndexOf(']');
        if (close <= 0 || close > MaximumTagLength)
        {
            return value;
        }

        var tag = value[..close];
        var rest = value[(close + 1)..];
        if (tag.LastIndexOf('[') > 0 || string.IsNullOrWhiteSpace(rest) || !tag.Any(char.IsLetter))
        {
            return value;
        }

        return rest;
    }

    /// <summary>Whether an OCR line is the tail of a title the quest list wrapped.</summary>
    /// <remarks>
    /// A long title wraps in the TASKS list, so "KORD BREACH] Uninvited Guests" and "- Part 1"
    /// arrive as two lines (#989). On its own the first is a tie between Part 1 and Part 2 and the
    /// second matches nothing.
    /// </remarks>
    private static bool IsWrappedTail(string previous, string line) =>
        PartTail.IsMatch(line) ||
        previous.EndsWith('-') || previous.EndsWith('\u2013') || previous.EndsWith('\u2014');

    private static readonly System.Text.RegularExpressions.Regex PartTail = new(
        @"^[-\u2013\u2014]?\s*part\s*[0-9lio]{1,2}\.?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        var distance = EditDistance(left, right);
        return Math.Clamp(1d - ((double)distance / Math.Max(left.Length, right.Length)), 0, 1);
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record CatalogName(QuestTaskDefinition Task, string Normalized);
}
