using System.Text;
using System.Text.Json;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

/// <summary>
/// Validates every invariant required by normalized persistence before a response can become a
/// cache head. A transport cache that accepts a body the database must later refuse turns one bad
/// response into a durable retry loop, because the next request sees that poisoned body as fresh.
/// </summary>
internal static class TarkovDevDatasetValidator
{
    private const double MaximumItemWeightKg = 1_000_000;
    private const int MaximumIdentifierUtf8Bytes = 512;
    private const int MaximumDisplayTextUtf8Bytes = 4096;
    private const int MaximumDescriptionUtf8Bytes = 64 * 1024;
    private const int MaximumUriUtf8Bytes = 8192;
    private static readonly long MinimumUnixMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaximumUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static void Validate<T>(T data, string sourceKey)
    {
        if (data is null)
        {
            throw Invalid(sourceKey, "the data envelope is null");
        }

        switch (data)
        {
            case TarkovDevItemsData items:
                ValidateItems(items, sourceKey);
                return;
            case TarkovDevMapsData maps:
                ValidateMaps(maps, sourceKey);
                return;
            case TarkovDevTasksData tasks:
                ValidateTasks(tasks, sourceKey);
                return;
            case IReadOnlyDictionary<string, TarkovDevHideoutStation> hideout:
                ValidateHideout(hideout, sourceKey);
                return;
            case IReadOnlyDictionary<string, TarkovDevTrader> traders:
                ValidateTraders(traders, sourceKey);
                return;
            case IReadOnlyCollection<TarkovDevCraft> crafts:
                var craftIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var craft in crafts)
                {
                    if (craft is null)
                    {
                        throw Invalid(sourceKey, "craft is null");
                    }

                    Required(craft.Id, sourceKey, "craft id");
                    OptionalIdentifier(craft.Station, sourceKey, $"craft '{craft.Id}' station id");
                    Unique(craftIds, craft.Id, sourceKey, "craft id");
                    NonNegative(craft.Level, sourceKey, $"craft '{craft.Id}' station level");
                    ValidateRequirement(craft.ProductItem, sourceKey, "craft product");
                    foreach (var requirement in Present(craft.RequiredItems, sourceKey, $"craft '{craft.Id}' requirements"))
                    {
                        ValidateRequirement(requirement, sourceKey, "craft requirement");
                    }
                }

                return;
            case IReadOnlyCollection<TarkovDevBarter> barters:
                var barterIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var barter in barters)
                {
                    if (barter is null)
                    {
                        throw Invalid(sourceKey, "barter is null");
                    }

                    Required(barter.Id, sourceKey, "barter id");
                    OptionalIdentifier(barter.Trader, sourceKey, $"barter '{barter.Id}' trader id");
                    Unique(barterIds, barter.Id, sourceKey, "barter id");
                    ValidateRequirement(barter.OfferedItem, sourceKey, "barter output");
                    foreach (var requirement in Present(barter.RequiredItems, sourceKey, $"barter '{barter.Id}' requirements"))
                    {
                        ValidateRequirement(requirement, sourceKey, "barter requirement");
                    }
                }

                return;
            case IReadOnlyCollection<TarkovDevPricePoint> prices:
                foreach (var point in prices)
                {
                    if (point is null)
                    {
                        throw Invalid(sourceKey, "price point is null");
                    }

                    NonNegative(point.Price, sourceKey, "price");
                    NonNegative(point.PriceMin, sourceKey, "minimum price");
                    if (point.Timestamp is { } timestamp &&
                        (timestamp < MinimumUnixMilliseconds || timestamp > MaximumUnixMilliseconds))
                    {
                        throw Invalid(sourceKey, $"price timestamp '{timestamp}' is outside the UTC timestamp range");
                    }
                }

