using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>One raid in the evening's plan.</summary>
/// <param name="MapKey">The map id, or empty for a hideout loot run that can be on any map.</param>
/// <param name="QuestNames">The quests this raid moves, most objectives first.</param>
/// <param name="Objectives">How many objectives the plan assumes this raid gets done: an estimate.</param>
public sealed record SessionPlanRaid(
    int Number,
    string MapKey,
    string MapLabel,
    IReadOnlyList<string> QuestNames,
    int Objectives)
{
    public bool IsLootRun => MapKey.Length == 0;
}

/// <summary>
/// The evening's raids, in order, and the numbers they were counted from. Every count here is an
/// estimate: the plan assumes each raid gets a few objectives done and ends in about the time the
/// player's own raids have taken.
/// </summary>
/// <param name="PerRaid">Raid plus the stash and queue around it.</param>
/// <param name="PerRaidMeasured">True when <paramref name="PerRaid"/> comes from the player's own raids.</param>
/// <param name="RaidsPlayed">Raids already finished this session.</param>
/// <param name="Remaining">Session time left.</param>
public sealed record SessionPlan(
    IReadOnlyList<SessionPlanRaid> Raids,
    TimeSpan PerRaid,
    bool PerRaidMeasured,
    int RaidsPlayed,
    TimeSpan Remaining)
{
    public static SessionPlan Empty { get; } = new([], SessionPlanner.DefaultPerRaid, false, 0, TimeSpan.Zero);
}

/// <summary>Where the player is in tonight's session, read from their own raid history.</summary>
/// <param name="StartedUtc">The first raid of this session, or null before one has started.</param>
/// <param name="MedianRaid">The median length of the player's recent raids, or null below three.</param>
public sealed record SessionProgress(DateTimeOffset? StartedUtc, int RaidsPlayed, TimeSpan Elapsed, TimeSpan? MedianRaid)
{
    public static SessionProgress Fresh { get; } = new(null, 0, TimeSpan.Zero, null);
}

/// <summary>
/// [#712 2-3] "I have two hours": four to six raids in order, for task progress first and hideout
/// loot after. It extends <see cref="NextRaidPlanner"/> rather than replacing it: raid one is the
/// map the next-raid suggestion names, and each raid after it is that same choice made again with
/// the objectives the earlier raids were assumed to finish taken off the board.
/// </summary>
/// <remarks>
/// The plan is re-made whenever the board or the raid history changes, which is what "re-planned
/// after each recap" means here: a finished raid is one fewer slot and, once the game or the player
/// records it, fewer objectives. Nothing about the plan is observed; the page labels it an estimate.
/// </remarks>
public static class SessionPlanner
{
    /// <summary>A raid with its stash and queue time, before the player has three of their own.</summary>
    public static readonly TimeSpan DefaultPerRaid = TimeSpan.FromMinutes(30);

    /// <summary>Stash, trader and matching time between raids, added to a measured raid length.</summary>
    public static readonly TimeSpan Turnaround = TimeSpan.FromMinutes(7);

    /// <summary>How many objectives one raid is assumed to get done on its map.</summary>
    public const int ObjectivesPerRaid = 4;

    public const int MaxRaids = 8;

    /// <summary>The session lengths the page offers, in minutes.</summary>
    public static IReadOnlyList<int> SessionLengths { get; } = [60, 90, 120, 180, 240];

    public const int DefaultSessionMinutes = 120;

    /// <summary>A gap between raids longer than this starts a new session.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(45);

