namespace TarkovCompanion.Application.Services.Quests;

/// <summary>Merges overlapping quest-list screenfuls without showing the same quest twice.</summary>
/// <remarks>
/// A long TASKS list has to be scrolled, so adjacent screenshots normally share several rows.
/// Confirmed matches deduplicate by catalog task id. Uncertain and unmatched rows have no quest
/// identity yet, so only equivalent OCR evidence is collapsed; distinct uncertainty is retained
/// for the player instead of guessed away.
/// </remarks>
public sealed class QuestListMatchMerger
{
    public QuestListMatchResult Merge(IEnumerable<QuestListLineMatch> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var merged = new List<QuestListLineMatch>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var key = Key(line);
            if (!positions.TryGetValue(key, out var existingIndex))
            {
                positions[key] = merged.Count;
                merged.Add(line);
                continue;
            }

            if (BestConfidence(line) > BestConfidence(merged[existingIndex]))
            {
                merged[existingIndex] = line;
            }
        }

        return new(merged);
    }

    private static string Key(QuestListLineMatch line) => line.Kind switch
    {
        QuestListLineKind.Matched when line.Confirmed is { } matched => $"matched:{matched.TaskId}",
        QuestListLineKind.Ambiguous =>
            $"ambiguous:{QuestListMatcher.Normalize(line.OcrLine)}:{string.Join(',', line.Candidates.Select(candidate => candidate.TaskId))}",
        _ => $"unmatched:{QuestListMatcher.Normalize(line.OcrLine)}",
    };

    private static double BestConfidence(QuestListLineMatch line) =>
        line.Candidates.FirstOrDefault()?.Confidence ?? 0;
}
