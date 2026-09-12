using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loadouts;

namespace TarkovCompanion.App.ViewModels;

/// <summary>The part of a kit an assigned item is standing in for.</summary>
/// <remarks>
/// This mirrors the shape of <see cref="LoadoutSelection"/> rather than the game's equipment
/// screen. A slot the selection record cannot express would be a slot the evaluation ignores,
/// and offering it would invite the player to describe a kit the page never actually reads.
/// </remarks>
public enum LoadoutSlot
{
    Weapon,
    Ammunition,
    Magazine,
    Armor,
    Plate,
    Helmet,
    Headset,
    Rig,
    Backpack,
    Medical,
}

/// <summary>One entry of the slot chooser.</summary>
public sealed record LoadoutSlotOption(LoadoutSlot Slot, string Name, bool AllowsMany, string Hint);

/// <summary>A search hit, with the facts the evaluation will actually use for it.</summary>
public sealed record LoadoutSearchResultViewModel(
    string ItemId,
    string Name,
    string ShortName,
    string Category,
    string Cost,
    string Weight,
    string Caliber,
    ICommand AssignCommand);

/// <summary>One item currently standing in a slot.</summary>
public sealed record LoadoutAssignmentViewModel(
    string ItemId,
    string SlotName,
    string ItemName,
    string Detail,
    ICommand RemoveCommand);

/// <summary>A single line returned by the evaluation, issue or warning.</summary>
public sealed record LoadoutFindingViewModel(string Message);

/// <summary>
/// Prices, weighs and sanity-checks a kit the player assembles by hand.
/// </summary>
/// <remarks>
/// <para>
/// The honesty constraint is the whole point of this page. Of the compatibility checks
/// <see cref="LoadoutIntelligenceService"/> implements, only two can fire on the synced data:
/// the weapon-to-ammunition caliber match and the per-slot category check. The magazine-to-weapon
/// and plate-to-armor checks read compatibility sets that <c>SqliteItemFactCatalog</c> documents
/// as deliberately empty, because nothing upstream states them; the magazine-to-ammunition
/// caliber check reads a caliber the projection records only for items carrying ammunition or
/// weapon properties, so a magazine never has one. All three abstain. An abstained check produces
/// no issue, which looks exactly like a pass, so the page says in plain words which checks ran.
/// </para>
/// <para>
/// The service is built here rather than injected. The container registers
/// <c>ILoadoutService</c> against an empty <c>IEnumerable&lt;LoadoutItemFacts&gt;</c>, so the
/// injected instance knows no items at all; reading the catalog is the only way this page gets
/// real data, and doing it per action rather than at container build time means the facts appear
/// as soon as the first sync lands instead of at the next restart.
/// </para>
/// </remarks>
public sealed class LoadoutPageViewModel : PageViewModel
{
    /// <summary>What the page can and cannot check, stated before any result is shown.</summary>
    private const string ChecksNote =
        "Two checks actually run: the weapon and the ammunition must share a caliber, and every " +
        "assigned item must belong to the category of the slot it sits in. Three do not run: " +
        "magazine-to-weapon fit and plate-to-armor fit are absent from the synced data, and the " +
        "magazine-to-ammunition caliber match needs a caliber the sync records only for weapons " +
        "and rounds. Those three abstain rather than pass, so an empty issue list is silence " +
        "about them, not approval.";

    /// <summary>Where the money and the kilograms come from, and what they leave out.</summary>
    private const string DataNote =
        "Cost per item is the best local price: the last flea sale, else the 24-hour average, " +
        "else the game's base price. Weight is read from the synced item payload. An item that " +
        "states neither counts as zero, so both totals are floors rather than estimates. No " +
        "player profile is passed to the evaluation, so ammunition is never reported as " +
        "unobtainable here.";

    private static readonly IReadOnlyList<LoadoutSlotOption> SlotOptions =
    [
        new(LoadoutSlot.Weapon, "Weapon", false, "One weapon. Its caliber is what the ammunition is checked against."),
        new(LoadoutSlot.Ammunition, "Ammunition", false, "One round. Drives the ammo tier and the only caliber check that runs."),
        new(LoadoutSlot.Magazine, "Magazines", true, "Counted toward cost and weight; fit is not checked."),
        new(LoadoutSlot.Armor, "Body armor", false, "One armor rig or vest."),
        new(LoadoutSlot.Plate, "Plates", true, "Counted toward cost and weight; fit into the armor is not checked."),
        new(LoadoutSlot.Helmet, "Helmet", false, "One helmet."),
        new(LoadoutSlot.Headset, "Headset", false, "One headset."),
        new(LoadoutSlot.Rig, "Rig", false, "One chest rig."),
        new(LoadoutSlot.Backpack, "Backpack", false, "One backpack."),
        new(LoadoutSlot.Medical, "Medical", true, "Any number of medical items."),
    ];

