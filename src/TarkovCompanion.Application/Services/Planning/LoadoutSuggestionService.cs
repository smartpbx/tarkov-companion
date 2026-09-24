using System.Globalization;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>A map that has active objectives, and how many.</summary>
public sealed record LoadoutSuggestionMap(string MapId, string Name, int ObjectiveCount);

/// <summary>A suggestion with what the player has of it and where to get it.</summary>
/// <param name="Have">"You own it", "None owned", or "Owned: not scanned"; empty where nothing is named.</param>
/// <param name="Source">The best trader source, or why there is none; empty where it is owned or names nothing.</param>
public sealed record LoadoutSuggestionRow(
    LoadoutSuggestion Suggestion,
    string Have,
    bool IsOwned,
    string Source,
    bool IsObtainable);

public sealed record LoadoutSuggestionPlan(
    IReadOnlyList<LoadoutSuggestionMap> Maps,
    string? MapId,
    IReadOnlyList<LoadoutSuggestionRow> Rows,
    string PlannerVersion,
    string? UnavailableReason = null);

/// <summary>
/// Reads the active profile's quest board, keeps the incomplete objectives of active quests, runs
/// <see cref="LoadoutSuggestionPlanner"/> for one map, and says for each suggestion whether the
/// player owns it (#793 owned counts) and whether a trader will sell it to them now (#761).
/// </summary>
public sealed class LoadoutSuggestionService(
    IPlayerProfileService profiles,
    IQuestReadService quests,
    IItemRepository items,
    IMapDataService? maps = null,
    IItemAcquisitionService? acquisitions = null)
{
    // The catalog's "Silencer" category; every suppressor carries it among its ancestors.
    private const string SilencerCategoryId = "550aa4cd4bdc2dd8348b456c";

    // Offers are read for this many candidate items at most: a "one of 22 weapons" suggestion
    // is answered by its cheapest few, not by every one.
    private const int SourcedItemsPerSuggestion = 24;

    public async Task<LoadoutSuggestionPlan> PlanAsync(string? mapId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var board = await quests.GetQuestBoardAsync(new(profile.Id, profile.GameMode, profile.ProfileGeneration), cancellationToken)
            .ConfigureAwait(false);
        if (board.UnavailableReason is { } unavailable)
        {
            return new([], null, [], PlannerVersions.LoadoutSuggestions, unavailable);
        }

        var objectives = board.Tasks
            .Where(task => task.RecordedState == RecordedTaskState.Active)
            .SelectMany(task => task.Objectives
                .Where(objective => objective.RecordedState != RecordedObjectiveState.Completed && !objective.IsUnsupported)
                .Select(objective => new LoadoutSuggestionObjective(
                    task.TaskId,
                    task.Name,
                    objective.ObjectiveId,
                    objective.Description,
                    objective.Kind,
                    // As the map read does: an objective that names no map is on its quest's map.
                    objective.MapIds.Count == 0 && task.PrimaryMapId is { } primary ? [primary] : objective.MapIds,
                    objective.ItemTargets,
                    objective.FoundInRaidRequired,
                    objective.TargetCount,
                    objective.SubtypeJson)))
            .ToArray();

        var mapCounts = objectives
            .SelectMany(objective => objective.MapIds.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Id: group.Key, Count: group.Count()))
            .ToArray();
        var named = new List<LoadoutSuggestionMap>(mapCounts.Length);
        foreach (var (id, count) in mapCounts)
        {
            var map = maps is null ? null : await maps.GetAsync(id, cancellationToken).ConfigureAwait(false);
            named.Add(new(id, map?.Name ?? id, count));
        }

        var ordered = named
            .OrderByDescending(map => map.ObjectiveCount)
            .ThenBy(map => map.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var chosen = ordered.FirstOrDefault(map => string.Equals(map.MapId, mapId, StringComparison.OrdinalIgnoreCase))?.MapId
            ?? ordered.FirstOrDefault()?.MapId;
        if (chosen is null)
        {
            return new(ordered, null, [], PlannerVersions.LoadoutSuggestions);
        }

        var itemFacts = await ItemsAsync(objectives, cancellationToken).ConfigureAwait(false);
        var suggestions = LoadoutSuggestionPlanner.Suggest(chosen, objectives, itemFacts);
        var sources = await SourcesAsync(suggestions, profile.OwnedItemCounts, cancellationToken).ConfigureAwait(false);
        var rows = suggestions
            .Select(suggestion => Row(suggestion, profile.OwnedItemCounts, sources, itemFacts))
            .ToArray();
        return new(ordered, chosen, rows, PlannerVersions.LoadoutSuggestions);
    }

    private async Task<IReadOnlyDictionary<string, LoadoutSuggestionItem>> ItemsAsync(
        IEnumerable<LoadoutSuggestionObjective> objectives,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in objectives)
        {
            // Only what the rules name: a hand-in list is not a loadout.
            var named = objective.Kind is QuestObjectiveKind.PlantItem or QuestObjectiveKind.Mark or
                QuestObjectiveKind.UseItem or QuestObjectiveKind.FindQuestItem or QuestObjectiveKind.FindItem;
            foreach (var target in objective.ItemTargets.Where(target => named || target.SourceField == "requiredKeys"))
            {
                ids.Add(target.ItemId);
            }

            if (objective.Kind == QuestObjectiveKind.Shoot)
            {
                var conditions = ShootConditions.Read(objective.SubtypeJson);
                ids.UnionWith(conditions.Weapons);
                ids.UnionWith(conditions.ModSets.SelectMany(set => set));
                ids.UnionWith(conditions.Wearing.SelectMany(set => set));
                ids.UnionWith(conditions.NotWearing.SelectMany(set => set));
            }
        }

        var facts = new Dictionary<string, LoadoutSuggestionItem>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (await items.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } item)
            {
                facts[id] = new(id, item.Name, item.CategoryIds.Contains(SilencerCategoryId), item.ShortName);
            }
        }

        return facts;
    }

    private async Task<ILookup<string, ProfiledItemAcquisition>> SourcesAsync(
        IReadOnlyList<LoadoutSuggestion> suggestions,
        IReadOnlyDictionary<string, int> owned,
        CancellationToken cancellationToken)
    {
        var wanted = suggestions
            .Where(suggestion => suggestion.Kind != LoadoutSuggestionKind.Room &&
                !suggestion.ItemIds.Any(id => owned.GetValueOrDefault(id) > 0))
            .SelectMany(suggestion => suggestion.ItemIds.Take(SourcedItemsPerSuggestion))
            .ToHashSet(StringComparer.Ordinal);
        if (acquisitions is null || wanted.Count == 0)
        {
            return Array.Empty<ProfiledItemAcquisition>().ToLookup(row => row.Offer.ItemId, StringComparer.Ordinal);
        }

        var rows = await acquisitions.GetAsync(wanted, cancellationToken).ConfigureAwait(false);
        return rows.ToLookup(row => row.Offer.ItemId, StringComparer.Ordinal);
    }

    private static LoadoutSuggestionRow Row(
        LoadoutSuggestion suggestion,
        IReadOnlyDictionary<string, int> owned,
        ILookup<string, ProfiledItemAcquisition> sources,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items)
    {
        // Room is for something picked up in the raid: owning or buying one does not serve it.
        if (suggestion.ItemIds.Count == 0 || suggestion.Kind == LoadoutSuggestionKind.Room)
        {
            return new(suggestion, string.Empty, false, string.Empty, false);
        }

        // Unknown is not zero (#509): an item no scan has counted is "not scanned", never "none".
        var ownedId = suggestion.ItemIds.FirstOrDefault(id => owned.GetValueOrDefault(id) > 0);
        if (ownedId is not null)
        {
            var count = owned[ownedId];
            var have = suggestion.ItemIds.Count == 1
                ? count > 1 ? string.Create(CultureInfo.CurrentCulture, $"You own {count}") : "You own it"
                : "You own " + (items.GetValueOrDefault(ownedId)?.Name ?? ownedId);
            if (suggestion.Count <= count || suggestion.ItemIds.Count > 1)
            {
                return new(suggestion, have, true, string.Empty, true);
            }
        }

        var recorded = suggestion.ItemIds.Any(owned.ContainsKey);
        var haveText = ownedId is not null
            ? string.Create(CultureInfo.CurrentCulture, $"You own {owned[ownedId]} of {suggestion.Count}")
            : recorded ? "None owned" : "Owned: not scanned";

        var offers = suggestion.ItemIds
            .Take(SourcedItemsPerSuggestion)
            .SelectMany(id => sources[id])
            .OrderByDescending(row => row.Availability.IsObtainable)
            .ThenBy(row => row.Offer.Kind)
            .ThenBy(row => row.Offer.PriceRoubles ?? long.MaxValue)
            .ToArray();
        if (offers.FirstOrDefault() is not { } best)
        {
            return new(suggestion, haveText, false, "No trader sells it", false);
        }

        var what = suggestion.ItemIds.Count > 1 ? (items.GetValueOrDefault(best.Offer.ItemId)?.Name ?? best.Offer.ItemId) + " · " : string.Empty;
        var how = best.Offer.Kind == ItemAcquisitionKind.Cash && best.Offer.PriceRoubles is { } price
            ? $"{best.Offer.TraderName} {price.ToString("N0", CultureInfo.CurrentCulture)} ₽"
            : $"{best.Offer.TraderName} barter";
        var source = best.Availability.IsObtainable
            ? what + how
            : $"{what}{how} · {best.Availability.RequirementLabel}";
        return new(suggestion, haveText, false, source, best.Availability.IsObtainable);
    }
}
