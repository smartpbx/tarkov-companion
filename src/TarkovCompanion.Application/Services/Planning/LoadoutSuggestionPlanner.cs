using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>What a suggestion asks the player to change, in the order a kit is packed.</summary>
public enum LoadoutSuggestionKind
{
    Key,
    Weapon,
    WeaponMod,
    Wear,
    LeaveBehind,
    Range,
    Carry,
    Room,
}

/// <summary>One incomplete objective of an active quest, as the suggestion rules read it.</summary>
public sealed record LoadoutSuggestionObjective(
    string TaskId,
    string TaskName,
    string ObjectiveId,
    string Description,
    QuestObjectiveKind Kind,
    IReadOnlyList<string> MapIds,
    IReadOnlyList<QuestObjectiveItemTarget> ItemTargets,
    bool? FoundInRaidRequired,
    decimal? TargetCount,
    string SubtypeJson);

/// <summary>What the rules need to know about an item the catalog names.</summary>
/// <param name="ShortName">The game's short name, used where several are listed on one line.</param>
public sealed record LoadoutSuggestionItem(string Id, string Name, bool IsSuppressor, string? ShortName = null);

/// <summary>
/// One change to the kit, the objective that asks for it, and every quest it serves.
/// </summary>
/// <param name="ItemIds">Any one of these does; empty where the suggestion names no item (range).</param>
/// <param name="Count">How many to bring, where more than one is used up.</param>
/// <param name="AnyMap">True where the objective can be done on any map, not only the chosen one.</param>
public sealed record LoadoutSuggestion(
    LoadoutSuggestionKind Kind,
    string Title,
    string Reason,
    IReadOnlyList<string> Quests,
    IReadOnlyList<string> ItemIds,
    int Count,
    bool AnyMap,
    string PlannerVersion);

/// <summary>
/// Turns the chosen map's active objectives into loadout changes (#307): the key a locked room
/// needs, the weapon or suppressor a kill must be made with, gear to wear or leave at home, the
/// range a kill must be made from, what to plant or use, and room for what is to be found.
/// </summary>
/// <remarks>
/// Every suggestion is read from a condition the catalog states on an objective; nothing is
/// inferred from what players usually do. So there is no "class 4 armour for Customs": no source
/// this app reads says what armour is common on a map, and a guess dressed as a suggestion would
/// be read as a fact. The ADR (0021) keeps the list of what is and is not modelled.
///
/// Shooting objectives with no map at all ("with a suppressed bolt-action rifle", anywhere) are
/// offered on every map and marked as such: the kill can be made on the raid being packed for.
/// </remarks>
public static class LoadoutSuggestionPlanner
{
    private const int NamesShown = 3;

