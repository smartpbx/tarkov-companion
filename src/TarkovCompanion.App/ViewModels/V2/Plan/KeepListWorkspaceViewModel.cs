using System.Globalization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>Which group of the Keep list one row sorts under.</summary>
public enum KeepListReasonKind
{
    ActiveQuest,
    Quest,
    Hideout,
    Key,
    HighValue,
}

/// <summary>One item worth keeping, and every reason the synced data has for saying so.</summary>
public sealed record KeepListRowViewModel(
    string ItemId,
    string Name,
    string Tier,
    bool IsHighValue,
    IReadOnlyList<string> Reasons)
{
    public string ReasonSummary => string.Join(", ", Reasons);
}

public sealed record KeepListGroupViewModel(string Label, IReadOnlyList<KeepListRowViewModel> Items)
{
    public string Heading => $"{Label} ({Items.Count})";
}

/// <summary>
/// V2 rough package 25: db4tarkov calls its "items to keep" list editorial. This one is computed
/// from data the companion already syncs — quest and hideout item requirements, key market prices
/// — plus the profile's own recorded progress. Lives under the Plan route (issue #402) because
/// every input already does: the Plan and Hideout tabs read the same requirement catalog and
/// profile, and "what to keep" is a planning question, not a stash-organising one.
/// </summary>
/// <remarks>
/// Reuses <see cref="IQuestProgressService"/>-shaped facts (quest/hideout need) and
/// <see cref="ValueTierThresholds"/> (the same tier table <c>RecommendationEngine</c> uses) rather
/// than inventing a second scoring pass. The full <c>ExplainableRecommendationEngine</c> is not
/// called here: it decides what to do with one scanned cell (take/sell/drop) and needs placement
/// and capacity context this list has no reason to have. All this list needs from it is the tier
/// a value-per-slot number falls into, so it asks the threshold table directly.
///
/// Keys reuse <see cref="KeyValue"/> exactly as the V1 Keys page does: a key is worth keeping when
/// a tracked or outstanding quest needs it, a hideout build needs it, or the market prices it in
/// the top quarter of cached keys (Clayton's "the flea price of a key already prices its loot and
/// keep value too").
/// </remarks>
public sealed class KeepListWorkspaceViewModel : BindableViewModel
{
    private static readonly IReadOnlyList<KeepListReasonKind> GroupOrder =
    [
        KeepListReasonKind.ActiveQuest,
        KeepListReasonKind.Quest,
        KeepListReasonKind.Hideout,
        KeepListReasonKind.Key,
        KeepListReasonKind.HighValue,
    ];

    private static readonly IReadOnlyDictionary<KeepListReasonKind, string> GroupLabels =
        new Dictionary<KeepListReasonKind, string>
        {
            [KeepListReasonKind.ActiveQuest] = "Quests you're on",
            [KeepListReasonKind.Quest] = "Quests ahead of you",
            [KeepListReasonKind.Hideout] = "Hideout upgrades",
            [KeepListReasonKind.Key] = "Keys worth keeping",
            [KeepListReasonKind.HighValue] = "High value",
        };

    private readonly IRequirementCatalog _requirements;
    private readonly IPlayerProfileService _profileService;
    private readonly IItemRepository _itemRepository;
    private readonly IItemFactCatalog _factCatalog;
    private readonly IQuestReadService _questReadService;
    private IReadOnlyList<KeepListGroupViewModel> _groups = [];
    private string _status = "Loading the keep list…";

