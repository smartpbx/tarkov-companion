using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Reads what the Keep list is computed from — requirements, key facts, the active profile, the
/// quest board and the item catalog — and hands it to <see cref="KeepListPlanner"/>.
/// </summary>
public sealed class KeepListService
{
    private readonly IRequirementCatalog _requirements;
    private readonly IPlayerProfileService _profileService;
    private readonly IItemRepository _itemRepository;
    private readonly IItemFactCatalog _factCatalog;
    private readonly IQuestReadService _questReadService;

    public KeepListService(
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
    }

    /// <summary>The computed Keep list, or <see cref="KeepPlan.NoData"/> before anything has synced.</summary>
    public async Task<KeepPlan> BuildAsync(CancellationToken cancellationToken)
    {
        var hideoutRequirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false);
        var keyFacts = await _factCatalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false);
        var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var board = await _questReadService.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);

        // Quest needs come off the board, not the requirement catalog: see QuestItemNeedPlanner
        // for the migration that empties the catalog's table and what the board adds.
        var questNeeds = QuestItemNeedPlanner.FromBoard(board.Tasks);

        // Whether anything has synced is asked of the board's quests, not of the needs left on
        // them: a player who has finished every quest has data and nothing to keep, and reading
        // the open needs told them nothing was cached.
        if (board.Tasks.Count == 0 && hideoutRequirements.Count == 0 && keyFacts.Count == 0)
        {
            return KeepPlan.NoData;
        }

        var stations = await _requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false);

        // The profile and the board each record objective progress, in different tables, and they
        // are the same fact. The larger is taken; adding them would hand in the same item twice.
        var progress = new Dictionary<string, int>(profile.ObjectiveProgress, StringComparer.Ordinal);
        foreach (var (objectiveId, recorded) in questNeeds.RecordedProgress)
        {
            progress[objectiveId] = Math.Max(progress.GetValueOrDefault(objectiveId), recorded);
        }

        var inputs = new KeepListInputs(
            profile with { ObjectiveProgress = progress },
            questNeeds.Requirements,
            hideoutRequirements,
            board.Tasks.ToDictionary(task => task.TaskId, task => task.Name, StringComparer.Ordinal),
            board.Tasks
                .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal),
            stations.ToDictionary(station => station.StationId, station => station.Name, StringComparer.OrdinalIgnoreCase),
            keyFacts)
        {
            InterchangeableCounts = questNeeds.InterchangeableCounts,
            Reusable = questNeeds.Reusable,
            LaterPrestigeTierTaskIds = KeepListPlanner.LaterPrestigeTiers(board.Tasks),
            QuestTotals = QuestItemNeedPlanner.TotalsByItem(board.Tasks),
        };
        return await KeepListPlanner.PlanAsync(inputs, ResolveItemAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The S/A/B/C/D band <c>RecommendationEngine</c> ranks scanned loot by, for one item.</summary>
    private async Task<KeepItemFacts> ResolveItemAsync(string itemId, CancellationToken cancellationToken)
    {
        var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return new KeepItemFacts(itemId, "—");
        }

        var price = await _itemRepository.GetPriceAsync(item.Id, cancellationToken).ConfigureAwait(false);
        return price is null || price.BestEconomicValue == 0
            ? new KeepItemFacts(item.Name, "—")
            : new KeepItemFacts(item.Name, ValueTierThresholds.Default.GetTier(item.ValuePerSlot(price)));
    }
}
