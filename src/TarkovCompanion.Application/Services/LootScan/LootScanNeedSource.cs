using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// What the player still needs an item for, in the shape the recommendation engine ranks.
/// </summary>
/// <remarks>
/// <para>
/// The Loot Scan passed the engine an empty need list and a status that said so, which is why a
/// quest hand-in lying in a container came back "valued, not decided". Everything it needed
/// was already in the app: the requirement rows the item scanner answers from, the profile's
/// progress, and the quest board that knows which quests the player is on.
/// </para>
/// <para>
/// How near a quest is comes from the board and nowhere else. A quest the player is on, or has
/// pinned, is current. Any other is as many steps ahead as the longest chain of unfinished
/// prerequisites in front of it, and at least one. The engine ignores a quest further off than
/// its horizon, so on a fresh wipe the whole game is not a reason to take.
/// </para>
/// </remarks>
public sealed class LootScanNeedSource(
    IPlayerProfileService profiles,
    ProfileNeedAggregationService aggregation,
    IQuestReadService quests,
    IRequirementCatalog requirements)
{
    private readonly IPlayerProfileService _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly ProfileNeedAggregationService _aggregation = aggregation ?? throw new ArgumentNullException(nameof(aggregation));
    private readonly IQuestReadService _quests = quests ?? throw new ArgumentNullException(nameof(quests));
    private readonly IRequirementCatalog _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));

    /// <summary>Reads the profile, the board and the station names once for a whole scan.</summary>
    public async Task<LootScanNeedSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<QuestSummaryReadModel>? board = null;
        try
        {
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var read = await _quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            board = read.UnavailableReason is null ? read.Tasks : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without the board nothing says how near a quest is, and the snapshot reports that
            // rather than calling every quest current.
        }

        IReadOnlyDictionary<string, string> stations;
        try
        {
            stations = (await _requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false))
                .GroupBy(station => station.StationId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stations = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new(profile, _aggregation, board, stations);
    }
}

/// <summary>One scan's view of the player's needs: read once, asked once per item.</summary>
public sealed class LootScanNeedSnapshot
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion loot scan", "loot-scan-need-source-1");

    /// <summary>Further than any horizon a policy may set, for a quest whose distance cannot be read.</summary>
    private const int BeyondHorizon = 1000;

    private readonly PlayerProfile _profile;
    private readonly ProfileNeedAggregationService _aggregation;
    private readonly IReadOnlyDictionary<string, QuestSummaryReadModel>? _board;
    private readonly IReadOnlyDictionary<string, string> _stations;
    private readonly Dictionary<string, int> _steps = new(StringComparer.Ordinal);

    internal LootScanNeedSnapshot(
        PlayerProfile profile,
        ProfileNeedAggregationService aggregation,
        IReadOnlyList<QuestSummaryReadModel>? board,
        IReadOnlyDictionary<string, string> stations)
    {
        _profile = profile;
        _aggregation = aggregation;
        _board = board?
            .GroupBy(task => task.TaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _stations = stations;
    }

    /// <summary>Whether the quest board was read. Without it quests are listed but not ranked as near.</summary>
    public bool QuestBoardRead => _board is not null;

    public IReadOnlyList<RecommendationNeed> NeedsFor(string itemId, DateTimeOffset evaluatedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var outstanding = _aggregation.GetOutstandingRequirements(_profile, itemId);
        var status = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "need.outstanding");
        var needs = new List<RecommendationNeed>();
        foreach (var quest in outstanding.Quests)
        {
            var task = _board?.GetValueOrDefault(quest.Requirement.TaskId);
            needs.Add(new(
                $"quest.{quest.Requirement.TaskId}.{quest.Requirement.ObjectiveId}",
                task?.Name ?? "a quest",
                RecommendationNeedPurpose.Quest,
                StepsAhead(quest.Requirement.TaskId),
                quest.Remaining,
                quest.Requirement.FoundInRaidRequired,
                status,
                Provenance($"profile-needs/quest/{quest.Requirement.TaskId}", "json.tarkov.dev/tasks", evaluatedUtc)));
        }

        foreach (var level in outstanding.Hideout)
        {
            var station = _stations.GetValueOrDefault(level.Requirement.StationId) ?? "the hideout";
            needs.Add(new(
                $"hideout.{level.Requirement.StationId}.{level.Requirement.TargetLevel}",
                $"{station} level {level.Requirement.TargetLevel}",
                RecommendationNeedPurpose.Hideout,
                Math.Max(0, level.Requirement.TargetLevel - level.CurrentLevel - 1),
                level.Remaining,
                requiresFoundInRaid: false,
                status,
                Provenance($"profile-needs/hideout/{level.Requirement.StationId}", "json.tarkov.dev/hideout", evaluatedUtc)));
        }

        // The engine takes at most this many. Nearest first, so what is cut is the far future.
        return needs
            .GroupBy(need => need.NeedId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(need => need.StepsAhead)
            .ThenBy(need => need.NeedId, StringComparer.Ordinal)
            .Take(RecommendationProfileFacts.MaximumNeeds)
            .ToArray();
    }

    private int StepsAhead(string taskId) => StepsAhead(taskId, []);

    private int StepsAhead(string taskId, HashSet<string> visiting)
    {
        if (_board is null || !_board.TryGetValue(taskId, out var task))
        {
            return BeyondHorizon;
        }

        if (task.IsPinned || task.RecordedState == RecordedTaskState.Active)
        {
            return 0;
        }

        if (_steps.TryGetValue(taskId, out var known))
        {
            return known;
        }

        if (!visiting.Add(taskId))
        {
            // A cycle in the catalog is not a distance.
            return BeyondHorizon;
        }

        // A quest nothing stands in front of is one step ahead: accepting it. Each unfinished
        // prerequisite is its own distance plus the step of finishing it, so the quest after
        // one the player is on is also one step ahead, and the one after that is two.
        var steps = 1;
        foreach (var prerequisite in task.Prerequisites.Where(item => item.RecordedState != RecordedTaskState.Completed))
        {
            var ahead = StepsAhead(prerequisite.RequiredTaskId, visiting);
            steps = Math.Max(steps, ahead >= BeyondHorizon ? BeyondHorizon : ahead + 1);
        }

        visiting.Remove(taskId);
        _steps[taskId] = steps;
        return steps;
    }

    private static EvidenceProvenance Provenance(string identifier, string catalog, DateTimeOffset evaluatedUtc) =>
        new(
            EvidenceSourceClass.DerivedCalculation,
            identifier,
            evaluatedUtc,
            EvidenceConfidence.Certain,
            Producer,
            generatedUtc: evaluatedUtc,
            inputs:
            [
                new(EvidenceSourceClass.PublicStructuredData, catalog, evaluatedUtc, EvidenceConfidence.Certain, Producer),
                new(EvidenceSourceClass.UserEntered, "profile/progress", evaluatedUtc, EvidenceConfidence.Certain, Producer),
            ]);
}