    private static readonly IReadOnlyDictionary<string, LoadoutItemFacts> NoFacts =
        new Dictionary<string, LoadoutItemFacts>(StringComparer.Ordinal);

    private readonly IItemFactCatalog _catalog;
    private readonly IItemSearchService _searchService;
    private readonly IItemRepository _itemRepository;
    private readonly Dictionary<LoadoutSlot, List<AssignedItem>> _selection = [];

    private IReadOnlyList<LoadoutItemFacts> _factList = [];
    private IReadOnlyDictionary<string, LoadoutItemFacts> _facts = NoFacts;
    private LoadoutIntelligenceService? _loadoutService;
    private ApplicationRuntimeSnapshot? _snapshot;
    private int? _knownItemCount;

    private LoadoutSlotOption _selectedSlot = SlotOptions[0];
    private string _searchQuery = string.Empty;
    /// <summary>What the status line says when there is nothing wrong and nothing searched.</summary>
    private const string ReadyToSearch = "Pick a slot, search an item, and assign it.";

    private bool _showingNoData;
    private string _searchStatus = ReadyToSearch;
    private string _assignmentStatus = "Nothing is assigned yet.";
    private string _evaluationStatus = "Assign at least one item, then evaluate.";
    private string _costSummary = "No cost yet.";
    private string _weightSummary = "No weight yet.";
    private string _ammoTierSummary = "No ammunition assigned.";
    private string _issuesStatus = "Not evaluated yet.";
    private string _warningsStatus = "Not evaluated yet.";
    private IReadOnlyList<LoadoutSearchResultViewModel> _results = [];
    private IReadOnlyList<LoadoutAssignmentViewModel> _assignments = [];
    private IReadOnlyList<LoadoutFindingViewModel> _issues = [];
    private IReadOnlyList<LoadoutFindingViewModel> _warnings = [];

    public LoadoutPageViewModel(
        IItemFactCatalog catalog,
        IItemSearchService searchService,
        IItemRepository itemRepository)
        : base("Loadout", "Price, weigh and sanity-check a kit you assemble by hand", "Runtime state not loaded")
    {
        _catalog = catalog;
        _searchService = searchService;
        _itemRepository = itemRepository;
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
        EvaluateCommand = new AsyncDelegateCommand(EvaluateAsync);
        ClearCommand = new DelegateCommand(Clear);
    }

    public AsyncDelegateCommand SearchCommand { get; }

    public AsyncDelegateCommand EvaluateCommand { get; }

    public DelegateCommand ClearCommand { get; }

    public IReadOnlyList<LoadoutSlotOption> Slots => SlotOptions;

    public string ChecksExplanation => ChecksNote;

    public string DataExplanation => DataNote;

