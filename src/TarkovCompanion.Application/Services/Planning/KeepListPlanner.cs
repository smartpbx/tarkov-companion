using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>Everything the Keep list is computed from, read once, so the same snapshot always gives the same list.</summary>
/// <param name="TrackedTaskIds">The quests the player is on: active or pinned.</param>
public sealed record KeepListInputs(
    PlayerProfile Profile,
    IReadOnlyList<QuestItemRequirement> QuestRequirements,
    IReadOnlyList<HideoutItemRequirement> HideoutRequirements,
    IReadOnlyDictionary<string, string> TaskNames,
    IReadOnlySet<string> TrackedTaskIds,
    IReadOnlyDictionary<string, string> StationNames,
    IReadOnlyList<KeyFacts> KeyFacts)
{
    /// <summary>For a quest requirement that is one of several items which would each do, how many would.</summary>
    public IReadOnlyDictionary<(string TaskId, string ItemId), int> InterchangeableCounts { get; init; } =
        new Dictionary<(string, string), int>();

    /// <summary>The quest requirements that are carried in and back out (a key), which are not added together.</summary>
    public IReadOnlySet<(string TaskId, string ItemId)> Reusable { get; init; } = new HashSet<(string, string)>();
}

/// <summary>
/// The items worth keeping, computed from the quest and hideout requirements, the key facts and
/// what the profile has already handed in, built or owns. Nothing here is editorial.
/// </summary>
/// <remarks>
/// Moved out of the Keep list view model by #307, which had it computing this while also
/// presenting it. The view model now only lays out what this returns.
///
/// Reuses <see cref="KeyValue"/> exactly as the Keys page does: a key is worth keeping when a
/// tracked or outstanding quest needs it, a hideout build needs it, or the market prices it in the
/// top quarter of cached keys. It does not call the explainable recommendation engine: that decides
/// what to do with one scanned cell and needs placement and capacity this list has no reason to
/// have. All it needs from the value side is the tier, which the caller resolves per item.
///
/// A quest that is completed, or whose objective has had its full count recorded, asks for
/// nothing, so it drops off as progress is recorded. Hideout need is summed across stations and
/// the profile's stock taken off once at the item, not per station, because one pile of bolts
/// serves whichever station is built first.
/// </remarks>
public static class KeepListPlanner
{
    public static async Task<KeepPlan> PlanAsync(
        KeepListInputs inputs,
        Func<string, CancellationToken, Task<KeepItemFacts>> resolveItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(resolveItem);

        var profile = inputs.Profile;

        // Item -> task -> quantity still outstanding, and how much of it must be found in raid.
        // Only uncompleted tasks with something still owed reach the map, so its keys are exactly
        // the items a quest still asks for.
        var questByItem = new Dictionary<string, Dictionary<string, (int Remaining, int FoundInRaid)>>(StringComparer.Ordinal);
        var trackedItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in inputs.QuestRequirements.Where(x => !profile.CompletedTaskIds.Contains(x.TaskId)))
        {
            var progress = profile.ObjectiveProgress.GetValueOrDefault(requirement.ObjectiveId);
            var remaining = Math.Max(0, requirement.Required - progress);
            if (remaining <= 0)
            {
                continue;
            }

            var byTask = questByItem.TryGetValue(requirement.ItemId, out var existing)
                ? existing
                : questByItem[requirement.ItemId] = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            var known = byTask.GetValueOrDefault(requirement.TaskId);
            byTask[requirement.TaskId] = (
                known.Remaining + remaining,
                known.FoundInRaid + (requirement.FoundInRaidRequired ? remaining : 0));
            if (inputs.TrackedTaskIds.Contains(requirement.TaskId))
            {
                trackedItemIds.Add(requirement.ItemId);
            }
        }

