using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// What opens a locked quest (#288). The eligibility evaluator already says why a quest is
/// locked, in sentences written for a diagnostics pane ("Recorded player level 4 is below required
/// level 15."); this turns the same reasons into what to do about it ("reach level 15").
/// </summary>
public static class QuestUnlockPlanner
{
    public static IReadOnlyList<QuestUnlockStep> Steps(QuestSummaryReadModel task, Func<string, string?> nameOfTask)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(nameOfTask);
        var steps = new List<QuestUnlockStep>();
        foreach (var reason in task.Eligibility.Reasons)
        {
            switch (reason.Code)
            {
                case "player-level" when TrailingNumber(reason.Detail) is { } level:
                    steps.Add(new(QuestUnlockKind.Level, $"reach level {level}"));
                    break;
                case "prerequisite-state" or "unknown-prerequisite-state" when reason.RelatedTaskId is { } required:
                    var name = nameOfTask(required) ?? required;
                    var statuses = task.Prerequisites
                        .FirstOrDefault(prerequisite => string.Equals(prerequisite.RequiredTaskId, required, StringComparison.Ordinal))
                        ?.RequiredStatuses ?? [];
                    steps.Add(new(QuestUnlockKind.Quest, $"{Verb(statuses)} {name}", required));
                    break;
                default:
                    steps.Add(new(QuestUnlockKind.Other, reason.Detail.TrimEnd('.'), reason.RelatedTaskId));
                    break;
            }
        }

        // Quests first: they are what the player can act on tonight, a level is what arrives meanwhile.
        return [.. steps.OrderBy(step => step.Kind == QuestUnlockKind.Quest ? 0 : step.Kind == QuestUnlockKind.Level ? 1 : 2)];
    }

    /// <summary>"finish Debut · reach level 15", cut to <paramref name="maximum"/> steps with the rest counted.</summary>
    public static string Summarise(IReadOnlyList<QuestUnlockStep> steps, int maximum = 2)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var shown = steps.Take(maximum).Select(step => step.Label);
        return steps.Count > maximum
            ? string.Join(" · ", shown) + $" · +{steps.Count - maximum} more"
            : string.Join(" · ", shown);
    }

    /// <summary>
    /// A prerequisite that accepts "active" opens as soon as the other quest is taken; anything
    /// else has to be seen through. "failed" alone is the one that asks for the opposite.
    /// </summary>
    private static string Verb(IReadOnlyList<string> statuses)
    {
        var accepts = statuses.Select(status => status.ToLowerInvariant()).ToArray();
        return accepts switch
        {
            _ when accepts.Contains("active") => "start",
            _ when accepts.Length > 0 && accepts.All(status => status == "failed") => "fail",
            _ => "finish",
        };
    }

    private static int? TrailingNumber(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsAsciiDigit(text[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]))
        {
            start--;
        }

        return start < end && int.TryParse(text.AsSpan(start, end - start), out var number) ? number : null;
    }
}

/// <summary>
/// Which map to run next (#288): the one where a single raid moves the most quests along, then
/// the one with the most objectives to do. Objectives that can be done anywhere have no map of
/// their own and are never the suggestion.
/// </summary>
public static class NextRaidPlanner
{
    public static IReadOnlyList<NextRaidCandidate> Rank(IEnumerable<NextRaidCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return
        [
            .. candidates
                .Where(candidate => candidate.MapKey.Length > 0 && candidate.Objectives > 0)
                .OrderByDescending(candidate => candidate.Quests)
                .ThenByDescending(candidate => candidate.Objectives)
                .ThenBy(candidate => candidate.MapLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.MapKey, StringComparer.Ordinal),
        ];
    }

    public static NextRaidCandidate? Suggest(IEnumerable<NextRaidCandidate> candidates) =>
        Rank(candidates).FirstOrDefault();
}
