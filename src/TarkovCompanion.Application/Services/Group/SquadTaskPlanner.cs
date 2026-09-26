using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>[#712 T7] One objective of a shared quest on the planned map.</summary>
/// <param name="Members">Who has it open, in squad order.</param>
/// <param name="HasPlace">Whether the catalog places it on this map, so the route can visit it.</param>
public sealed record SquadPlanObjective(
    string ObjectiveId,
    string Description,
    IReadOnlyList<string> Members,
    bool HasPlace)
{
    /// <summary>Two or more of the squad have it open: one trip does it for all of them.</summary>
    public bool Together => Members.Count >= 2;
}

/// <summary>[#712 T7] A quest two or more of the squad have active, with what is left of it on one map.</summary>
/// <param name="Order">Where it comes in the suggested order, from 1.</param>
/// <param name="Members">Everyone who has the quest active, in squad order (this player first).</param>
/// <param name="Objectives">Its open objectives on this map, together ones first.</param>
public sealed record SquadPlanQuest(
    string TaskId,
    string Name,
    int Order,
    IReadOnlyList<string> Members,
    IReadOnlyList<SquadPlanObjective> Objectives)
{
    /// <summary>At least one objective here is open for two or more of the squad.</summary>
    public bool HasTogether => Objectives.Any(objective => objective.Together);
}

/// <summary>[#712 T7] One map and the shared quests with something left to do on it.</summary>
/// <param name="MapId">The map, by quest catalog id.</param>
public sealed record SquadPlanMap(string MapId, IReadOnlyList<SquadPlanQuest> Quests)
{
    /// <summary>How many of the squad's objectives here are open for two or more of them.</summary>
    public int TogetherCount => Quests.Sum(quest => quest.Objectives.Count(objective => objective.Together));
}

/// <summary>
/// [#712 T7] The shared-task planner: from the squad's quest sync (#780), the quests two or more of
/// them have active, grouped by the maps where their open objectives are, with a suggested order
/// and which objectives one trip does for several of them.
/// </summary>
/// <remarks>
/// <para>
/// Counted per quest, the Tonight rule (TonightMaps): a quest with four things to do on Customs is
/// one reason to queue Customs. A shared quest counts for a map when any member who has it still has
/// an objective open there, placed by the same rule as the Raid map's own pins
/// (SquadQuestResolver.MapIdsOf).
/// </para>
/// <para>
/// Everything is resolved from this player's own catalog; only ids crossed the relay. A member whose
/// companion sent quest ids but no objectives (an older build) still counts towards "who has it", but
/// adds no objective, because nothing says what is left of it for them.
/// </para>
/// <para>
/// The order is a suggestion, and the reasons are the ones the squad can check: quests more of them
/// share first (one trip helps the most people), then quests with an objective several of them have
/// open, then more objectives here, then by name. Distance is left to the Raid map's objective route,
/// which starts from where the player actually is.
/// </para>
/// </remarks>
public static class SquadTaskPlanner
{
    /// <summary>Maps worth offering, as TonightMaps.</summary>
    public const int MapLimit = 6;

    /// <summary>Every map with a shared quest to work on, most shared quests first.</summary>
    public static IReadOnlyList<SquadPlanMap> Plan(SquadQuestPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (picture.SharedTaskIds.Count == 0)
        {
            return [];
        }

        var order = picture.Members.Select((member, index) => (member.Name, index))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var mapIds = picture.Members
            .SelectMany(member => member.Quests)
            .Where(quest => picture.SharedTaskIds.Contains(quest.TaskId))
            .SelectMany(quest => quest.Open)
            .SelectMany(objective => objective.MapIds)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. mapIds
                .Select(mapId => ForMap(picture, mapId, order))
                .Where(map => map.Quests.Count > 0)
                .OrderByDescending(map => map.Quests.Count)
                .ThenByDescending(map => map.TogetherCount)
                .ThenBy(map => map.MapId, StringComparer.OrdinalIgnoreCase)
                .Take(MapLimit),
        ];
    }

    /// <summary>The shared quests with an open objective on one map, in the suggested order.</summary>
    public static SquadPlanMap ForMap(SquadQuestPicture picture, string mapId) =>
        ForMap(
            picture ?? throw new ArgumentNullException(nameof(picture)),
            mapId,
            picture.Members.Select((member, index) => (member.Name, index))
                .GroupBy(pair => pair.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal));

    private static SquadPlanMap ForMap(SquadQuestPicture picture, string mapId, IReadOnlyDictionary<string, int> memberOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var quests = new List<SquadPlanQuest>();
        foreach (var taskId in picture.SharedTaskIds)
        {
            var holders = picture.Members
                .Select(member => (member.Name, Quest: member.Quests.FirstOrDefault(quest => quest.TaskId == taskId)))
                .Where(pair => pair.Quest is not null)
                .ToArray();
            if (holders.Length < 2)
            {
                continue;
            }

            var objectives = holders
                .SelectMany(holder => holder.Quest!.Open
                    .Where(open => open.MapIds.Contains(mapId, StringComparer.OrdinalIgnoreCase))
                    .Select(open => (holder.Name, Open: open)))
                .GroupBy(pair => pair.Open.Objective.Id, StringComparer.Ordinal)
                .Select(group => new SquadPlanObjective(
                    group.Key,
                    group.First().Open.Objective.Description,
                    [.. group.Select(pair => pair.Name).Distinct(StringComparer.Ordinal).OrderBy(name => memberOrder.GetValueOrDefault(name))],
                    HasPlace(group.First().Open.Objective, mapId)))
                .OrderByDescending(objective => objective.Members.Count)
                .ThenBy(objective => objective.Description, StringComparer.CurrentCulture)
                .ToArray();
            if (objectives.Length == 0)
            {
                continue;
            }

            quests.Add(new(
                taskId,
                holders[0].Quest!.Name,
                0,
                [.. holders.Select(holder => holder.Name).OrderBy(name => memberOrder.GetValueOrDefault(name))],
                objectives));
        }

        return new(mapId,
        [
            .. quests
                .OrderByDescending(quest => quest.Members.Count)
                .ThenByDescending(quest => quest.HasTogether)
                .ThenByDescending(quest => quest.Objectives.Count)
                .ThenBy(quest => quest.Name, StringComparer.CurrentCulture)
                .Select((quest, index) => quest with { Order = index + 1 }),
        ]);
    }

    /// <summary>Whether the catalog gives the objective a spot on this map (a zone with a position).</summary>
    private static bool HasPlace(QuestObjectiveDefinition objective, string mapId) =>
        objective.Zones.Any(zone =>
            zone.Position is not null &&
            (zone.MapId is null || string.Equals(zone.MapId, mapId, StringComparison.OrdinalIgnoreCase)));
}