    public static IReadOnlyList<LoadoutSuggestion> Suggest(
        string mapId,
        IEnumerable<LoadoutSuggestionObjective> objectives,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(objectives);
        ArgumentNullException.ThrowIfNull(items);

        var drafts = new List<Draft>();
        foreach (var objective in objectives
                     .OrderBy(objective => objective.TaskName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(objective => objective.ObjectiveId, StringComparer.Ordinal))
        {
            var onMap = objective.MapIds.Contains(mapId, StringComparer.OrdinalIgnoreCase);
            var anyMap = objective.MapIds.Count == 0 && objective.Kind == QuestObjectiveKind.Shoot;
            if (!onMap && !anyMap)
            {
                continue;
            }

            Keys(objective, items, anyMap, drafts);
            switch (objective.Kind)
            {
                case QuestObjectiveKind.Shoot:
                    Shooting(objective, items, anyMap, drafts);
                    break;
                case QuestObjectiveKind.PlantItem or QuestObjectiveKind.Mark or QuestObjectiveKind.UseItem:
                    Carried(objective, items, drafts);
                    break;
                case QuestObjectiveKind.FindQuestItem:
                    Room(objective, items, drafts);
                    break;
                case QuestObjectiveKind.FindItem when objective.FoundInRaidRequired == true:
                    Room(objective, items, drafts);
                    break;
            }
        }

        return drafts
            .GroupBy(draft => draft.Identity, StringComparer.Ordinal)
            .Select(group => Merge(group.ToArray()))
            .OrderBy(suggestion => suggestion.AnyMap)
            .ThenBy(suggestion => suggestion.Kind)
            .ThenBy(suggestion => suggestion.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void Keys(
        LoadoutSuggestionObjective objective,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items,
        bool anyMap,
        List<Draft> drafts)
    {
        // Each group is one door: any key in it opens that door, and every group must be opened.
        foreach (var group in objective.ItemTargets
                     .Where(target => target.SourceField == "requiredKeys")
                     .GroupBy(target => target.AlternativeGroup)
                     .OrderBy(group => group.Key))
        {
            var ids = group.OrderBy(target => target.SourceOrdinal).Select(target => target.ItemId).Distinct(StringComparer.Ordinal).ToArray();
            drafts.Add(new(
                LoadoutSuggestionKind.Key,
                "Bring " + Names(ids, items, " or "),
                objective,
                ids,
                0,
                anyMap));
        }
    }

    private static void Shooting(
        LoadoutSuggestionObjective objective,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items,
        bool anyMap,
        List<Draft> drafts)
    {
        var conditions = ShootConditions.Read(objective.SubtypeJson);
        if (conditions.Weapons.Count > 0)
        {
            drafts.Add(new(
                LoadoutSuggestionKind.Weapon,
                conditions.Weapons.Count == 1
                    ? "Bring " + Name(conditions.Weapons[0], items)
                    : string.Create(CultureInfo.CurrentCulture, $"Bring one of {conditions.Weapons.Count} weapons: {ShortNames(conditions.Weapons, items)}"),
                objective,
                conditions.Weapons,
                0,
                anyMap));
        }

        if (conditions.ModSets.Count > 0)
        {
            // A set is mods that must all be fitted; any one set will do. Where every set holds a
            // suppressor, "a suppressor" is what the player needs to hear, not forty part names.
            var suppressors = conditions.ModSets
                .SelectMany(set => set)
                .Where(id => items.GetValueOrDefault(id)?.IsSuppressor == true)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var everySetSuppressed = conditions.ModSets.All(set => set.Any(id => items.GetValueOrDefault(id)?.IsSuppressor == true));
            if (everySetSuppressed)
            {
                drafts.Add(new(
                    LoadoutSuggestionKind.WeaponMod,
                    conditions.Weapons.Count > 0 ? "Fit a suppressor to it" : "Fit a suppressor",
                    objective,
                    suppressors,
                    0,
                    anyMap));
            }
            else
            {
                var ids = conditions.ModSets.Count == 1
                    ? conditions.ModSets[0]
                    : conditions.ModSets.SelectMany(set => set).Distinct(StringComparer.Ordinal).ToArray();
                drafts.Add(new(
                    LoadoutSuggestionKind.WeaponMod,
                    conditions.ModSets.Count == 1
                        ? "Fit " + Names(conditions.ModSets[0], items, " + ")
                        : string.Create(CultureInfo.CurrentCulture, $"Fit one of {conditions.ModSets.Count} mod sets: {ShortNames(ids, items)}"),
                    objective,
                    ids,
                    0,
                    anyMap));
            }
        }

        if (conditions.Wearing.Count > 0)
        {
            var ids = conditions.Wearing.SelectMany(set => set).Distinct(StringComparer.Ordinal).ToArray();
            drafts.Add(new(
                LoadoutSuggestionKind.Wear,
                conditions.Wearing.Count == 1
                    ? "Wear " + Names(conditions.Wearing[0], items, " + ", conditions.Names)
                    : "Wear one of: " + string.Join(" / ", conditions.Wearing.Take(NamesShown).Select(set => Names(set, items, " + ", conditions.Names))) +
                      (conditions.Wearing.Count > NamesShown ? " …" : string.Empty),
                objective,
                ids,
                0,
                anyMap));
        }

        if (conditions.NotWearing.Count > 0)
        {
            var ids = conditions.NotWearing.SelectMany(set => set).Distinct(StringComparer.Ordinal).ToArray();
            drafts.Add(new(
                LoadoutSuggestionKind.LeaveBehind,
                "Leave behind: " + Names(ids, items, ", ", conditions.Names),
                objective,
                [],
                0,
                anyMap));
        }

        if (conditions.DistanceMetres is > 0 and var metres)
        {
            drafts.Add(new(
                LoadoutSuggestionKind.Range,
                conditions.DistanceAtMost
                    ? string.Create(CultureInfo.CurrentCulture, $"Close range: within {metres:0} m")
                    : string.Create(CultureInfo.CurrentCulture, $"Long range: {metres:0} m or more"),
                objective,
                [],
                0,
                anyMap));
        }
    }

    private static void Carried(
        LoadoutSuggestionObjective objective,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items,
        List<Draft> drafts)
    {
        // A marker or a planted item is left behind, so two objectives need two; "use any" is
        // a choice of one.
        var ids = objective.ItemTargets
            .Where(target => target.SourceField is "items" or "item" or "markerItem" or "useAny")
            .OrderBy(target => target.SourceOrdinal)
            .Select(target => target.ItemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var count = objective.Kind == QuestObjectiveKind.Mark ? 1 : Math.Max(1, (int)Math.Ceiling(objective.TargetCount ?? 1));
        drafts.Add(new(
            LoadoutSuggestionKind.Carry,
            ids.Length == 1 ? "Bring " + Name(ids[0], items) : "Bring one of: " + Names(ids, items, ", "),
            objective,
            ids,
            count,
            false));
    }

    private static void Room(
        LoadoutSuggestionObjective objective,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items,
        List<Draft> drafts)
    {
        var ids = objective.ItemTargets
            .Where(target => target.SourceField is "items" or "item" or "questItem")
            .OrderBy(target => target.SourceOrdinal)
            .Select(target => target.ItemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var count = Math.Max(1, (int)Math.Ceiling(objective.TargetCount ?? 1));
        // A quest item exists only in its raid, and the item catalog does not list it, so its id
        // is not a name to show.
        var what = ids.All(id => !items.ContainsKey(id))
            ? "the quest item"
            : ids.Length == 1 ? Name(ids[0], items) : Names(ids, items, " or ");
        drafts.Add(new(
            LoadoutSuggestionKind.Room,
            (objective.Kind == QuestObjectiveKind.FindItem ? "Room for found in raid: " : "Room for ") + what,
            objective,
            ids,
            count,
            false));
    }

    private static LoadoutSuggestion Merge(IReadOnlyList<Draft> group)
    {
        var first = group[0];
        var quests = group.Select(draft => draft.Objective.TaskName).Distinct(StringComparer.Ordinal).ToArray();
        // Keys, weapons and worn gear serve every objective at once; what is planted or picked up
        // is counted per objective.
        var count = first.Kind is LoadoutSuggestionKind.Carry or LoadoutSuggestionKind.Room
            ? group.Sum(draft => draft.Count)
            : first.Count;
        var title = count > 1 ? string.Create(CultureInfo.CurrentCulture, $"{first.Title} ×{count}") : first.Title;
        var reason = group.Count == 1
            ? first.Objective.Description
            : string.Create(CultureInfo.CurrentCulture, $"{first.Objective.Description} (+{group.Count - 1} more)");
        return new(first.Kind, title, reason, quests, first.ItemIds, count, group.All(draft => draft.AnyMap), PlannerVersions.LoadoutSuggestions);
    }

    private static string Name(string id, IReadOnlyDictionary<string, LoadoutSuggestionItem> items, IReadOnlyDictionary<string, string>? names = null) =>
        items.GetValueOrDefault(id)?.Name ?? names?.GetValueOrDefault(id) ?? id;

    // Eleven rifle names do not fit a line; "Mosin inf., Mosin, SV-98 …" does.
    private static string ShortNames(IReadOnlyList<string> ids, IReadOnlyDictionary<string, LoadoutSuggestionItem> items) =>
        string.Join(", ", ids.Take(NamesShown).Select(id => items.GetValueOrDefault(id) is { ShortName: { Length: > 0 } shortName } ? shortName : Name(id, items))) +
        (ids.Count > NamesShown ? " …" : string.Empty);

    private static string Names(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, LoadoutSuggestionItem> items,
        string separator,
        IReadOnlyDictionary<string, string>? names = null)
    {
        var shown = ids.Take(NamesShown).Select(id => Name(id, items, names));
        return string.Join(separator, shown) + (ids.Count > NamesShown ? " …" : string.Empty);
    }

    private sealed record Draft(
        LoadoutSuggestionKind Kind,
        string Title,
        LoadoutSuggestionObjective Objective,
        IReadOnlyList<string> ItemIds,
        int Count,
        bool AnyMap)
    {
        // Same kind and same items is one suggestion; a range has no items, so its words decide.
        public string Identity => ItemIds.Count == 0
            ? $"{Kind}|{Title}"
            : $"{Kind}|{string.Join(',', ItemIds.Order(StringComparer.Ordinal))}";
    }
}

/// <summary>The shooting conditions json.tarkov.dev states on a shoot objective.</summary>
/// <remarks>
/// Read from the stored subtype JSON. External JSON is untrusted: an unknown shape reads as no
/// condition rather than an exception, since a missing suggestion costs less than a broken page.
/// usingWeaponMods and wearing are lists of sets: the player needs every item of any one set.
/// </remarks>
public sealed record ShootConditions(
    IReadOnlyList<string> Weapons,
    IReadOnlyList<IReadOnlyList<string>> ModSets,
    IReadOnlyList<IReadOnlyList<string>> Wearing,
    IReadOnlyList<IReadOnlyList<string>> NotWearing,
    double? DistanceMetres,
    bool DistanceAtMost,
    IReadOnlyDictionary<string, string> Names)
{
    public static ShootConditions None { get; } = new([], [], [], [], null, false, new Dictionary<string, string>());

    public static ShootConditions Read(string? subtypeJson)
    {
        if (string.IsNullOrWhiteSpace(subtypeJson))
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(subtypeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var weapons = Ids(root, "usingWeapon", names);
            var mods = Sets(root, "usingWeaponMods", names);
            var wearing = Sets(root, "wearing", names);
            var notWearing = Sets(root, "notWearing", names);
            double? distance = null;
            var atMost = false;
            if (root.TryGetProperty("distance", out var range) && range.ValueKind == JsonValueKind.Object &&
                range.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number &&
                value.GetDouble() is > 0 and var metres)
            {
                distance = metres;
                atMost = range.TryGetProperty("compareMethod", out var compare) &&
                    compare.ValueKind == JsonValueKind.String &&
                    compare.GetString() is "<=" or "<";
            }

            return new(weapons, mods, wearing, notWearing, distance, atMost, names);
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static IReadOnlyList<string> Ids(JsonElement root, string property, Dictionary<string, string> names) =>
        root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(element => Id(element, names)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            : [];

    private static IReadOnlyList<IReadOnlyList<string>> Sets(JsonElement root, string property, Dictionary<string, string> names)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sets = new List<IReadOnlyList<string>>();
        foreach (var element in array.EnumerateArray())
        {
            string[] set = element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Select(item => Id(item, names)).OfType<string>().ToArray()
                : Id(element, names) is { } single ? [single] : [];
            if (set.Length > 0)
            {
                sets.Add(set);
            }
        }

        return sets;
    }

    // The feed writes an item either as its id or as {"id": ..., "name": ...}.
    private static string? Id(JsonElement element, Dictionary<string, string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { Length: > 0 } id:
                return id;
            case JsonValueKind.Object when element.TryGetProperty("id", out var idElement) &&
                                           idElement.ValueKind == JsonValueKind.String &&
                                           idElement.GetString() is { Length: > 0 } id:
                if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() is { Length: > 0 } text)
                {
                    names.TryAdd(id, text);
                }

                return id;
            default:
                return null;
        }
    }
}