                return;
            default:
                throw new JsonException($"No normalized persistence validator is registered for '{typeof(T).Name}'.");
        }
    }

    private static void ValidateItems(TarkovDevItemsData data, string sourceKey)
    {
        var items = Present(data.Items, sourceKey, "items dictionary");
        var categories = Present(data.ItemCategories, sourceKey, "item categories dictionary");
        foreach (var pair in items)
        {
            var item = pair.Value ?? throw Invalid(sourceKey, $"item '{pair.Key}' is null");
            Required(pair.Key, sourceKey, "item dictionary key");
            Required(item.Id, sourceKey, "item id");
            RequiredText(item.Name, sourceKey, $"item '{pair.Key}' name");
            OptionalText(item.ShortName, sourceKey, $"item '{pair.Key}' short name");
            OptionalText(
                item.Description,
                sourceKey,
                $"item '{pair.Key}' description",
                MaximumDescriptionUtf8Bytes);
            OptionalText(item.NormalizedName, sourceKey, $"item '{pair.Key}' normalized name");
            OptionalText(item.IconLink, sourceKey, $"item '{pair.Key}' icon URL", MaximumUriUtf8Bytes);
            OptionalText(item.GridImageLink, sourceKey, $"item '{pair.Key}' grid-image URL", MaximumUriUtf8Bytes);
            OptionalText(item.WikiLink, sourceKey, $"item '{pair.Key}' wiki URL", MaximumUriUtf8Bytes);
            if (!string.Equals(pair.Key, item.Id, StringComparison.Ordinal))
            {
                throw Invalid(sourceKey, $"item dictionary key '{pair.Key}' does not match id '{item.Id}'");
            }

            if (item.Width is < 1 or > 64 || item.Height is < 1 or > 256)
            {
                throw Invalid(sourceKey, $"item '{pair.Key}' has an invalid footprint");
            }

            NonNegative(item.BasePrice, sourceKey, $"item '{pair.Key}' base price");
            NonNegative(item.Avg24hPrice, sourceKey, $"item '{pair.Key}' average price");
            NonNegative(item.LastLowPrice, sourceKey, $"item '{pair.Key}' last-low price");
            NonNegative(item.Low24hPrice, sourceKey, $"item '{pair.Key}' low price");
            NonNegative(item.High24hPrice, sourceKey, $"item '{pair.Key}' high price");
            if (item.Weight is { } weightKg &&
                (!double.IsFinite(weightKg) || weightKg is < 0 or > MaximumItemWeightKg))
            {
                throw Invalid(sourceKey, $"item '{pair.Key}' weight is outside the persisted range");
            }

            if (item.Properties is { ValueKind: JsonValueKind.Object } properties &&
                properties.TryGetProperty("weight", out var weight) &&
                weight.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                (!weight.TryGetDouble(out var number) || !double.IsFinite(number) ||
                 number is < 0 or > MaximumItemWeightKg))
            {
                throw Invalid(sourceKey, $"item '{pair.Key}' weight is outside the persisted range");
            }

            RequiredStrings(
                Present(item.Types, sourceKey, $"item '{pair.Key}' types"),
                sourceKey,
                $"item '{pair.Key}' type");
            RequiredStrings(
                Present(item.Categories, sourceKey, $"item '{pair.Key}' categories"),
                sourceKey,
                $"item '{pair.Key}' category id");
            var offerKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in Present(item.SellToTrader, sourceKey, $"item '{pair.Key}' trader offers"))
            {
                if (offer is null)
                {
                    throw Invalid(sourceKey, $"item '{pair.Key}' trader offer is null");
                }

                Required(offer.Trader, sourceKey, $"item '{pair.Key}' offer trader id");
                Required(offer.Currency, sourceKey, $"item '{pair.Key}' offer currency");
                NonNegative(offer.Price, sourceKey, $"item '{pair.Key}' trader price");
                NonNegative(offer.PriceRub, sourceKey, $"item '{pair.Key}' trader rouble price");
                Unique(
                    offerKeys,
                    $"{offer.Trader}\u001f{offer.Currency}",
                    sourceKey,
                    $"item '{pair.Key}' trader/currency offer");
            }
        }

        ValidateDictionaryIdentities(categories, sourceKey, "item category", value => value.Id);
        foreach (var category in categories.Values)
        {
            OptionalText(category.Name, sourceKey, $"item category '{category.Id}' name");
        }
    }

    private static void ValidateMaps(TarkovDevMapsData data, string sourceKey)
    {
        var maps = Present(data.Maps, sourceKey, "maps dictionary");
        var lootContainers = Present(data.LootContainers, sourceKey, "loot containers dictionary");
        ValidateDictionaryIdentities(maps, sourceKey, "map", value => value.Id);
        ValidateDictionaryIdentities(lootContainers, sourceKey, "loot container", value => value.Id);
        foreach (var container in lootContainers.Values)
        {
            OptionalText(container.Name, sourceKey, $"loot container '{container.Id}' name");
            OptionalText(container.NormalizedName, sourceKey, $"loot container '{container.Id}' normalized name");
        }
        if (maps.Count > 0 && maps.Values.All(map => Present(map.Extracts, sourceKey, $"map '{map.Id}' extracts").Count == 0))
        {
            throw Invalid(sourceKey, "every map contains zero extracts");
        }

        foreach (var map in maps.Values)
        {
            OptionalText(map.Name, sourceKey, $"map '{map.Id}' name");
            OptionalText(map.NormalizedName, sourceKey, $"map '{map.Id}' normalized name");
            OptionalIdentifier(map.NameId, sourceKey, $"map '{map.Id}' internal name id");
            if (map.RaidDuration is < 0 or > int.MaxValue / 60)
            {
                throw Invalid(sourceKey, $"map '{map.Id}' raid duration cannot be stored as seconds");
            }

            var extracts = Present(map.Extracts, sourceKey, $"map '{map.Id}' extracts");
            var transits = Present(map.Transits, sourceKey, $"map '{map.Id}' transits");
            var locks = Present(map.Locks, sourceKey, $"map '{map.Id}' locks");
            var spawns = Present(map.Spawns, sourceKey, $"map '{map.Id}' spawns");
            var mapLootContainers = Present(map.LootContainers, sourceKey, $"map '{map.Id}' container positions");
            var looseLoot = Present(map.LootLoose, sourceKey, $"map '{map.Id}' loose-loot positions");
            _ = Present(map.Hazards, sourceKey, $"map '{map.Id}' hazards");
            foreach (var extract in extracts)
            {
                if (extract is null)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' extract is null");
                }

                Required(extract.Id, sourceKey, $"map '{map.Id}' extract id");
                OptionalText(extract.Name, sourceKey, $"map '{map.Id}' extract '{extract.Id}' name");
                OptionalIdentifier(extract.Faction, sourceKey, $"map '{map.Id}' extract '{extract.Id}' faction");
            }

            foreach (var transit in transits)
            {
                if (transit is null)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' transit is null");
                }

                Required(transit.Id, sourceKey, $"map '{map.Id}' transit id");
            }

            foreach (var mapLock in locks)
            {
                if (mapLock is null)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' lock is null");
                }

                Required(mapLock.Id, sourceKey, $"map '{map.Id}' lock id");
                OptionalIdentifier(mapLock.Key, sourceKey, $"map '{map.Id}' lock '{mapLock.Id}' key item id");
            }

            if (locks.Select(mapLock => mapLock.Id).Distinct(StringComparer.Ordinal).Count() != locks.Count)
            {
                throw Invalid(sourceKey, $"map '{map.Id}' contains duplicate lock ids");
            }

            foreach (var spawn in spawns)
            {
                if (spawn is null)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' spawn is null");
                }

                RequiredStrings(
                    Present(spawn.Sides, sourceKey, $"map '{map.Id}' spawn sides"),
                    sourceKey,
                    $"map '{map.Id}' spawn side");
                RequiredStrings(
                    Present(spawn.Categories, sourceKey, $"map '{map.Id}' spawn categories"),
                    sourceKey,
                    $"map '{map.Id}' spawn category");
                OptionalText(spawn.ZoneName, sourceKey, $"map '{map.Id}' spawn zone name");
            }

            if (mapLootContainers.Any(value => value is null) || looseLoot.Any(value => value is null))
            {
                throw Invalid(sourceKey, $"map '{map.Id}' loot position is null");
            }

            foreach (var container in mapLootContainers)
            {
                OptionalIdentifier(
                    container.LootContainer,
                    sourceKey,
                    $"map '{map.Id}' loot-container type id");
                if (Present(container.Items, sourceKey, $"map '{map.Id}' container item pool").Count > 0)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' container position also claims a loose-loot item pool");
                }
            }

            foreach (var loose in looseLoot)
            {
                if (!string.IsNullOrWhiteSpace(loose.LootContainer))
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' loose-loot position also claims a container type");
                }

                var itemIds = Present(loose.Items, sourceKey, $"map '{map.Id}' loose-loot item pool");
                RequiredStrings(itemIds, sourceKey, $"map '{map.Id}' loose-loot item id");
                if (itemIds.Count > 256 || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Count)
                {
                    throw Invalid(sourceKey, $"map '{map.Id}' loose-loot item pool is oversized or contains duplicate ids");
                }
            }

            foreach (var position in extracts.Select(value => value.Position)
                         .Concat(spawns.Select(value => value.Position))
                         .Concat(mapLootContainers.Select(value => value.Position))
                         .Concat(looseLoot.Select(value => value.Position)))
            {
                ValidatePosition(position, sourceKey, map.Id);
            }
        }
    }

    private static void ValidateTasks(TarkovDevTasksData data, string sourceKey)
    {
        var tasks = Present(data.Tasks, sourceKey, "tasks dictionary");
        ValidateDictionaryIdentities(tasks, sourceKey, "task", value => value.Id);
        foreach (var task in tasks.Values)
        {
            RequiredText(task.Name, sourceKey, $"task '{task.Id}' name");
            OptionalText(task.NormalizedName, sourceKey, $"task '{task.Id}' normalized name");
            OptionalIdentifier(task.Trader, sourceKey, $"task '{task.Id}' trader id");
            OptionalIdentifier(task.Map, sourceKey, $"task '{task.Id}' primary map id");
            OptionalIdentifier(task.FactionName, sourceKey, $"task '{task.Id}' faction");
            OptionalIdentifier(task.RequiredPrestige, sourceKey, $"task '{task.Id}' required prestige id");
            OptionalText(task.WikiLink, sourceKey, $"task '{task.Id}' wiki URL", MaximumUriUtf8Bytes);
            if (task.MinPlayerLevel is < 0 || task.AvailableDelaySecondsMin is < 0 ||
                task.AvailableDelaySecondsMax is < 0)
            {
                throw Invalid(sourceKey, $"task '{task.Id}' contains a negative level or delay");
            }

            RequiredStrings(
                Present(task.GameMode, sourceKey, $"task '{task.Id}' game modes"),
                sourceKey,
                $"task '{task.Id}' game mode");
            foreach (var requirement in Present(task.TaskRequirements, sourceKey, $"task '{task.Id}' requirements"))
            {
                if (requirement is null)
                {
                    throw Invalid(sourceKey, $"task '{task.Id}' requirement is null");
                }

                Required(requirement.Task, sourceKey, $"task '{task.Id}' prerequisite task id");
                RequiredStrings(
                    Present(requirement.Status, sourceKey, $"task '{task.Id}' requirement statuses"),
                    sourceKey,
                    $"task '{task.Id}' requirement status");
            }

            ValidateObjectives(
                task.Id,
                Present(task.Objectives, sourceKey, $"task '{task.Id}' objectives"),
                sourceKey,
                "objective");
            ValidateObjectives(
                task.Id,
                Present(task.FailConditions, sourceKey, $"task '{task.Id}' failure conditions"),
                sourceKey,
                "failure condition");
        }
    }

    private static void ValidateObjectives(
        string taskId,
        IReadOnlyList<TarkovDevTaskObjective> objectives,
        string sourceKey,
        string kind)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in objectives)
        {
            if (objective is null)
            {
                throw Invalid(sourceKey, $"task '{taskId}' {kind} is null");
            }

            Required(objective.Id, sourceKey, $"task '{taskId}' {kind} id");
            Required(objective.Type, sourceKey, $"task '{taskId}' {kind} type");
            OptionalText(
                objective.Description,
                sourceKey,
                $"task '{taskId}' {kind} description",
                MaximumDescriptionUtf8Bytes);
            OptionalIdentifier(objective.Item, sourceKey, $"task '{taskId}' {kind} item id");
            OptionalIdentifier(objective.QuestItem, sourceKey, $"task '{taskId}' {kind} quest item id");
            OptionalIdentifier(objective.MarkerItem, sourceKey, $"task '{taskId}' {kind} marker item id");
            OptionalIdentifier(objective.Task, sourceKey, $"task '{taskId}' {kind} target task id");
            // Objective ids used to be globally unique across every task, but upstream now
            // reuses one id for the same underlying objective shared by several tasks (e.g. a
            // "find quest item" step common to a quest chain), so uniqueness is scoped to this
            // task's own objective list rather than the whole catalog.
            Unique(ids, objective.Id, sourceKey, $"task '{taskId}' {kind} id");

            if (objective.Count is < 0)
            {
                throw Invalid(sourceKey, $"task '{taskId}' {kind} count is negative");
            }

            RequiredStrings(
                Present(objective.Items, sourceKey, $"task '{taskId}' {kind} items"),
                sourceKey,
                $"task '{taskId}' {kind} item id");
            RequiredStrings(
                Present(objective.UseAny, sourceKey, $"task '{taskId}' {kind} use-any items"),
                sourceKey,
                $"task '{taskId}' {kind} use-any item id");
            _ = Present(objective.RequiredKeys, sourceKey, $"task '{taskId}' {kind} required keys");
            RequiredStrings(
                Present(objective.Maps, sourceKey, $"task '{taskId}' {kind} maps"),
                sourceKey,
                $"task '{taskId}' {kind} map id");
            RequiredStrings(
                Present(objective.Status, sourceKey, $"task '{taskId}' {kind} statuses"),
                sourceKey,
                $"task '{taskId}' {kind} status");
            foreach (var requiredKeys in objective.RequiredKeys)
            {
                RequiredStrings(
                    Present(requiredKeys, sourceKey, $"task '{taskId}' {kind} required-key group"),
                    sourceKey,
                    $"task '{taskId}' {kind} required-key item id");
            }

            foreach (var zone in Present(objective.Zones, sourceKey, $"task '{taskId}' {kind} zones"))
            {
                if (zone is null)
                {
                    throw Invalid(sourceKey, $"task '{taskId}' {kind} zone is null");
                }

                Finite(zone.Bottom, sourceKey, $"task '{taskId}' zone bottom elevation");
                Finite(zone.Top, sourceKey, $"task '{taskId}' zone top elevation");
                Finite(zone.TerrainElevation, sourceKey, $"task '{taskId}' zone terrain elevation");
                OptionalIdentifier(zone.Id, sourceKey, $"task '{taskId}' zone id");
                OptionalIdentifier(zone.Map, sourceKey, $"task '{taskId}' zone map id");
                OptionalText(zone.Name, sourceKey, $"task '{taskId}' zone name");
                ValidateObjectivePosition(zone.Position, sourceKey, taskId);
                ValidateObjectivePosition(zone.Size, sourceKey, taskId);
                foreach (var position in Present(zone.Outline, sourceKey, $"task '{taskId}' zone outline"))
                {
                    ValidateObjectivePosition(position, sourceKey, taskId);
                }
            }

            foreach (var location in Present(
                         objective.PossibleLocations,
                         sourceKey,
                         $"task '{taskId}' {kind} possible locations"))
            {
                if (location is null)
                {
                    throw Invalid(sourceKey, $"task '{taskId}' {kind} possible location is null");
                }

                foreach (var position in Present(location.Positions, sourceKey, $"task '{taskId}' location positions"))
                {
                    ValidateObjectivePosition(position, sourceKey, taskId);
                }

                OptionalIdentifier(location.Map, sourceKey, $"task '{taskId}' possible-location map id");
            }
        }
    }

    private static void ValidateObjectivePosition(
        TarkovDevObjectivePosition? position,
        string sourceKey,
        string taskId)
    {
        if (position is null)
        {
            return;
        }

        Finite(position.X, sourceKey, $"task '{taskId}' zone x coordinate");
        Finite(position.Y, sourceKey, $"task '{taskId}' zone y coordinate");
        Finite(position.Z, sourceKey, $"task '{taskId}' zone z coordinate");
    }

    private static void ValidateHideout(
        IReadOnlyDictionary<string, TarkovDevHideoutStation> data,
        string sourceKey)
    {
        _ = Present(data, sourceKey, "hideout dictionary");
        ValidateDictionaryIdentities(data, sourceKey, "hideout station", value => value.Id);
        foreach (var station in data.Values)
        {
            RequiredText(station.Name, sourceKey, $"hideout station '{station.Id}' name");
            var levelIds = new HashSet<string>(StringComparer.Ordinal);
            var levels = new HashSet<int>();
            foreach (var level in Present(station.Levels, sourceKey, $"hideout station '{station.Id}' levels"))
            {
                if (level is null)
                {
                    throw Invalid(sourceKey, $"hideout station '{station.Id}' level is null");
                }

                Required(level.Id, sourceKey, $"hideout station '{station.Id}' level id");
                Unique(levelIds, level.Id, sourceKey, $"hideout station '{station.Id}' level id");
                if (!levels.Add(level.Level))
                {
                    throw Invalid(sourceKey, $"hideout station '{station.Id}' contains duplicate level {level.Level}");
                }

                if (level.Level < 0)
                {
                    throw Invalid(sourceKey, $"hideout station '{station.Id}' level is negative");
                }

                foreach (var requirement in Present(level.ItemRequirements, sourceKey, "hideout item requirements"))
                {
                    ValidateRequirement(requirement, sourceKey, "hideout requirement");
                }

                foreach (var requirement in Present(level.StationLevelRequirements, sourceKey, "hideout station requirements"))
                {
                    if (requirement is null)
                    {
                        throw Invalid(sourceKey, "hideout station requirement is null");
                    }

                    Required(requirement.Station, sourceKey, "hideout station requirement id");
                    NonNegative(requirement.Level, sourceKey, "hideout station requirement level");
                }

                foreach (var requirement in Present(level.TraderRequirements, sourceKey, "hideout trader requirements"))
                {
                    if (requirement is null)
                    {
                        throw Invalid(sourceKey, "hideout trader requirement is null");
                    }

                    Required(requirement.Trader, sourceKey, "hideout trader requirement id");
                    OptionalIdentifier(requirement.RequirementType, sourceKey, "hideout trader requirement type");
                    OptionalIdentifier(requirement.CompareMethod, sourceKey, "hideout trader comparison method");
                    if (requirement.Level is null && requirement.Value is null)
                    {
                        throw Invalid(sourceKey, "hideout trader requirement level is missing");
                    }

                    if (requirement.Level is { } levelValue && requirement.Value is { } comparisonValue &&
                        levelValue != comparisonValue)
                    {
                        throw Invalid(sourceKey, "hideout trader requirement level spellings disagree");
                    }

                    NonNegative(requirement.RequiredLevel, sourceKey, "hideout trader requirement level");
                }

                foreach (var requirement in Present(level.SkillRequirements, sourceKey, "hideout skill requirements"))
                {
                    if (requirement is null)
                    {
                        throw Invalid(sourceKey, "hideout skill requirement is null");
                    }

                    Required(requirement.Skill, sourceKey, "hideout skill requirement id");
                    NonNegative(requirement.Level, sourceKey, "hideout skill requirement level");
                }
            }
        }
    }

    private static void ValidateTraders(
        IReadOnlyDictionary<string, TarkovDevTrader> data,
        string sourceKey)
    {
        _ = Present(data, sourceKey, "trader dictionary");
        ValidateDictionaryIdentities(data, sourceKey, "trader", value => value.Id);
        foreach (var trader in data.Values)
        {
            RequiredText(trader.Name, sourceKey, $"trader '{trader.Id}' name");
            var levelIds = new HashSet<string>(StringComparer.Ordinal);
            var levels = new HashSet<int>();
            foreach (var level in Present(trader.Levels, sourceKey, $"trader '{trader.Id}' levels"))
            {
                if (level is null)
                {
                    throw Invalid(sourceKey, $"trader '{trader.Id}' level is null");
                }

                Required(level.Id, sourceKey, $"trader '{trader.Id}' level id");
                Unique(levelIds, level.Id, sourceKey, $"trader '{trader.Id}' level id");
                if (!levels.Add(level.Level))
                {
                    throw Invalid(sourceKey, $"trader '{trader.Id}' contains duplicate level {level.Level}");
                }

                if (level.Level < 0)
                {
                    throw Invalid(sourceKey, $"trader '{trader.Id}' level is negative");
                }
            }
        }
    }

    private static void ValidateDictionaryIdentities<T>(
        IReadOnlyDictionary<string, T> data,
        string sourceKey,
        string kind,
        Func<T, string> id)
    {
        foreach (var pair in data)
        {
            Required(pair.Key, sourceKey, $"{kind} dictionary key");
            if (pair.Value is null)
            {
                throw Invalid(sourceKey, $"{kind} '{pair.Key}' is null");
            }

            var valueId = id(pair.Value);
            Required(valueId, sourceKey, $"{kind} id");
            if (!string.Equals(pair.Key, valueId, StringComparison.Ordinal))
            {
                throw Invalid(sourceKey, $"{kind} dictionary key '{pair.Key}' does not match id '{valueId}'");
            }
        }
    }

    private static void ValidatePosition(TarkovDevMapPosition? position, string sourceKey, string mapId)
    {
        if (position is not null &&
            (position.X is { } x && !double.IsFinite(x) ||
             position.Y is { } y && !double.IsFinite(y) ||
             position.Z is { } z && !double.IsFinite(z)))
        {
            throw Invalid(sourceKey, $"map '{mapId}' contains a non-finite coordinate");
        }
    }

    private static void ValidateRequirement(TarkovDevItemRequirement requirement, string sourceKey, string kind)
    {
        if (requirement is null)
        {
            throw Invalid(sourceKey, $"{kind} is null");
        }

        Required(requirement.Item, sourceKey, $"{kind} item id");
        if (requirement.Count <= 0)
        {
            throw Invalid(sourceKey, $"{kind} count is not positive");
        }
    }

    private static void Required(string? value, string sourceKey, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(sourceKey, $"{field} is missing");
        }

        BoundedText(value, sourceKey, field, MaximumIdentifierUtf8Bytes);
    }

    private static void RequiredText(string? value, string sourceKey, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(sourceKey, $"{field} is missing");
        }

        BoundedText(value, sourceKey, field, MaximumDisplayTextUtf8Bytes);
    }

    private static void OptionalIdentifier(string? value, string sourceKey, string field) =>
        OptionalText(value, sourceKey, field, MaximumIdentifierUtf8Bytes);

    private static void OptionalText(
        string? value,
        string sourceKey,
        string field,
        int maximumUtf8Bytes = MaximumDisplayTextUtf8Bytes)
    {
        if (value is not null)
        {
            BoundedText(value, sourceKey, field, maximumUtf8Bytes);
        }
    }

    private static void BoundedText(string value, string sourceKey, string field, int maximumUtf8Bytes)
    {
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw Invalid(sourceKey, $"{field} exceeds its {maximumUtf8Bytes:N0}-byte UTF-8 budget");
        }
    }

    private static T Present<T>(T? value, string sourceKey, string field)
        where T : class =>
        value ?? throw Invalid(sourceKey, $"{field} is null");

    private static void RequiredStrings(IEnumerable<string> values, string sourceKey, string field)
    {
        var ordinal = 0;
        foreach (var value in values)
        {
            Required(value, sourceKey, $"{field} at index {ordinal}");
            ordinal++;
        }
    }

    private static void NonNegative(long? value, string sourceKey, string field)
    {
        if (value < 0)
        {
            throw Invalid(sourceKey, $"{field} is negative");
        }
    }

    private static void NonNegative(int? value, string sourceKey, string field)
    {
        if (value < 0)
        {
            throw Invalid(sourceKey, $"{field} is negative");
        }
    }

    private static void Finite(double? value, string sourceKey, string field)
    {
        if (value is { } number && !double.IsFinite(number))
        {
            throw Invalid(sourceKey, $"{field} is not finite");
        }
    }

    private static void Unique(
        HashSet<string> values,
        string value,
        string sourceKey,
        string field)
    {
        if (!values.Add(value))
        {
            throw Invalid(sourceKey, $"{field} '{value}' occurs more than once");
        }
    }

    private static InvalidDataException Invalid(string sourceKey, string detail) =>
        new($"json.tarkov.dev response '{sourceKey}' cannot be normalized: {detail}.");
}
