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

        var results = new List<QuestListLineMatch>();
        foreach (var rawLine in ocrLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.Trim();
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
                results.Add(new(line, QuestListLineKind.Unmatched, ranked));
                continue;
            }

            var top = ranked[0].Confidence;
            var margin = ranked.Length == 1 ? 1 : top - ranked[1].Confidence;
            var kind = top >= MatchThreshold && margin >= AmbiguityMargin
                ? QuestListLineKind.Matched
                : QuestListLineKind.Ambiguous;
            results.Add(new(line, kind, ranked));
        }

        return new(results);
    }

    internal static string Normalize(string value)
    {
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