    /// <summary>The slot the next assignment goes into.</summary>
    /// <remarks>
    /// A combo box can write null when its items are replaced, and every other member here
    /// assumes a slot is chosen, so a null write keeps the previous choice instead of leaving
    /// the page without one.
    /// </remarks>
    public LoadoutSlotOption SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedSlot, value);
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public string AssignmentStatus
    {
        get => _assignmentStatus;
        private set => SetProperty(ref _assignmentStatus, value);
    }

    public string EvaluationStatus
    {
        get => _evaluationStatus;
        private set => SetProperty(ref _evaluationStatus, value);
    }

    public string CostSummary
    {
        get => _costSummary;
        private set => SetProperty(ref _costSummary, value);
    }

    public string WeightSummary
    {
        get => _weightSummary;
        private set => SetProperty(ref _weightSummary, value);
    }

    public string AmmoTierSummary
    {
        get => _ammoTierSummary;
        private set => SetProperty(ref _ammoTierSummary, value);
    }

    public string IssuesStatus
    {
        get => _issuesStatus;
        private set => SetProperty(ref _issuesStatus, value);
    }

    public string WarningsStatus
    {
        get => _warningsStatus;
        private set => SetProperty(ref _warningsStatus, value);
    }

    public IReadOnlyList<LoadoutSearchResultViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public IReadOnlyList<LoadoutAssignmentViewModel> Assignments
    {
        get => _assignments;
        private set => SetProperty(ref _assignments, value);
    }

    public IReadOnlyList<LoadoutFindingViewModel> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }

    public IReadOnlyList<LoadoutFindingViewModel> Warnings
    {
        get => _warnings;
        private set => SetProperty(ref _warnings, value);
    }

    /// <summary>Takes the runtime snapshot and drops any projection the last sync invalidated.</summary>
    /// <remarks>
    /// <c>IItemFactCatalog.Invalidate</c> runs at the end of a sync, so a service built from the
    /// previous projection would keep answering from rows that no longer exist. The cached item
    /// count is the only change signal this page is given, so it is what triggers the rebuild.
    /// </remarks>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
        if (_knownItemCount == snapshot.Data.ItemCount)
        {
            return;
        }

        _knownItemCount = snapshot.Data.ItemCount;
        _factList = [];
        _facts = NoFacts;
        _loadoutService = null;
        if (snapshot.Data.ItemCount == 0)
        {
            Results = [];
            _showingNoData = true;
            SearchStatus = snapshot.Data.Detail;
        }
        else if (_showingNoData)
        {
            // Said before the item cache had loaded and never taken back, so the page insisted
            // no data was available while the header counted five thousand cached items.
            _showingNoData = false;
            SearchStatus = ReadyToSearch;
        }
    }

    public Task SearchAsync() => SearchAsync(CancellationToken.None);

    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.Data.ItemCount is null or 0)
        {
            Results = [];
            SearchStatus = _snapshot?.Data.Detail ?? "Runtime state is not loaded.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Results = [];
            SearchStatus = "Enter an item name or short name.";
            return;
        }

        try
        {
            SearchStatus = "Searching the local item cache…";
            var facts = await EnsureFactsAsync(cancellationToken).ConfigureAwait(true);
            var hits = await _searchService.SearchAsync(SearchQuery, 20, cancellationToken).ConfigureAwait(true);
            Results = hits.Select(hit => Describe(hit.Item, facts)).ToArray();
            SearchStatus = Results.Count == 0
                ? "No local item matched that query."
                : $"{Results.Count} result(s) from the local cache. Assign takes the slot chosen above.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Results = [];
            SearchStatus = $"Item search failed: {exception.Message}";
        }
    }

    public Task EvaluateAsync() => EvaluateAsync(CancellationToken.None);

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var selectedIds = SelectedItemIds();
        if (selectedIds.Count == 0)
        {
            ResetEvaluation("Assign at least one item, then evaluate.");
            return;
        }

        try
        {
            EvaluationStatus = "Evaluating the assigned kit…";
            var service = await EnsureServiceAsync(cancellationToken).ConfigureAwait(true);
            var evaluation = await service
                .EvaluateAsync(BuildSelection(), profile: null, cancellationToken)
                .ConfigureAwait(true);

            Issues = evaluation.CompatibilityIssues.Select(message => new LoadoutFindingViewModel(message)).ToArray();
            Warnings = evaluation.Warnings.Select(message => new LoadoutFindingViewModel(message)).ToArray();
            CostSummary = DescribeCost(evaluation, selectedIds);
            WeightSummary = DescribeWeight(evaluation, selectedIds);
            AmmoTierSummary = DescribeAmmoTier(evaluation);

            // IsCompatible is "no issue was raised", and three of the five checks cannot raise
            // one, so it must never be rendered as a verdict of compatible.
            IssuesStatus = Issues.Count == 0
                ? "No issue was raised by the two checks that can run. The other three abstained."
                : $"{Issues.Count} issue(s) raised by the checks that can run.";
            WarningsStatus = Warnings.Count == 0
                ? "No warning. Warnings are judgement calls, not compatibility failures."
                : $"{Warnings.Count} warning(s). These are judgement calls, not compatibility failures.";
            EvaluationStatus = $"Evaluated {selectedIds.Count} assigned item(s) against the local fact tables.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ResetEvaluation($"The kit could not be evaluated: {exception.Message}");
        }
    }

    /// <summary>Empties every slot and the last evaluation with it.</summary>
    public void Clear()
    {
        _selection.Clear();
        RefreshAssignments();
        ResetEvaluation("Assign at least one item, then evaluate.");
    }

    private async Task AssignAsync(string itemId, CancellationToken cancellationToken)
    {
        var slot = SelectedSlot;
        try
        {
            // The repository is the authority on what an item is called; the fact table is the
            // authority on what it costs and weighs. Reading the name here keeps an assignment
            // readable even when the fact projection is stale or missing the item entirely.
            var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
            var facts = _facts.GetValueOrDefault(itemId);
            var name = item?.Name ?? facts?.Name ?? itemId;
            var category = item?.Category ?? facts?.Category ?? ItemCategory.Unknown;

            if (!_selection.TryGetValue(slot.Slot, out var items))
            {
                items = [];
                _selection[slot.Slot] = items;
            }

            if (!slot.AllowsMany)
            {
                items.Clear();
            }

            items.Add(new(itemId, name, $"{category} · {DescribeCost(facts)} · {DescribeWeight(facts)}"));
            RefreshAssignments();
            AssignmentStatus = slot.AllowsMany
                ? $"Added {name} to {slot.Name}."
                : $"{slot.Name} is now {name}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AssignmentStatus = $"That item could not be assigned: {exception.Message}";
        }
    }

    private void Remove(LoadoutSlot slot, int index)
    {
        if (!_selection.TryGetValue(slot, out var items) || index < 0 || index >= items.Count)
        {
            return;
        }

        var removed = items[index];
        items.RemoveAt(index);
        if (items.Count == 0)
        {
            _selection.Remove(slot);
        }

        RefreshAssignments();
        AssignmentStatus = $"Removed {removed.Name}.";
    }

    private void RefreshAssignments()
    {
        var rows = new List<LoadoutAssignmentViewModel>();
        foreach (var option in SlotOptions)
        {
            if (!_selection.TryGetValue(option.Slot, out var items))
            {
                continue;
            }

            for (var index = 0; index < items.Count; index++)
            {
                // The whole list is rebuilt on every change, so each row's command can close over
                // its position: no row ever outlives the arrangement its index refers to.
                var position = index;
                var slot = option.Slot;
                rows.Add(new(
                    items[index].ItemId,
                    option.Name,
                    items[index].Name,
                    items[index].Detail,
                    new DelegateCommand(() => Remove(slot, position))));
            }
        }

        Assignments = rows;
        if (rows.Count == 0)
        {
            AssignmentStatus = "Nothing is assigned yet.";
        }
    }

    private LoadoutSelection BuildSelection() => new(
        FirstId(LoadoutSlot.Weapon),
        FirstId(LoadoutSlot.Ammunition),
        AllIds(LoadoutSlot.Magazine),
        FirstId(LoadoutSlot.Armor),
        AllIds(LoadoutSlot.Plate),
        FirstId(LoadoutSlot.Helmet),
        FirstId(LoadoutSlot.Headset),
        FirstId(LoadoutSlot.Rig),
        FirstId(LoadoutSlot.Backpack),
        AllIds(LoadoutSlot.Medical));

    private string? FirstId(LoadoutSlot slot)
    {
        if (_selection.TryGetValue(slot, out var items) && items.Count > 0)
        {
            return items[0].ItemId;
        }

        return null;
    }

    private IReadOnlyList<string> AllIds(LoadoutSlot slot)
    {
        if (_selection.TryGetValue(slot, out var items))
        {
            return items.Select(item => item.ItemId).ToArray();
        }

        return [];
    }

    private List<string> SelectedItemIds()
    {
        var ids = new List<string>();
        foreach (var option in SlotOptions)
        {
            if (!_selection.TryGetValue(option.Slot, out var items))
            {
                continue;
            }

            // A single-value slot only ever contributes its first entry, exactly as
            // BuildSelection reads it, so the per-item accounting below cannot count an item the
            // evaluation never saw.
            ids.AddRange(option.AllowsMany
                ? items.Select(item => item.ItemId)
                : items.Take(1).Select(item => item.ItemId));
        }

        return ids;
    }

    private async Task<IReadOnlyDictionary<string, LoadoutItemFacts>> EnsureFactsAsync(
        CancellationToken cancellationToken)
    {
        // An empty projection is re-read rather than cached: on a clean install the database is
        // still empty when the page first opens and the first sync lands seconds later.
        if (_facts.Count > 0)
        {
            return _facts;
        }

        var facts = await _catalog.GetLoadoutFactsAsync(cancellationToken).ConfigureAwait(true);
        _factList = facts;
        _facts = facts.ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);
        return _facts;
    }

    private async Task<LoadoutIntelligenceService> EnsureServiceAsync(CancellationToken cancellationToken)
    {
        if (_loadoutService is { } existing)
        {
            return existing;
        }

        await EnsureFactsAsync(cancellationToken).ConfigureAwait(true);
        var ammo = await _catalog.GetAmmoAsync(cancellationToken).ConfigureAwait(true);
        var packs = await _catalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(true);

        // No availability rules are passed because nothing in the synced data states a round's
        // level, trader or task gate. AmmoIntelligenceService reads a missing rule as obtainable,
        // so the "not obtainable for the active profile" warning cannot fire from this page.
        var service = new LoadoutIntelligenceService(_factList, new AmmoIntelligenceService(ammo, packs));
        _loadoutService = service;
        return service;
    }

    private LoadoutSearchResultViewModel Describe(
        ItemDefinition item,
        IReadOnlyDictionary<string, LoadoutItemFacts> facts)
    {
        var fact = facts.GetValueOrDefault(item.Id);
        return new(
            item.Id,
            item.Name,
            item.ShortName,
            item.Category.ToString(),
            DescribeCost(fact),
            DescribeWeight(fact),
            fact?.Caliber is { } caliber ? caliber : "No caliber recorded",
            new AsyncDelegateCommand(() => AssignAsync(item.Id, CancellationToken.None)));
    }

    private string DescribeCost(LoadoutEvaluation evaluation, IReadOnlyCollection<string> selectedIds)
    {
        var unpriced = selectedIds.Count(id => _facts.GetValueOrDefault(id) is null or { ApproximateCostRoubles: <= 0 });
        var total = Roubles(evaluation.ApproximateCostRoubles);
        return unpriced == 0
            ? $"{total} · every assigned item has a recorded price."
            : $"{total} · {unpriced} of {selectedIds.Count} assigned item(s) have no recorded price and counted as zero.";
    }

    private string DescribeWeight(LoadoutEvaluation evaluation, IReadOnlyCollection<string> selectedIds)
    {
        if (evaluation.ApproximateWeightKg is not { } weight)
        {
            // The service returns no weight at all when an assigned id is absent from the fact
            // table, because a partial sum would read as a complete one.
            return "No total: at least one assigned item is missing from the loadout fact table.";
        }

        var unweighed = selectedIds.Count(id => _facts.GetValueOrDefault(id) is null or { WeightKg: <= 0 });
        return unweighed == 0
            ? $"{Kilograms(weight)} · every assigned item has a recorded weight."
            : $"{Kilograms(weight)} · {unweighed} of {selectedIds.Count} assigned item(s) state no weight upstream and counted as zero, so the kit is heavier than this.";
    }

    private static string DescribeAmmoTier(LoadoutEvaluation evaluation) =>
        string.Equals(evaluation.AmmoTier, "Unknown", StringComparison.Ordinal)
            ? "Unknown: no ammunition is assigned, or the assigned round has no ballistic row in the local cache."
            : $"Tier {evaluation.AmmoTier} within its own caliber, ranked by penetration then damage.";

    private static string DescribeCost(LoadoutItemFacts? facts) =>
        facts is null ? "no facts" : facts.ApproximateCostRoubles > 0 ? Roubles(facts.ApproximateCostRoubles) : "no price";

    private static string DescribeWeight(LoadoutItemFacts? facts) =>
        facts is null ? "no facts" : facts.WeightKg > 0 ? Kilograms(facts.WeightKg) : "no weight";

    private void ResetEvaluation(string status)
    {
        Issues = [];
        Warnings = [];
        CostSummary = "No cost yet.";
        WeightSummary = "No weight yet.";
        AmmoTierSummary = "No ammunition assigned.";
        IssuesStatus = "Not evaluated yet.";
        WarningsStatus = "Not evaluated yet.";
        EvaluationStatus = status;
    }

    private static string Roubles(long value) => value.ToString("N0", CultureInfo.CurrentCulture) + " ₽";

    private static string Kilograms(double value) => value.ToString("N2", CultureInfo.CurrentCulture) + " kg";

    /// <summary>What the page remembers about one assigned item.</summary>
    private sealed record AssignedItem(string ItemId, string Name, string Detail);
}
