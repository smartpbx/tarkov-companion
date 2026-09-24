using TarkovCompanion.App.Services;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Planning;
using System.Globalization;
using TarkovCompanion.Core.Common;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Intelligence.Gear;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Loadouts;
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
/// <param name="AllowsMany">
/// Whether its slot holds more than one. The board tile shows what is in a slot and clears the
/// whole slot; only a slot that holds several needs a row per item to remove one of them, so the
/// itemised list below the board is filtered to those and the board carries the rest.
/// </param>
public sealed record LoadoutAssignmentViewModel(
    string ItemId,
    string SlotName,
    string ItemName,
    string Detail,
    ICommand RemoveCommand,
    bool AllowsMany = false)
{
    /// <summary>"Allergic · event name" where the Events page records an allergy to this food or medicine (#285).</summary>
    public string AllergyWarning { get; init; } = string.Empty;

    public bool HasAllergyWarning => AllergyWarning.Length > 0;
}

/// <summary>A single line returned by the evaluation, issue or warning.</summary>
/// <summary>An issue or warning, with the plain reason the rule raised it (empty when there is none).</summary>
public sealed record LoadoutFindingViewModel(string Message, string Explanation = "");

/// <summary>
/// One tile of the kit board: a slot, whatever is standing in it, and a way to aim at it.
/// </summary>
/// <remarks>
/// [V2 rough package 60 — Plan] #288 asked for visual loadout slots. The page had a combo box and
/// a flat list of whatever happened to be assigned, so the two questions a kit is actually looked
/// at to answer — what is in it, and what is still missing — both needed counting. Ten tiles
/// answer both at a glance, and an empty one says so rather than being absent.
/// </remarks>
public sealed record LoadoutSlotTileViewModel(
    LoadoutSlot Slot,
    string Name,
    string Hint,
    bool IsFilled,
    string Summary,
    string Detail,
    bool IsSelected,
    string AutomationId,
    ICommand SelectCommand,
    ICommand ClearSlotCommand);

/// <summary>One saved kit, as it reads in the preset list.</summary>
public sealed record LoadoutPresetViewModel(
    string Name,
    string Detail,
    bool IsComparing,
    ICommand LoadCommand,
    ICommand CompareCommand,
    ICommand DeleteCommand)
{
    /// <summary>What the compare button will do, said on the button rather than by its state.</summary>
    public string CompareLabel => IsComparing ? PlanText.LoadoutComparing : PlanText.LoadoutCompare;
}

/// <summary>One row of the side-by-side comparison: a measure, both values, and the difference.</summary>
public sealed record LoadoutComparisonRowViewModel(string Measure, string Current, string Other, string Difference);

