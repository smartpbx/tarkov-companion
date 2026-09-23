using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>[#780] One member's quest ids and open objectives, as the exchange delivered them.</summary>
/// <param name="Name">The member's display name; "You" for this player's own row.</param>
/// <param name="IsSelf">Whether this is this player's own list rather than a squadmate's.</param>
/// <param name="QuestIds">Their active (and pinned) quests, by catalog id.</param>
/// <param name="Objectives">The open objectives of their active quests.</param>
public sealed record SquadMemberQuestIds(
    string Name,
    bool IsSelf,
    IReadOnlyList<string> QuestIds,
    IReadOnlyList<GroupObjectiveView> Objectives);

/// <summary>[#780] One open objective a squadmate has, named and placed from this catalog.</summary>
public sealed record SquadObjective(
    string MemberName,
    QuestTaskDefinition Task,
    QuestObjectiveDefinition Objective,
    decimal? Count,
    IReadOnlyList<string> MapIds);

/// <summary>[#780] One quest a member is on, with how far along it is.</summary>
/// <param name="ObjectiveCount">Required objectives the quest has.</param>
/// <param name="OpenCount">Of those, how many are still open for this member, where they said.</param>
/// <param name="ReportsObjectives">
/// False for a member whose companion sent no objectives at all (an older build, or a relay that
/// drops them), so the row says nothing about progress instead of claiming it is all done.
/// </param>
public sealed record SquadQuest(
    string TaskId,
    string Name,
    int ObjectiveCount,
    int OpenCount,
    bool ReportsObjectives,
    IReadOnlyList<SquadObjective> Open)
{
    public int DoneCount => Math.Max(0, ObjectiveCount - OpenCount);
}

/// <summary>[#780] A member and the quests of theirs this catalog knows.</summary>
public sealed record SquadMemberQuests(string Name, bool IsSelf, IReadOnlyList<SquadQuest> Quests);

/// <summary>[#780] The squad's quests, resolved against this player's own catalog.</summary>
/// <param name="SharedTaskIds">Quests two or more members (this player included) have active.</param>
public sealed record SquadQuestPicture(
    IReadOnlyList<SquadMemberQuests> Members,
    IReadOnlySet<string> SharedTaskIds,
    QuestProfileScope? Scope,
    QuestCatalogProvenance? Provenance)
{
    public static SquadQuestPicture Empty { get; } =
        new([], new HashSet<string>(StringComparer.Ordinal), null, null);

    /// <summary>Every squadmate's open objectives (not this player's own), in member order.</summary>
    public IEnumerable<SquadObjective> SquadmateObjectives =>
        Members.Where(member => !member.IsSelf)
            .SelectMany(member => member.Quests)
            .SelectMany(quest => quest.Open);

    /// <summary>
    /// The squadmates' open objectives on one map, as the read model the quest layer projects.
    /// </summary>
    /// <remarks>
    /// One entry per objective: two squadmates on the same objective is one place to go, drawn in
    /// the first one's colour. An objective this player has open too is left out, because their
    /// own pin is already on the map and a second one on top of it would say nothing new.
    /// </remarks>
    public QuestMapObjectivesReadModel? MapQuery(IReadOnlyCollection<string> mapIds, IReadOnlySet<string> ownOpenObjectiveIds)
    {
        ArgumentNullException.ThrowIfNull(mapIds);
        ArgumentNullException.ThrowIfNull(ownOpenObjectiveIds);
        if (Scope is null || Provenance is null || mapIds.Count == 0)
        {
            return null;
        }

        var requested = mapIds.ToArray();
        var objectives = SquadmateObjectives
            .Where(objective => !ownOpenObjectiveIds.Contains(objective.Objective.Id))
            .Where(objective => objective.MapIds.Any(map => requested.Contains(map, StringComparer.OrdinalIgnoreCase)))
            .DistinctBy(objective => objective.Objective.Id, StringComparer.Ordinal)
            .Select(objective => new QuestMapObjectiveReadModel(
                objective.Task.Id,
                objective.Task.Name,
                objective.Task.TraderId,
                objective.Objective.Id,
                objective.Objective.SourceOrdinal,
                objective.Objective.Description,
                objective.Objective.Kind,
                objective.Objective.IsUnsupported,
                objective.Objective.Optional,
                RecordedTaskState.Active,
                RecordedObjectiveState.Unknown,
                false,
                false,
                null,
                $"Shared by {objective.MemberName}",
                null,
                objective.Objective.FoundInRaidRequired,
                objective.MapIds,
                objective.Objective.Zones,
                objective.Objective.ItemTargets)
            {
                TargetCount = objective.Objective.TargetCount,
                RecordedCount = objective.Count,
                WikiUri = objective.Task.WikiUri,
            })
            .ToArray();
        return new(Scope, 0, Provenance, requested, objectives, []);
    }

    /// <summary>Which squadmate an objective drawn from <see cref="MapQuery"/> belongs to.</summary>
    public string? MemberFor(string objectiveId) =>
        SquadmateObjectives.FirstOrDefault(objective => objective.Objective.Id == objectiveId)?.MemberName;
}

/// <summary>
/// [#780] Names and places the squad's shared quest ids from this player's own quest catalog.
/// </summary>
/// <remarks>
/// Only ids cross the relay; everything readable here (quest names, objective text, maps, zones)
/// comes from the receiver's own copy of the catalog. An id this copy does not know is a quest
/// added since it last synced, and is passed over rather than guessed at.
/// </remarks>
public sealed class SquadQuestResolver(
    IQuestCatalog catalog,
    IPlayerProfileService profiles,
    QuestTrackingOptions options,
    TimeProvider? timeProvider = null)
{
    /// <summary>The catalog changes on a data sync, not between exchanges.</summary>
    private static readonly TimeSpan RereadCatalogAfter = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (QuestProfileScope Scope, QuestCatalogSnapshot? Catalog, IReadOnlyDictionary<string, QuestTaskDefinition> Tasks, DateTimeOffset ReadUtc)? _cached;

    public async Task<SquadQuestPicture> ResolveAsync(
        IReadOnlyList<SquadMemberQuestIds> members,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            return SquadQuestPicture.Empty;
        }

        var (scope, snapshot, tasks) = await CatalogAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? SquadQuestPicture.Empty
            : Resolve(members, tasks) with { Scope = scope, Provenance = snapshot.Provenance };
    }

    private async Task<(QuestProfileScope Scope, QuestCatalogSnapshot? Catalog, IReadOnlyDictionary<string, QuestTaskDefinition> Tasks)> CatalogAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            if (_cached is { } cached && cached.Scope == scope && _time.GetUtcNow() - cached.ReadUtc < RereadCatalogAfter)
            {
                return (cached.Scope, cached.Catalog, cached.Tasks);
            }

            var snapshot = await catalog.GetAsync(profile.GameMode, options.NormalizedLanguage, cancellationToken).ConfigureAwait(false);
            var tasks = snapshot?.Tasks
                .GroupBy(task => task.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, QuestTaskDefinition>(StringComparer.Ordinal);
            _cached = (scope, snapshot, tasks, _time.GetUtcNow());
            return (scope, snapshot, tasks);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resolves every member's ids against one catalog; pure, so it is tested directly.</summary>
    public static SquadQuestPicture Resolve(
        IReadOnlyList<SquadMemberQuestIds> members,
        IReadOnlyDictionary<string, QuestTaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(tasks);
        var resolved = members
            .Select(member => new SquadMemberQuests(member.Name, member.IsSelf, Quests(member, tasks)))
            .ToArray();
        var shared = resolved
            .SelectMany(member => member.Quests.Select(quest => quest.TaskId).Distinct(StringComparer.Ordinal))
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new(resolved, shared, null, null);
    }

    private static IReadOnlyList<SquadQuest> Quests(
        SquadMemberQuestIds member,
        IReadOnlyDictionary<string, QuestTaskDefinition> tasks)
    {
        var reports = member.Objectives.Count > 0;
        var openByTask = member.Objectives
            .GroupBy(objective => objective.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var quests = new List<SquadQuest>();
        foreach (var taskId in member.QuestIds.Distinct(StringComparer.Ordinal))
        {
            if (!tasks.TryGetValue(taskId, out var task))
            {
                continue;
            }

            var byId = task.Objectives.ToDictionary(objective => objective.Id, StringComparer.Ordinal);
            var open = (openByTask.GetValueOrDefault(taskId) ?? [])
                .Where(shared => byId.ContainsKey(shared.ObjectiveId))
                .Select(shared => new SquadObjective(
                    member.Name,
                    task,
                    byId[shared.ObjectiveId],
                    shared.Count,
                    MapIdsOf(task, byId[shared.ObjectiveId])))
                .ToArray();
            var required = task.Objectives.Where(objective => objective.Optional != true).ToArray();
            quests.Add(new(
                task.Id,
                task.Name,
                required.Length,
                open.Count(objective => objective.Objective.Optional != true),
                reports,
                open));
        }

        return quests;
    }

    /// <summary>The maps an objective is on, with the quest's own map where the objective names none.</summary>
    /// <remarks>The same rule the player's own objectives are placed by (QuestReadService).</remarks>
    internal static IReadOnlyList<string> MapIdsOf(QuestTaskDefinition task, QuestObjectiveDefinition objective)
    {
        var mapIds = objective.MapAssociations
            .Select(association => association.MapId)
            .Concat(objective.Zones.Where(zone => zone.MapId is not null).Select(zone => zone.MapId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return mapIds.Length == 0 && task.PrimaryMapId is not null ? [task.PrimaryMapId] : mapIds;
    }
}