    /// <summary>Reads a stored session length; anything unreadable or out of range is two hours.</summary>
    public static int ParseSessionMinutes(string? stored) =>
        int.TryParse(stored, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
        && minutes is >= 30 and <= 600
            ? minutes
            : DefaultSessionMinutes;

    /// <summary>
    /// Where tonight's session stands: the run of raids ending with the latest one, each starting
    /// within <see cref="SessionGap"/> of the one before, provided the latest ended within that gap
    /// of now. Raids without both a start and an end are not counted.
    /// </summary>
    public static SessionProgress Measure(IEnumerable<RaidHistoryEntry> history, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(history);
        var timed = history
            .Where(raid => raid.StartedUtc is { } started && raid.EndedUtc is { } ended && ended > started && started <= nowUtc)
            .OrderByDescending(raid => raid.StartedUtc)
            .ToArray();
        var lengths = timed
            .Take(20)
            .Select(raid => raid.EndedUtc!.Value - raid.StartedUtc!.Value)
            .Where(length => length >= TimeSpan.FromMinutes(2) && length <= TimeSpan.FromMinutes(60))
            .OrderBy(length => length)
            .ToArray();
        TimeSpan? median = lengths.Length >= 3 ? lengths[lengths.Length / 2] : null;
        if (timed.Length == 0 || nowUtc - timed[0].EndedUtc!.Value > SessionGap)
        {
            return SessionProgress.Fresh with { MedianRaid = median };
        }

        var count = 1;
        var first = timed[0];
        for (var index = 1; index < timed.Length; index++)
        {
            if (first.StartedUtc!.Value - timed[index].EndedUtc!.Value > SessionGap)
            {
                break;
            }

            first = timed[index];
            count++;
        }

        return new(first.StartedUtc, count, nowUtc - first.StartedUtc!.Value, median);
    }

    /// <summary>
    /// Plans what is left of the session over the player's active quests.
    /// </summary>
    /// <param name="mapName">Names a map id; the planner never shows a raw id where a name exists.</param>
    /// <param name="hideoutItemsNeeded">Items the hideout still needs; above zero, spare slots become loot runs.</param>
    public static SessionPlan Plan(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        TimeSpan sessionLength,
        SessionProgress progress,
        Func<string, string> mapName,
        ActiveEventRules? eventRules = null,
        int hideoutItemsNeeded = 0)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(mapName);
        var perRaid = progress.MedianRaid is { } median ? median + Turnaround : DefaultPerRaid;
        var remaining = sessionLength - progress.Elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var slots = Math.Min(MaxRaids, (int)Math.Floor(remaining / perRaid));

        // What is left to do, per map: one entry per unfinished objective that names a map. Counted
        // the way Plan's map groups count, optional and unsupported objectives included, so raid one
        // is the map the "Suggested" line names and the two never disagree on the same board.
        var open = new List<OpenObjective>();
        foreach (var task in tasks)
        {
            if (task.RecordedState != RecordedTaskState.Active)
            {
                continue;
            }

            foreach (var objective in task.Objectives)
            {
                if (objective.RecordedState == RecordedObjectiveState.Completed || objective.MapIds.Count == 0)
                {
                    continue;
                }

                open.Add(new(task.TaskId, task.Name, objective.ObjectiveId, objective.MapIds));
            }
        }

        var raids = new List<SessionPlanRaid>();
        while (raids.Count < slots)
        {
            var candidates = open
                .SelectMany(entry => entry.MapIds.Select(map => (Map: map, Entry: entry)))
                .GroupBy(pair => pair.Map, StringComparer.OrdinalIgnoreCase)
                .Select(group => new NextRaidCandidate(
                    group.Key,
                    mapName(group.Key),
                    group.Select(pair => pair.Entry.TaskId).Distinct(StringComparer.Ordinal).Count(),
                    group.Count()));
            if (NextRaidPlanner.Suggest(candidates, eventRules) is not { } best)
            {
                break;
            }

            // The raid does the quests with the most objectives on this map first: those are the
            // quests a single raid is most likely to finish.
            var here = open
                .Where(entry => entry.MapIds.Contains(best.MapKey, StringComparer.OrdinalIgnoreCase))
                .GroupBy(entry => entry.TaskId, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.First().TaskName, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(group => group)
                .Take(ObjectivesPerRaid)
                .ToArray();
            raids.Add(new(
                raids.Count + 1,
                best.MapKey,
                best.MapLabel,
                [.. here.Select(entry => entry.TaskName).Distinct(StringComparer.Ordinal)],
                here.Length));
            foreach (var done in here)
            {
                open.Remove(done);
            }
        }

        while (raids.Count < slots && hideoutItemsNeeded > 0)
        {
            raids.Add(new(raids.Count + 1, string.Empty, string.Empty, [], 0));
        }

        return new(raids, perRaid, progress.MedianRaid is not null, progress.RaidsPlayed, remaining);
    }

    private sealed record OpenObjective(string TaskId, string TaskName, string ObjectiveId, IReadOnlyList<string> MapIds);
}