/// <summary>One same-slot replacement and the trader source the active profile can or cannot use.</summary>
public sealed record LoadoutAlternativeViewModel(
    string Name,
    string Detail,
    string Source,
    string Requirement,
    bool IsObtainable,
    ICommand ChooseCommand)
{
    public double Opacity => IsObtainable ? 1 : 0.52;
}

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
    /// <summary>
    /// What is actually checked, and what the totals are.
    /// </summary>
    /// <remarks>
    /// This ran to eleven lines across two paragraphs, naming the three checks that abstain
    /// and the order the price falls back through. Both totals still count a missing figure as
    /// zero, so both are floors; that is the part a player acts on and all that is left here.
    /// </remarks>
    private static string ChecksNote => PlanText.LoadoutChecksNote;

    private static string DataNote => PlanText.LoadoutDataNote;

    /// <summary>What the budget line says before anybody has typed one.</summary>
    private static string NoBudget => PlanText.LoadoutNoBudget;

    private static readonly IReadOnlyList<LoadoutSlotOption> SlotOptions =
    [
        new(LoadoutSlot.Weapon, PlanText.LoadoutSlotWeapon, false, PlanText.LoadoutHintWeapon),
        new(LoadoutSlot.Ammunition, PlanText.LoadoutSlotAmmunition, false, PlanText.LoadoutHintAmmunition),
        new(LoadoutSlot.Magazine, PlanText.LoadoutSlotMagazines, true, PlanText.LoadoutHintMagazine),
        new(LoadoutSlot.Armor, PlanText.LoadoutSlotBodyArmor, false, PlanText.LoadoutHintArmor),
        new(LoadoutSlot.Plate, PlanText.LoadoutSlotPlates, true, PlanText.LoadoutHintPlate),
        new(LoadoutSlot.Helmet, PlanText.LoadoutSlotHelmet, false, PlanText.LoadoutHintHelmet),
        new(LoadoutSlot.Headset, PlanText.LoadoutSlotHeadset, false, PlanText.LoadoutHintHeadset),
        new(LoadoutSlot.Rig, PlanText.LoadoutSlotRig, false, PlanText.LoadoutHintRig),
        new(LoadoutSlot.Backpack, PlanText.LoadoutSlotBackpack, false, PlanText.LoadoutHintBackpack),
        new(LoadoutSlot.Medical, PlanText.LoadoutSlotMedical, true, PlanText.LoadoutHintMedical),
    ];

    private static readonly IReadOnlyDictionary<string, LoadoutItemFacts> NoFacts =
        new Dictionary<string, LoadoutItemFacts>(StringComparer.Ordinal);

    private readonly IItemFactCatalog _catalog;
    private readonly IItemSearchService _searchService;
    private readonly IItemRepository _itemRepository;
    // [V2 rough package 60 — Plan] #288. Optional: the V1 page predates saved kits and every test
    // that builds this view model builds it without one. A page with no store offers no presets
    // rather than offering a Save button that silently does nothing.
    private readonly ILoadoutPresetStore? _presets;
    private readonly TimeProvider _clock;
    private readonly Dictionary<LoadoutSlot, List<AssignedItem>> _selection = [];
    private readonly AllergyWarningService? _allergies;
    private readonly IItemAcquisitionService? _acquisitions;
    private IReadOnlyDictionary<string, string> _allergyWarnings = new Dictionary<string, string>(StringComparer.Ordinal);

    private IReadOnlyList<LoadoutItemFacts> _factList = [];
    private IReadOnlyDictionary<string, LoadoutItemFacts> _facts = NoFacts;
    private LoadoutIntelligenceService? _loadoutService;
    private ApplicationRuntimeSnapshot? _snapshot;
    private int? _knownItemCount;

    private LoadoutSlotOption _selectedSlot = SlotOptions[0];
    private string _searchQuery = string.Empty;
    /// <summary>What the status line says when there is nothing wrong and nothing searched.</summary>
    private static string ReadyToSearch => PlanText.LoadoutReadyToSearch;

    private bool _showingNoData;
    private string _searchStatus = ReadyToSearch;
    private string _assignmentStatus = PlanText.LoadoutNothingAssigned;
    private string _evaluationStatus = PlanText.LoadoutAssignThenEvaluate;
    private string _costSummary = PlanText.LoadoutNoCostYet;
    private string _weightSummary = PlanText.LoadoutNoWeightYet;
    private string _ammoTierSummary = PlanText.LoadoutNoAmmunitionAssigned;
    private string _issuesStatus = PlanText.LoadoutNotEvaluatedYet;
    private string _warningsStatus = PlanText.LoadoutNotEvaluatedYet;
    private IReadOnlyList<LoadoutSearchResultViewModel> _results = [];
    private IReadOnlyList<LoadoutAssignmentViewModel> _assignments = [];
    private IReadOnlyList<LoadoutFindingViewModel> _issues = [];
    private IReadOnlyList<LoadoutFindingViewModel> _warnings = [];
    private IReadOnlyList<LoadoutAssignmentViewModel> _multiAssignments = [];
    private IReadOnlyList<LoadoutSlotTileViewModel> _slotBoard = [];
    private IReadOnlyList<LoadoutPresetViewModel> _presetRows = [];
    private IReadOnlyList<LoadoutComparisonRowViewModel> _comparison = [];
    private IReadOnlyList<LoadoutAlternativeViewModel> _alternatives = [];
    private string _alternativesStatus = PlanText.LoadoutAssignForAlternatives;
    private string _presetName = string.Empty;
    private string _presetStatus = PlanText.LoadoutNoKitSaved;
    private string _budgetInput = string.Empty;
    private string _budgetSummary = NoBudget;
    private bool _isOverBudget;
    private string? _comparingPreset;
    private long? _evaluatedCost;
    private double? _evaluatedWeight;
    private string _evaluatedAmmoTier = string.Empty;

    public LoadoutPageViewModel(
        IItemFactCatalog catalog,
        IItemSearchService searchService,
        IItemRepository itemRepository,
        ILoadoutPresetStore? presets = null,
        TimeProvider? clock = null,
        AllergyWarningService? allergies = null,
        IItemAcquisitionService? acquisitions = null,
        // #283: how many of the assigned round the player owns, from stash and Ammo case scans.
        IPlayerProfileService? profiles = null)
        : base("Loadout", "Price and weigh a kit you assemble by hand", "Runtime state not loaded")
    {
        _catalog = catalog;
        _searchService = searchService;
        _itemRepository = itemRepository;
        _presets = presets;
        _allergies = allergies;
        _acquisitions = acquisitions;
        _profiles = profiles;
        _clock = clock ?? TimeProvider.System;
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
        EvaluateCommand = new AsyncDelegateCommand(EvaluateAsync);
        ClearCommand = new DelegateCommand(Clear);
        SavePresetCommand = new AsyncDelegateCommand(() => SavePresetAsync(CancellationToken.None));
        RefreshPresetsCommand = new AsyncDelegateCommand(() => LoadPresetsAsync(CancellationToken.None));
        ClearComparisonCommand = new DelegateCommand(() => Compare(null));
        RefreshSlotBoard();
    }

    /// <summary>
    /// #307: what the chosen map's active quests ask the kit for. Null where no quest board is
    /// composed, and the card is then not drawn.
    /// </summary>
    public TarkovCompanion.App.ViewModels.V2.Plan.LoadoutSuggestionsViewModel? Suggestions { get; init; }

    public AsyncDelegateCommand SearchCommand { get; }

    public AsyncDelegateCommand EvaluateCommand { get; }

    public DelegateCommand ClearCommand { get; }

    public AsyncDelegateCommand SavePresetCommand { get; }

    public AsyncDelegateCommand RefreshPresetsCommand { get; }

    public DelegateCommand ClearComparisonCommand { get; }

    /// <summary>Whether saved kits are offered at all; false with no store composed.</summary>
    public bool CanSavePresets => _presets is not null;

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
            if (value is not null && SetProperty(ref _selectedSlot, value))
            {
                RefreshSlotBoard();
                _ = RefreshAlternativesAsync(CancellationToken.None);
                if (Results.Count > 0 && !string.IsNullOrWhiteSpace(SearchQuery))
                {
                    // The list on screen was filtered for the slot just left.
                    _ = SearchAsync(CancellationToken.None);
                }
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

    /// <summary>The assignments in slots that hold several, which need a row each to remove one.</summary>
    public IReadOnlyList<LoadoutAssignmentViewModel> MultiAssignments
    {
        get => _multiAssignments;
        private set => SetProperty(ref _multiAssignments, value);
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

    /// <summary>The ten slots, filled or not. Always ten rows; an empty slot says so.</summary>
    public IReadOnlyList<LoadoutSlotTileViewModel> SlotBoard
    {
        get => _slotBoard;
        private set => SetProperty(ref _slotBoard, value);
    }

    /// <summary>How many of the ten have something in them, for the board's heading.</summary>
    public string SlotBoardSummary =>
        PlanText.LoadoutSlotsFilled(SlotBoard.Count(tile => tile.IsFilled), SlotOptions.Count);

    public IReadOnlyList<LoadoutPresetViewModel> Presets
    {
        get => _presetRows;
        private set => SetProperty(ref _presetRows, value);
    }

    /// <summary>The comparison table, empty until a saved kit is chosen to compare against.</summary>
    public IReadOnlyList<LoadoutComparisonRowViewModel> Comparison
    {
        get => _comparison;
        private set
        {
            if (SetProperty(ref _comparison, value))
            {
                OnPropertyChanged(nameof(HasComparison));
            }
        }
    }

    public bool HasComparison => Comparison.Count > 0;

    public IReadOnlyList<LoadoutAlternativeViewModel> Alternatives
    {
        get => _alternatives;
        private set
        {
            if (SetProperty(ref _alternatives, value))
            {
                OnPropertyChanged(nameof(HasAlternatives));
            }
        }
    }

    public bool HasAlternatives => Alternatives.Count > 0;

    public string AlternativesStatus
    {
        get => _alternativesStatus;
        private set => SetProperty(ref _alternativesStatus, value);
    }

    /// <summary>The name a Save press will use.</summary>
    public string PresetName
    {
        get => _presetName;
        set => SetProperty(ref _presetName, value);
    }

    public string PresetStatus
    {
        get => _presetStatus;
        private set => SetProperty(ref _presetStatus, value);
    }

    /// <summary>What the player is willing to spend, as they typed it.</summary>
    /// <remarks>
    /// Free text rather than a numeric control: a kit budget is typed as "250000" or "250,000"
    /// or "250k" depending on the person, and refusing two of those is a worse answer than
    /// reading all three.
    /// </remarks>
    public string BudgetInput
    {
        get => _budgetInput;
        set
        {
            if (SetProperty(ref _budgetInput, value))
            {
                RefreshBudget();
            }
        }
    }

    public string BudgetSummary
    {
        get => _budgetSummary;
        private set => SetProperty(ref _budgetSummary, value);
    }

    /// <summary>Whether the last evaluated kit costs more than the budget.</summary>
    /// <remarks>
    /// Paired with the word "over" in <see cref="BudgetSummary"/>, never colour alone (#266).
    /// </remarks>
    public bool IsOverBudget
    {
        get => _isOverBudget;
        private set => SetProperty(ref _isOverBudget, value);
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
        Alternatives = [];
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
            SearchStatus = _snapshot?.Data.Detail ?? PlanText.LoadoutRuntimeNotLoaded;
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Results = [];
            SearchStatus = PlanText.LoadoutEnterItemName;
            return;
        }

        try
        {
            SearchStatus = PlanText.LoadoutSearching;
            var facts = await EnsureFactsAsync(cancellationToken).ConfigureAwait(true);
            // Only what the chosen slot takes, what the catalog is sure about first. More is read
            // than is shown because "bp" is mostly rounds and the slot may be Weapon.
            var slot = SelectedSlot;
            var hits = await _searchService.SearchAsync(SearchQuery, 60, cancellationToken).ConfigureAwait(true);
            var fitting = hits
                .Where(hit => LoadoutSlotRules.Accepts(slot.Slot, KindOf(hit.Item.Id, hit.Item.Category, facts)))
                .OrderByDescending(hit => LoadoutSlotRules.Fits(slot.Slot, KindOf(hit.Item.Id, hit.Item.Category, facts)))
                .Take(20)
                .ToArray();
            Results = fitting.Select(hit => Describe(hit.Item, facts)).ToArray();
            SearchStatus = (Results.Count, hits.Count) switch
            {
                (0, 0) => PlanText.LoadoutNoLocalMatch,
                (0, _) => PlanText.LoadoutNothingForSlot(slot.Name),
                _ => PlanText.LoadoutResultsForSlot(Results.Count, slot.Name),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Results = [];
            SearchStatus = PlanText.LoadoutSearchFailed(exception.Message);
        }
    }

    public Task EvaluateAsync() => EvaluateAsync(CancellationToken.None);

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var selectedIds = SelectedItemIds();
        if (selectedIds.Count == 0)
        {
            ResetEvaluation(PlanText.LoadoutAssignThenEvaluate);
            return;
        }

        try
        {
            EvaluationStatus = PlanText.LoadoutEvaluating;
            var service = await EnsureServiceAsync(cancellationToken).ConfigureAwait(true);
            var evaluation = await service
                .EvaluateAsync(BuildSelection(), profile: null, cancellationToken)
                .ConfigureAwait(true);

            Issues = evaluation.CompatibilityIssues.Select(message => Finding(evaluation, message)).ToArray();
            Warnings = evaluation.Warnings.Select(message => Finding(evaluation, message)).ToArray();
            CostSummary = DescribeCost(evaluation);
            WeightSummary = DescribeWeight(evaluation);
            AmmoTierSummary = DescribeAmmoTier(evaluation) + await DescribeOwnedRoundsAsync(cancellationToken).ConfigureAwait(true);
            _evaluatedCost = evaluation.ApproximateCostRoubles;
            _evaluatedWeight = evaluation.ApproximateWeightKg;
            _evaluatedAmmoTier = evaluation.AmmoTier;
            RefreshBudget();
            await RefreshComparisonAsync(cancellationToken).ConfigureAwait(true);

            // IsCompatible is "no issue was raised", and three of the five checks cannot raise
            // one, so it must never be rendered as a verdict of compatible.
            IssuesStatus = Issues.Count == 0
                ? PlanText.LoadoutNoIssues
                : PlanText.LoadoutIssueCount(Issues.Count);
            WarningsStatus = Warnings.Count == 0
                ? PlanText.LoadoutNoWarnings
                : PlanText.LoadoutWarningCount(Warnings.Count);
            EvaluationStatus = PlanText.LoadoutItemsEvaluated(selectedIds.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ResetEvaluation(PlanText.LoadoutUnreadable(exception.Message));
        }
    }

    /// <summary>Empties every slot and the last evaluation with it.</summary>
    public void Clear()
    {
        _selection.Clear();
        RefreshAssignments();
        ResetEvaluation(PlanText.LoadoutAssignThenEvaluate);
    }

    /// <summary>Replaces the board with catalog items identified in a screenshot.</summary>
    /// <remarks>
    /// Recognition supplies identities, not slot guesses. The catalog category chooses the slot;
    /// ambiguous armor kinds use their own named category, and generic weapon attachments are not
    /// called magazines unless their catalog name says magazine.
    /// </remarks>
    public async Task<int> LoadRecognizedItemsAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var facts = await EnsureFactsAsync(cancellationToken).ConfigureAwait(true);
        _selection.Clear();

        foreach (var itemId in itemIds)
        {
            var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
            var fact = facts.GetValueOrDefault(itemId);
            var name = item?.Name ?? fact?.Name ?? itemId;
            var category = KindOf(itemId, item?.Category ?? ItemCategory.Unknown, facts);
            if (SlotForRecognized(category, name) is not { } slot)
            {
                continue;
            }

            var option = SlotOptions.First(candidate => candidate.Slot == slot);
            if (!_selection.TryGetValue(slot, out var assigned))
            {
                _selection[slot] = assigned = [];
            }

            if (!option.AllowsMany)
            {
                assigned.Clear();
            }

            assigned.Add(new(itemId, name, $"{category} · {DescribeCost(fact)} · {DescribeWeight(fact)}{DescribeGear(fact)}"));
        }

        await RefreshAllergyWarningsAsync(cancellationToken).ConfigureAwait(true);
        RefreshAssignments();
        ResetEvaluation(Assignments.Count == 0
            ? PlanText.LoadoutNoRecognizedSlot
            : PlanText.LoadoutRecognizedLoaded);
        AssignmentStatus = Assignments.Count == 0
            ? PlanText.LoadoutNoRecognizedAssigned
            : PlanText.LoadoutRecognizedCount(Assignments.Count);
        await RefreshAlternativesAsync(cancellationToken).ConfigureAwait(true);
        return Assignments.Count;
    }

    internal static LoadoutSlot? SlotForRecognized(ItemCategory category, string name) => category switch
    {
        ItemCategory.Weapon => LoadoutSlot.Weapon,
        ItemCategory.Ammunition or ItemCategory.AmmunitionPack => LoadoutSlot.Ammunition,
        ItemCategory.Attachment when name.Contains("magazine", StringComparison.OrdinalIgnoreCase) => LoadoutSlot.Magazine,
        ItemCategory.Armor => LoadoutSlot.Armor,
        ItemCategory.Plate => LoadoutSlot.Plate,
        ItemCategory.Helmet => LoadoutSlot.Helmet,
        ItemCategory.Headset => LoadoutSlot.Headset,
        ItemCategory.Rig => LoadoutSlot.Rig,
        ItemCategory.Backpack => LoadoutSlot.Backpack,
        ItemCategory.Medicine or ItemCategory.Provision => LoadoutSlot.Medical,
        _ => null,
    };

    internal static bool RecognizedSlotAllowsMany(LoadoutSlot slot) =>
        SlotOptions.First(option => option.Slot == slot).AllowsMany;

    /// <summary>Reads the saved kits, so the page can offer them.</summary>
    public async Task LoadPresetsAsync(CancellationToken cancellationToken)
    {
        if (_presets is null)
        {
            return;
        }

        try
        {
            var saved = await _presets.GetAsync(cancellationToken).ConfigureAwait(true);
            Presets = [.. saved.Select(Describe)];
            PresetStatus = Presets.Count == 0
                ? PlanText.LoadoutNoKitSaved
                : PlanText.LoadoutPresetCount(Presets.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Presets = [];
            PresetStatus = PlanText.LoadoutPresetsUnreadable(exception.Message);
        }
    }

    /// <summary>Saves the board as it stands, under the typed name.</summary>
    public async Task SavePresetAsync(CancellationToken cancellationToken)
    {
        if (_presets is null)
        {
            return;
        }

        if (!LoadoutPreset.IsUsableName(PresetName))
        {
            PresetStatus = PlanText.LoadoutNameKitFirst;
            return;
        }

        var items = SlotOptions
            .Where(option => _selection.ContainsKey(option.Slot))
            .SelectMany(option => _selection[option.Slot]
                .Select(item => new LoadoutPresetItem(option.Slot.ToString(), item.ItemId, item.Name)))
            .ToArray();
        if (items.Length == 0)
        {
            PresetStatus = PlanText.LoadoutAssignBeforeSaving;
            return;
        }

        var name = PresetName.Trim();
        try
        {
            await _presets
                .SaveAsync(new LoadoutPreset(name, _clock.GetUtcNow(), items), cancellationToken)
                .ConfigureAwait(true);
            PresetName = string.Empty;
            await LoadPresetsAsync(cancellationToken).ConfigureAwait(true);
            PresetStatus = PlanText.LoadoutSavedPreset(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PresetStatus = PlanText.LoadoutNotSaved(exception.Message);
        }
    }

    /// <summary>Replaces the board with a saved kit.</summary>
    /// <remarks>
    /// Replaces rather than merges. Loading a kit on top of another would produce a third kit
    /// nobody chose, and the two-item slots make it silently additive.
    /// </remarks>
    public async Task LoadPresetAsync(string name, CancellationToken cancellationToken)
    {
        if (_presets is null)
        {
            return;
        }

        var saved = (await _presets.GetAsync(cancellationToken).ConfigureAwait(true))
            .FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            PresetStatus = PlanText.LoadoutNoLongerSaved(name);
            await LoadPresetsAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        _selection.Clear();
        foreach (var item in saved.Items)
        {
            if (!Enum.TryParse<LoadoutSlot>(item.Slot, out var slot))
            {
                continue;
            }

            if (!_selection.TryGetValue(slot, out var items))
            {
                items = [];
                _selection[slot] = items;
            }

            items.Add(new(item.ItemId, item.Name, SlotOptions.First(option => option.Slot == slot).Name));
        }

        await RefreshAllergyWarningsAsync(cancellationToken).ConfigureAwait(true);
        RefreshAssignments();
        ResetEvaluation(PlanText.LoadoutLoadedEvaluate);
        PresetStatus = PlanText.LoadoutLoadedPreset(saved.Name);
    }

    /// <summary>Picks the saved kit the current one is measured against, or none.</summary>
    public void Compare(string? name)
    {
        _comparingPreset = name;
        Presets = [.. Presets.Select(row => row with
        {
            IsComparing = string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase),
        })];
        if (name is null)
        {
            Comparison = [];
            return;
        }

        RefreshComparisonAsync(CancellationToken.None).Observe("loadout", "refresh the comparison");
    }

    /// <summary>Removes a saved kit.</summary>
    public async Task DeletePresetAsync(string name, CancellationToken cancellationToken)
    {
        if (_presets is null)
        {
            return;
        }

        try
        {
            await _presets.DeleteAsync(name, cancellationToken).ConfigureAwait(true);
            if (string.Equals(_comparingPreset, name, StringComparison.OrdinalIgnoreCase))
            {
                _comparingPreset = null;
                Comparison = [];
            }

            await LoadPresetsAsync(cancellationToken).ConfigureAwait(true);
            PresetStatus = PlanText.LoadoutDeletedPreset(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PresetStatus = PlanText.LoadoutNotDeleted(exception.Message);
        }
    }

    private LoadoutPresetViewModel Describe(LoadoutPreset preset)
    {
        var name = preset.Name;
        return new(
            name,
            PlanText.LoadoutPresetDetail(preset.Items.Count, LocalTime.ToLocal(preset.SavedUtc).ToString("d MMM HH:mm", CultureInfo.CurrentCulture)),
            string.Equals(_comparingPreset, name, StringComparison.OrdinalIgnoreCase),
            new AsyncDelegateCommand(() => LoadPresetAsync(name, CancellationToken.None)),
            new DelegateCommand(() => Compare(string.Equals(_comparingPreset, name, StringComparison.OrdinalIgnoreCase) ? null : name)),
            new AsyncDelegateCommand(() => DeletePresetAsync(name, CancellationToken.None)));
    }

    /// <summary>
    /// Prices and weighs the kit being compared against, and states the differences.
    /// </summary>
    /// <remarks>
    /// The saved kit is run through the same evaluation the current one is, rather than storing
    /// the numbers it had when it was saved. Prices move; a comparison against last week's price
    /// of a kit would be a comparison against nothing in particular.
    /// </remarks>
    private async Task RefreshComparisonAsync(CancellationToken cancellationToken)
    {
        if (_presets is null || _comparingPreset is null)
        {
            Comparison = [];
            return;
        }

        var saved = (await _presets.GetAsync(cancellationToken).ConfigureAwait(true))
            .FirstOrDefault(preset => string.Equals(preset.Name, _comparingPreset, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            Comparison = [];
            return;
        }

        try
        {
            var service = await EnsureServiceAsync(cancellationToken).ConfigureAwait(true);
            var other = await service
                .EvaluateAsync(SelectionFrom(saved), profile: null, cancellationToken)
                .ConfigureAwait(true);
            Comparison =
            [
                new(
                    PlanText.LoadoutApproximateCost,
                    Roubles(_evaluatedCost),
                    Roubles(other.ApproximateCostRoubles),
                    Difference(_evaluatedCost, other.ApproximateCostRoubles)),
                new(
                    PlanText.LoadoutTotalWeight,
                    Kilograms(_evaluatedWeight),
                    Kilograms(other.ApproximateWeightKg),
                    Difference(_evaluatedWeight, other.ApproximateWeightKg)),
                new(PlanText.LoadoutAmmoTier, _evaluatedAmmoTier, other.AmmoTier, string.Empty),
                new(
                    PlanText.LoadoutSlotsFilledMeasure,
                    _selection.Count.ToString(CultureInfo.CurrentCulture),
                    saved.Items.Select(item => item.Slot).Distinct(StringComparer.Ordinal).Count().ToString(CultureInfo.CurrentCulture),
                    string.Empty),
                new(
                    PlanText.LoadoutCompatibilityIssues,
                    Issues.Count.ToString(CultureInfo.CurrentCulture),
                    other.CompatibilityIssues.Count.ToString(CultureInfo.CurrentCulture),
                    string.Empty),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Comparison = [];
            PresetStatus = PlanText.LoadoutNotCompared(exception.Message);
        }
    }

    private static LoadoutSelection SelectionFrom(LoadoutPreset preset)
    {
        var bySlot = preset.Items
            .Where(item => Enum.TryParse<LoadoutSlot>(item.Slot, out _))
            .GroupBy(item => Enum.Parse<LoadoutSlot>(item.Slot))
            .ToDictionary(group => group.Key, group => group.Select(item => item.ItemId).ToArray());

        string? One(LoadoutSlot slot) => bySlot.TryGetValue(slot, out var ids) && ids.Length > 0 ? ids[0] : null;
        IReadOnlyList<string> Many(LoadoutSlot slot) => bySlot.TryGetValue(slot, out var ids) ? ids : [];

        return new(
            One(LoadoutSlot.Weapon),
            One(LoadoutSlot.Ammunition),
            Many(LoadoutSlot.Magazine),
            One(LoadoutSlot.Armor),
            Many(LoadoutSlot.Plate),
            One(LoadoutSlot.Helmet),
            One(LoadoutSlot.Headset),
            One(LoadoutSlot.Rig),
            One(LoadoutSlot.Backpack),
            Many(LoadoutSlot.Medical));
    }

    private static string Roubles(long? value) =>
        value is { } amount ? amount.ToString("N0", CultureInfo.CurrentCulture) + " \u20bd" : PlanText.LoadoutUnknownValue;

    private static string Kilograms(double? value) =>
        value is { } weight ? weight.ToString("0.##", CultureInfo.CurrentCulture) + " kg" : PlanText.LoadoutUnknownValue;

    private static string Difference(long? current, long? other) =>
        current is { } left && other is { } right
            ? Signed(left - right, (left - right).ToString("N0", CultureInfo.CurrentCulture) + " \u20bd")
            : string.Empty;

    private static string Difference(double? current, double? other) =>
        current is { } left && other is { } right
            ? Signed((long)Math.Sign(left - right), (left - right).ToString("+0.##;-0.##;0", CultureInfo.CurrentCulture) + " kg")
            : string.Empty;

    private static string Signed(long sign, string text) => sign > 0 ? "+" + text : text;

    /// <summary>
    /// Restates the budget line against the last evaluation.
    /// </summary>
    /// <remarks>
    /// Against the *evaluated* cost, not a running total of what is assigned: the totals count a
    /// missing price as zero and are floors, and a budget line that moved while items were being
    /// added would be quoting a number the page had not computed.
    /// </remarks>
    private void RefreshBudget()
    {
        if (!TryReadBudget(BudgetInput, out var budget))
        {
            IsOverBudget = false;
            BudgetSummary = BudgetInput.Trim().Length == 0
                ? NoBudget
                : PlanText.LoadoutNotRoubles;
            return;
        }

        if (_evaluatedCost is not { } cost)
        {
            IsOverBudget = false;
            BudgetSummary = PlanText.LoadoutBudgetSet(Roubles(budget));
            return;
        }

        var difference = budget - cost;
        IsOverBudget = difference < 0;
        BudgetSummary = difference >= 0
            ? PlanText.LoadoutBudgetLeft(Roubles(cost), Roubles(budget), Roubles(difference))
            : PlanText.LoadoutBudgetOver(Roubles(cost), Roubles(budget), Roubles(-difference));
    }

    /// <summary>Reads a typed budget, accepting separators and a trailing k or m.</summary>
    internal static bool TryReadBudget(string? input, out long roubles)
    {
        roubles = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("\u20bd", string.Empty, StringComparison.Ordinal);
        var multiplier = 1L;
        if (text.EndsWith('k') || text.EndsWith('K'))
        {
            multiplier = 1_000;
            text = text[..^1];
        }
        else if (text.EndsWith('m') || text.EndsWith('M'))
        {
            multiplier = 1_000_000;
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value < 0 || value * multiplier > long.MaxValue)
        {
            return false;
        }

        roubles = (long)Math.Round(value * multiplier);
        return true;
    }

    /// <summary>Rebuilds the ten tiles from whatever is assigned.</summary>
    private void RefreshSlotBoard()
    {
        SlotBoard =
        [
            .. SlotOptions.Select(option =>
            {
                var items = _selection.GetValueOrDefault(option.Slot) ?? [];
                var slot = option.Slot;
                return new LoadoutSlotTileViewModel(
                    slot,
                    option.Name,
                    option.Hint,
                    items.Count > 0,
                    items.Count == 0 ? PlanText.LoadoutEmpty : string.Join(", ", items.Select(item => item.Name)),
                    items.Count == 0 ? option.Hint : items[0].Detail,
                    SelectedSlot.Slot == slot,
                    $"v2-loadout-slot-{slot.ToString().ToLowerInvariant()}",
                    new DelegateCommand(() => SelectedSlot = option),
                    new DelegateCommand(() => ClearSlot(slot)));
            }),
        ];
        OnPropertyChanged(nameof(SlotBoardSummary));
    }

    private void ClearSlot(LoadoutSlot slot)
    {
        if (!_selection.Remove(slot))
        {
            return;
        }

        RefreshAssignments();
        AssignmentStatus = PlanText.LoadoutSlotEmptyAgain(SlotOptions.First(option => option.Slot == slot).Name);
        _ = RefreshAlternativesAsync(CancellationToken.None);
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
            var category = KindOf(itemId, item?.Category ?? ItemCategory.Unknown, _facts);
            if (!LoadoutSlotRules.Accepts(slot.Slot, category))
            {
                // The results were filtered for the slot they were searched under; the slot can
                // have been changed since, and a preset or the tablet can ask for anything.
                AssignmentStatus = LoadoutSlotRules.Refusal(slot.Name, name, category);
                return;
            }

            if (!_selection.TryGetValue(slot.Slot, out var items))
            {
                items = [];
                _selection[slot.Slot] = items;
            }

            if (!slot.AllowsMany)
            {
                items.Clear();
            }

            items.Add(new(itemId, name, $"{category} · {DescribeCost(facts)} · {DescribeWeight(facts)}{DescribeGear(facts)}"));
            await RefreshAllergyWarningsAsync(cancellationToken).ConfigureAwait(true);
            RefreshAssignments();
            await RefreshAlternativesAsync(cancellationToken).ConfigureAwait(true);
            AssignmentStatus = slot.AllowsMany
                ? PlanText.LoadoutAddedToSlot(name, slot.Name)
                : PlanText.LoadoutSlotIsNow(slot.Name, name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AssignmentStatus = PlanText.LoadoutNotAssigned(exception.Message);
        }
    }

    /// <summary>
    /// Re-reads which foods and medicines the player is recorded allergic to. Read when the kit
    /// changes rather than once, because the record is made on another page in the same session.
    /// A failure here costs the warning, never the assignment.
    /// </summary>
    private async Task RefreshAllergyWarningsAsync(CancellationToken cancellationToken)
    {
        if (_allergies is null)
        {
            return;
        }

        try
        {
            _allergyWarnings = await OffInterfaceThread
                .Run(() => _allergies.GetAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("loadout", "read allergies", exception);
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
        AssignmentStatus = PlanText.LoadoutRemovedItem(removed.Name);
        _ = RefreshAlternativesAsync(CancellationToken.None);
    }

    private async Task RefreshAlternativesAsync(CancellationToken cancellationToken)
    {
        if (_acquisitions is null)
        {
            Alternatives = [];
            AlternativesStatus = PlanText.LoadoutNoAcquisitionCatalog;
            return;
        }

        try
        {
            var facts = await EnsureFactsAsync(cancellationToken).ConfigureAwait(true);
            var slot = SelectedSlot.Slot;
            var assigned = _selection.GetValueOrDefault(slot)?.Select(item => item.ItemId)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            if (assigned.Count == 0)
            {
                Alternatives = [];
                AlternativesStatus = PlanText.LoadoutAssignForAlternatives;
                return;
            }

            var currentCost = assigned.Select(id => facts.GetValueOrDefault(id)?.ApproximateCostRoubles)
                .FirstOrDefault(cost => cost is not null);
            var candidates = _factList
                .Where(fact => !assigned.Contains(fact.ItemId) && LoadoutSlotRules.Fits(slot, fact.Category))
                .OrderBy(fact => currentCost is { } cost && fact.ApproximateCostRoubles is { } candidateCost
                    ? Math.Abs(candidateCost - cost)
                    : long.MaxValue)
                .ThenBy(fact => fact.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(160)
                .ToArray();
            var byId = candidates.ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);
            var sources = await _acquisitions.GetAsync(byId.Keys.ToArray(), cancellationToken).ConfigureAwait(true);

            Alternatives = sources
                .Where(row => byId.ContainsKey(row.Offer.ItemId))
                .Take(8)
                .Select(row =>
                {
                    var fact = byId[row.Offer.ItemId];
                    var source = row.Offer.Kind == ItemAcquisitionKind.Cash && row.Offer.PriceRoubles is { } price
                        ? $"{row.Offer.TraderName} · {Roubles(price)}"
                        : PlanText.LoadoutTraderBarter(row.Offer.TraderName);
                    return new LoadoutAlternativeViewModel(
                        fact.Name,
                        $"{DescribeCost(fact)} · {DescribeWeight(fact)}{DescribeGear(fact)}",
                        source,
                        row.Availability.RequirementLabel,
                        row.Availability.IsObtainable,
                        new AsyncDelegateCommand(() => AssignAsync(fact.ItemId, CancellationToken.None)));
                })
                .ToArray();
            AlternativesStatus = Alternatives.Count == 0
                ? PlanText.LoadoutNoTraderAlternatives
                : PlanText.LoadoutAlternativeCount(Alternatives.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Alternatives = [];
            AlternativesStatus = PlanText.LoadoutAlternativesUnavailable(exception.Message);
        }
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
                    new DelegateCommand(() => Remove(slot, position)),
                    option.AllowsMany)
                {
                    AllergyWarning = _allergyWarnings.GetValueOrDefault(items[index].ItemId, string.Empty),
                });
            }
        }

        Assignments = rows;
        MultiAssignments = [.. rows.Where(row => row.AllowsMany)];
        RefreshSlotBoard();
        if (rows.Count == 0)
        {
            AssignmentStatus = PlanText.LoadoutNothingAssigned;
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
            KindOf(item.Id, item.Category, facts).ToString(),
            DescribeCost(fact),
            DescribeWeight(fact),
            DescribeDetail(fact),
            new AsyncDelegateCommand(() => AssignAsync(item.Id, CancellationToken.None)));
    }

    /// <summary>
    /// What kind of item this is: the item's own category, or the fact table's when the item
    /// says Unknown. A weapon preset is filed as Unknown and its facts carry its base weapon's
    /// kind (see SqliteItemFactCatalog.InheritFromPresetBases).
    /// </summary>
    private static ItemCategory KindOf(string itemId, ItemCategory category, IReadOnlyDictionary<string, LoadoutItemFacts> facts) =>
        category == ItemCategory.Unknown && facts.GetValueOrDefault(itemId) is { } fact ? fact.Category : category;

    internal static LoadoutFindingViewModel Finding(LoadoutEvaluation evaluation, string message) =>
        new(message, evaluation.Explanations?.GetValueOrDefault(message) ?? string.Empty);

    internal static string DescribeCost(LoadoutEvaluation evaluation)
    {
        if (evaluation.ApproximateCostRoubles is { } cost)
        {
            return Roubles(cost);
        }

        // Not a total: some assigned items have no price. What is known is a floor, and it says how
        // many of the kit it is a floor of, so a kit missing one price no longer reads as one missing all.
        var coverage = evaluation.CostCoverage;
        return evaluation.KnownCostRoubles is { } known
            ? PlanText.LoadoutCostAtLeast(Roubles(known), coverage.Known, coverage.Total)
            : PlanText.LoadoutCostNoTotal(coverage.Known, coverage.Total);
    }

    internal static string DescribeWeight(LoadoutEvaluation evaluation)
    {
        if (evaluation.ApproximateWeightKg is { } weight)
        {
            return Kilograms(weight);
        }

        var coverage = evaluation.WeightCoverage;
        return evaluation.KnownWeightKg is { } known
            ? PlanText.LoadoutWeightAtLeast(Kilograms(known), coverage.Known, coverage.Total)
            : PlanText.LoadoutWeightNoTotal(coverage.Known, coverage.Total);
    }

    private readonly IPlayerProfileService? _profiles;

    /// <summary>" · 180 owned" for the assigned round (a pack counts as its round), or nothing.</summary>
    private async Task<string> DescribeOwnedRoundsAsync(CancellationToken cancellationToken)
    {
        if (_profiles is null || FirstId(LoadoutSlot.Ammunition) is not { } assigned)
        {
            return string.Empty;
        }

        var packs = await _catalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(true);
        var round = packs.FirstOrDefault(pack => string.Equals(pack.PackItemId, assigned, StringComparison.Ordinal))?.AmmoItemId ?? assigned;
        var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(true);
        return new OwnedAmmo(profile.OwnedItemCounts, packs).RoundsOf(round) switch
        {
            null => PlanText.LoadoutOwnedNotScanned,
            var count => " · " + OwnedAmmo.Short(count).ToLower(CultureInfo.CurrentCulture),
        };
    }

    // The evaluator's own word for no tier, compared, not shown; the shown text is PlanText's.
    private const string UnknownTier = "Unknown";

    private static string DescribeAmmoTier(LoadoutEvaluation evaluation) =>
        string.Equals(evaluation.AmmoTier, UnknownTier, StringComparison.Ordinal)
            ? PlanText.LoadoutAmmoTierUnknown
            : PlanText.LoadoutAmmoTierIn(evaluation.AmmoTier);

    private static string DescribeCost(LoadoutItemFacts? facts) =>
        facts?.ApproximateCostRoubles is { } cost ? Roubles(cost) : PlanText.LoadoutNoPrice;

    private static string DescribeWeight(LoadoutItemFacts? facts) =>
        facts?.WeightKg is { } weight ? Kilograms(weight) : PlanText.LoadoutNoWeightValue;

    // The figures the catalog states for a piece of gear, such as "Class 6 · Ceramic · 60 durability".
    private static string DescribeGear(LoadoutItemFacts? facts) =>
        facts?.Gear is { } gear && GearFactsReader.Summarize(gear) is { } summary ? $" · {summary}" : string.Empty;

    private static string DescribeDetail(LoadoutItemFacts? facts) =>
        facts?.Caliber is { } caliber
            ? CaliberText.Describe(caliber, facts.Name)
            : facts?.Gear is { } gear
                ? GearFactsReader.Summarize(gear) ?? PlanText.LoadoutNoFigures
                : PlanText.LoadoutNoCaliber;

    private void ResetEvaluation(string status)
    {
        Issues = [];
        Warnings = [];
        CostSummary = PlanText.LoadoutNoCostYet;
        WeightSummary = PlanText.LoadoutNoWeightYet;
        AmmoTierSummary = PlanText.LoadoutNoAmmunitionAssigned;
        IssuesStatus = PlanText.LoadoutNotEvaluatedYet;
        WarningsStatus = PlanText.LoadoutNotEvaluatedYet;
        EvaluationStatus = status;
    }

    private static string Roubles(long value) => value.ToString("N0", CultureInfo.CurrentCulture) + " ₽";

    private static string Kilograms(double value) => value.ToString("N2", CultureInfo.CurrentCulture) + " kg";

    /// <summary>What the page remembers about one assigned item.</summary>
    private sealed record AssignedItem(string ItemId, string Name, string Detail);
}