    public KeepListWorkspaceViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository,
        IItemFactCatalog factCatalog,
        IQuestReadService questReadService)
    {
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        _factCatalog = factCatalog ?? throw new ArgumentNullException(nameof(factCatalog));
        _questReadService = questReadService ?? throw new ArgumentNullException(nameof(questReadService));
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public IReadOnlyList<KeepListGroupViewModel> Groups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                OnPropertyChanged(nameof(HasGroups));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var questRequirements = await _requirements.GetQuestRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var hideoutRequirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var keyFacts = await _factCatalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(true);
            if (questRequirements.Count == 0 && hideoutRequirements.Count == 0 && keyFacts.Count == 0)
            {
                Groups = [];
                Status = "No keep-list data cached yet.";
                return;
            }

            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var stations = await _requirements.GetStationsAsync(cancellationToken).ConfigureAwait(true);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await _questReadService.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(true);

            var (rows, groups) = await BuildAsync(
                profile,
                questRequirements,
                hideoutRequirements,
                stations,
                keyFacts,
                board,
                cancellationToken).ConfigureAwait(true);
            Groups = groups;
            Status = rows == 0
                ? "Nothing to keep right now — quests, hideout, and keys are all clear."
                : $"{Count(rows)} to keep";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Groups = [];
            Status = "Keep-list data isn't available yet.";
            WorkspaceFault.Record("keep", "refresh", exception);
        }
    }

    private async Task<(int RowCount, IReadOnlyList<KeepListGroupViewModel> Groups)> BuildAsync(
        PlayerProfile profile,
        IReadOnlyList<QuestItemRequirement> questRequirements,
        IReadOnlyList<HideoutItemRequirement> hideoutRequirements,
        IReadOnlyList<HideoutStationSummary> stations,
        IReadOnlyList<KeyFacts> keyFacts,
        QuestBoardReadModel board,
        CancellationToken cancellationToken)
    {
        var taskNames = board.Tasks.ToDictionary(task => task.TaskId, task => task.Name, StringComparer.Ordinal);
        var trackedTaskIds = board.Tasks
            .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
            .Select(task => task.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        var stationNames = stations.ToDictionary(
            station => station.StationId,
            station => station.Name,
            StringComparer.OrdinalIgnoreCase);

        // Item -> task -> quantity still outstanding. Only uncompleted tasks with something still
        // owed reach the map, so its keys are exactly the items a quest still asks for.
        var questByItem = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var trackedItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in questRequirements.Where(x => !profile.CompletedTaskIds.Contains(x.TaskId)))
        {
            var progress = profile.ObjectiveProgress.GetValueOrDefault(requirement.ObjectiveId);
            var remaining = Math.Max(0, requirement.Required - progress);
            if (remaining <= 0)
            {
                continue;
            }

            var byTask = questByItem.TryGetValue(requirement.ItemId, out var existing)
                ? existing
                : questByItem[requirement.ItemId] = new Dictionary<string, int>(StringComparer.Ordinal);
            byTask[requirement.TaskId] = byTask.GetValueOrDefault(requirement.TaskId) + remaining;
            if (trackedTaskIds.Contains(requirement.TaskId))
            {
                trackedItemIds.Add(requirement.ItemId);
            }
        }

        // Item -> station -> quantity the station's next build still asks for, before the
        // profile's own stock is subtracted (subtracted once at the item level below, the same
        // way ProfileNeedAggregationService does it, rather than per station).
        var hideoutByItem = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var requirement in hideoutRequirements.Where(x =>
                     profile.HideoutStationLevels.GetValueOrDefault(x.StationId) < x.TargetLevel))
        {
            var byStation = hideoutByItem.TryGetValue(requirement.ItemId, out var existing)
                ? existing
                : hideoutByItem[requirement.ItemId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byStation[requirement.StationId] = byStation.GetValueOrDefault(requirement.StationId) + requirement.Required;
        }

        var keyFactsById = keyFacts.ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);
        var keyRanks = KeyValue.Rank(keyFacts
            .Where(fact => fact.AcquisitionCostRoubles is > 0)
            .Select(fact => (fact.ItemId, fact.AcquisitionCostRoubles!.Value)));

        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        candidateIds.UnionWith(questByItem.Keys);
        candidateIds.UnionWith(hideoutByItem.Keys);
        candidateIds.UnionWith(keyFactsById.Keys);

        var byGroup = new Dictionary<KeepListReasonKind, List<KeepListRowViewModel>>();
        var rowCount = 0;
        foreach (var itemId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
            var reasons = new List<string>();
            var hasHideoutReason = false;
            var isTrackedQuest = trackedItemIds.Contains(itemId);
            var hasQuestReason = questByItem.TryGetValue(itemId, out var byTask);
            if (hasQuestReason)
            {
                foreach (var (taskId, remaining) in byTask!.OrderByDescending(x => x.Value))
                {
                    reasons.Add($"{Count(remaining)} for {taskNames.GetValueOrDefault(taskId, taskId)}");
                }
            }

            if (hideoutByItem.TryGetValue(itemId, out var byStation))
            {
                var owned = profile.OwnedItemCounts.GetValueOrDefault(itemId);
                var totalRequired = byStation.Values.Sum();
                if (Math.Max(0, totalRequired - owned) > 0)
                {
                    hasHideoutReason = true;
                    foreach (var (stationId, required) in byStation.OrderByDescending(x => x.Value))
                    {
                        reasons.Add($"{Count(required)} for {stationNames.GetValueOrDefault(stationId, stationId)}");
                    }
                }
            }

            var (tier, isHighValue) = await TierAsync(item, cancellationToken).ConfigureAwait(true);

            var hasKeyReason = false;
            if (keyFactsById.TryGetValue(itemId, out var keyFact))
            {
                var needs = new ItemNeedSummary(0, 0, hasHideoutReason ? 1 : 0)
                {
                    QuestsNeedingIt = hasQuestReason ? 1 : 0,
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
                    hasKeyReason = true;
                    if (!reasons.Contains(verdict.Reason, StringComparer.Ordinal))
                    {
                        reasons.Add(verdict.Reason);
                    }
                }
            }

            if (isHighValue)
            {
                reasons.Add("high value");
            }

            if (reasons.Count == 0)
            {
                continue;
            }

            var group = isTrackedQuest ? KeepListReasonKind.ActiveQuest
                : hasQuestReason ? KeepListReasonKind.Quest
                : hasHideoutReason ? KeepListReasonKind.Hideout
                : hasKeyReason ? KeepListReasonKind.Key
                : KeepListReasonKind.HighValue;
            (byGroup.TryGetValue(group, out var list) ? list : byGroup[group] = []).Add(new(
                itemId,
                item?.Name ?? itemId,
                tier,
                isHighValue,
                reasons));
            rowCount++;
        }

        var groups = GroupOrder
            .Where(byGroup.ContainsKey)
            .Select(kind => new KeepListGroupViewModel(
                GroupLabels[kind],
                byGroup[kind].OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            .ToArray();
        return (rowCount, groups);
    }

    /// <summary>
    /// The same S/A/B/C/D bands <c>RecommendationEngine</c> ranks scanned loot by. S and A read as
    /// "high value" here; nothing below that is worth a reason on its own.
    /// </summary>
    private async Task<(string Tier, bool IsHighValue)> TierAsync(ItemDefinition? item, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return ("—", false);
        }

        var price = await _itemRepository.GetPriceAsync(item.Id, cancellationToken).ConfigureAwait(true);
        if (price is null || price.BestEconomicValue == 0)
        {
            return ("—", false);
        }

        var tier = ValueTierThresholds.Default.GetTier(item.ValuePerSlot(price));
        return (tier, tier is "S" or "A");
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