        // Item -> station -> quantity the station's next build still asks for, before the
        // profile's own stock is subtracted (once, at the item level below).
        var hideoutByItem = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var requirement in inputs.HideoutRequirements.Where(x =>
                     profile.HideoutStationLevels.GetValueOrDefault(x.StationId) < x.TargetLevel))
        {
            var byStation = hideoutByItem.TryGetValue(requirement.ItemId, out var existing)
                ? existing
                : hideoutByItem[requirement.ItemId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byStation[requirement.StationId] = byStation.GetValueOrDefault(requirement.StationId) + requirement.Required;
        }

        // What the whole build asks for, every level of every station, built or not: the figure the
        // remaining part is read against.
        var hideoutTotalByItem = inputs.HideoutRequirements
            .GroupBy(requirement => requirement.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(requirement => requirement.Required), StringComparer.Ordinal);

        var keyFactsById = inputs.KeyFacts.ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);
        var keyRanks = KeyValue.Rank(inputs.KeyFacts
            .Where(fact => fact.AcquisitionCostRoubles is > 0)
            .Select(fact => (fact.ItemId, fact.AcquisitionCostRoubles!.Value)));

        var candidateIds = new SortedSet<string>(StringComparer.Ordinal);
        candidateIds.UnionWith(questByItem.Keys);
        candidateIds.UnionWith(hideoutByItem.Keys);
        candidateIds.UnionWith(keyFactsById.Keys);

        var entries = new List<KeepEntry>();
        foreach (var itemId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await resolveItem(itemId, cancellationToken).ConfigureAwait(false);
            var isTrackedQuest = trackedItemIds.Contains(itemId);

            var questNeeds = questByItem.TryGetValue(itemId, out var byTask)
                ? byTask
                    .Select(x => new KeepQuestNeed(x.Key, inputs.TaskNames.GetValueOrDefault(x.Key, x.Key), x.Value.Remaining, x.Value.FoundInRaid)
                    {
                        AnyOf = inputs.InterchangeableCounts.GetValueOrDefault((x.Key, itemId), 1),
                        IsTracked = inputs.TrackedTaskIds.Contains(x.Key),
                        IsReusable = inputs.Reusable.Contains((x.Key, itemId)),
                    })
                    // Two quests of one name are one quest the player takes one way or the other: on
                    // the real catalog ten names sit on two or three tasks, four as a BEAR and a USEC
                    // copy and six as branches. Both were listed, under the same name, and added up.
                    // The one the player is on stands for them, or else the one that asks for more.
                    .GroupBy(need => need.TaskName, StringComparer.Ordinal)
                    .Select(variants => variants
                        .OrderByDescending(need => need.IsTracked)
                        .ThenByDescending(need => need.Remaining)
                        .ThenBy(need => need.TaskId, StringComparer.Ordinal)
                        .First())
                    // The quests the player is on come first: they are why the item is on the list today.
                    .OrderByDescending(need => need.IsTracked)
                    .ThenByDescending(need => need.Remaining)
                    .ThenBy(need => need.TaskName, StringComparer.Ordinal)
                    .ToArray()
                : [];

            KeepHideoutNeed[] hideoutNeeds = [];
            if (hideoutByItem.TryGetValue(itemId, out var byStation) &&
                HeldCount.Remaining(byStation.Values.Sum(), HeldCount.Of(profile.OwnedItemCounts, itemId)) > 0)
            {
                hideoutNeeds = byStation
                    .Select(x => new KeepHideoutNeed(x.Key, inputs.StationNames.GetValueOrDefault(x.Key, x.Key), x.Value))
                    .OrderByDescending(need => need.Required)
                    .ThenBy(need => need.StationName, StringComparer.Ordinal)
                    .ToArray();
            }

            string? keyReason = null;
            if (keyFactsById.TryGetValue(itemId, out var keyFact))
            {
                var needs = new ItemNeedSummary(0, 0, hideoutNeeds.Length > 0 ? 1 : 0)
                {
                    QuestsNeedingIt = questNeeds.Length > 0 ? 1 : 0,
                    TrackedQuestsNeedingIt = isTrackedQuest ? 1 : 0,
                };
                var verdict = KeyValue.Judge(
                    keyFact.AcquisitionCostRoubles,
                    keyRanks.GetValueOrDefault(itemId),
                    keyFact.Locks.Count,
                    keyFact.MaximumUses,
                    needs);
                if (verdict.Call is KeepOrSell.Keep or KeepOrSell.KeepForLater)
                {
                    keyReason = verdict.Reason;
                }
            }

            if (questNeeds.Length == 0 && hideoutNeeds.Length == 0 && keyReason is null && !item.IsHighValue)
            {
                continue;
            }

            var group = isTrackedQuest ? KeepGroupKind.ActiveQuest
                : questNeeds.Length > 0 ? KeepGroupKind.Quest
                : hideoutNeeds.Length > 0 ? KeepGroupKind.Hideout
                : keyReason is not null ? KeepGroupKind.Key
                : KeepGroupKind.HighValue;
            entries.Add(new KeepEntry(
                itemId,
                group,
                item,
                questNeeds,
                hideoutNeeds,
                keyReason,
                hideoutNeeds.Length > 0 ? hideoutTotalByItem.GetValueOrDefault(itemId) : 0)
            {
                Held = HeldCount.Of(profile.OwnedItemCounts, itemId),
            });
        }

        return new KeepPlan(
            true,
            [
                .. entries
                    .OrderBy(entry => entry.Group)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.ItemId, StringComparer.Ordinal),
            ]);
    }
}
