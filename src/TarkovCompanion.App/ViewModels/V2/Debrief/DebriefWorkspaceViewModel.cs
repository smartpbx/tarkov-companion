using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.V2.Shell;
using System.Globalization;
using TarkovCompanion.App.Localization;
using System.Text.Json;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>One raid, in the list.</summary>
public sealed record DebriefRaidRowViewModel(
    Guid RaidId,
    string MapLabel,
    string Mode,
    string StartedLabel,
    string EndedLabel,
    string DurationLabel,
    string Outcome)
{
    public ICommand? SelectCommand { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>Where the outcome came from ("Manual", "Inferred"), or empty where there is none to label.</summary>
    public string OutcomeKindLabel { get; init; } = string.Empty;

    public bool HasOutcomeKind => OutcomeKindLabel.Length > 0;

    public string TagsLabel { get; init; } = string.Empty;

    public bool HasTags => TagsLabel.Length > 0;

    /// <summary>[#269] The wipe the raid was played in, under its mode; empty where it cannot be placed.</summary>
    public string WipeLabel { get; init; } = string.Empty;

    public bool HasWipe => WipeLabel.Length > 0;
}

/// <summary>One fact about the selected raid, with the kind of evidence behind it.</summary>
/// <param name="KindLabel">"Observed", "Inferred", "Estimate" or "Manual"; empty where there is no value to label.</param>
public sealed record DebriefFactRowViewModel(string Label, string Value, string KindLabel)
{
    public bool HasKind => KindLabel.Length > 0;
}

/// <summary>One scan taken during the selected raid.</summary>
/// <remarks>
/// What the companion recognised is inferred and what it is worth is an estimate, so each says so;
/// nothing here claims value was carried out of the raid.
/// </remarks>
public sealed record DebriefScanRowViewModel(
    string TimeLabel,
    string ItemLabel,
    string IdentityKindLabel,
    string ValueLabel,
    string ValueKindLabel,
    string DetailLabel)
{
    public bool HasIdentityKind => IdentityKindLabel.Length > 0;

    public bool HasValue => ValueLabel.Length > 0;

    public bool HasDetail => DetailLabel.Length > 0;

    public string ScanId { get; init; } = string.Empty;

    /// <summary>[#902 P9] The recognised item, for the row's link to its Intel page; empty when none.</summary>
    public string ItemId { get; init; } = string.Empty;

    public bool HasItem => ItemId.Length > 0;

    public bool HasNoItem => !HasItem;

    public bool IsWrong { get; init; }

    public string CorrectionLabel => IsWrong ? DebriefText.MarkedWrong : string.Empty;

    public bool HasCorrection => IsWrong;

    public string CorrectionActionLabel => IsWrong ? DebriefText.Restore : DebriefText.Wrong;

    public ICommand? CorrectionCommand { get; init; }

    /// <summary>A saved Loot Scan's calls, one line each ("TAKE Graphics card · ₽300k"). #274.</summary>
    public IReadOnlyList<string> VerdictLines { get; init; } = [];

    public bool HasVerdictLines => VerdictLines.Count > 0;
}

/// <summary>A raid's trail, handed to the shell to draw on the Raid map (V1's "Watch it").</summary>
/// <remarks>
/// Carries the raid's own map because a replay is only positions: V1 drew them on whatever map was
/// showing, so the shell chooses the map first.
/// </remarks>
public sealed record DebriefReplayRequest(
    string? MapId,
    string Title,
    IReadOnlyList<ScreenshotPosition> Positions,
    IReadOnlyList<WorldPosition> PlannedRoute,
    string Comparison);

/// <summary>One flea offer that sold during a raid, counted per item rather than per offer.</summary>
public sealed record DebriefSaleRowViewModel(string ItemLabel, string CountLabel, string TimeLabel);

/// <summary>One quest the game announced during a raid.</summary>
public sealed record DebriefQuestEventRowViewModel(string QuestLabel, string StateLabel, string TimeLabel);

/// <summary>What the companion has measured on one map: how often it's played and how it goes.</summary>
public sealed record DebriefMapStatRowViewModel(
    string MapLabel,
    string RaidsLabel,
    string DurationLabel,
    string LoadLabel,
    string ManualLabel = "",
    string ManualKindLabel = "")
{
    /// <summary>Whether any raid on this map has a manual kills or value field entered.</summary>
    public bool HasManual => ManualLabel.Length > 0;
}

/// <summary>
/// The outcome buckets the filter offers. The game never writes an outcome (see
/// <see cref="RaidFactRules"/>), so a raid's outcome is whatever the player typed; matching is by
/// keyword against that free text rather than an exact value.
/// </summary>
public enum DebriefOutcomeFilter { Any, Survived, Died, Mia, RunThrough }

/// <summary>Which side a raid was played on, where the log let the companion tell.</summary>
public enum DebriefSideFilter { Any, Pmc, Scav }

/// <summary>One choice in the map filter; a null id is every map.</summary>
public sealed record DebriefMapFilterOption(string? MapId, string Label);

/// <summary>One tag filter choice; a null tag means every raid.</summary>
public sealed record DebriefTagFilterOption(string? Tag, string Label);

/// <summary>One tag on the selected raid, with its own remove action.</summary>
public sealed record DebriefTagViewModel(string Label, ICommand RemoveCommand);

/// <summary>One filter chip (outcome or side), in the Plan tab's chip shape.</summary>
public sealed class DebriefFilterChipViewModel : BindableViewModel
{
    private bool _isSelected;

    internal DebriefFilterChipViewModel(string label, string automationId, Action select)
    {
        Label = label;
        AutomationId = automationId;
        SelectCommand = new DelegateCommand(select);
    }

    public string Label { get; }

    public string AutomationId { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// V2 workspace over the existing raid-history backend: raid list, raid detail (map, duration,
/// outcome, screenshot count), a correction for a wrong outcome or note, and export. Replaces the
/// legacy History passthrough on the Debrief route.
/// </summary>
public sealed partial class DebriefWorkspaceViewModel : BindableViewModel
{
    private readonly IRaidHistoryService _raidHistoryService;
    private readonly ILootScanHistoryStore? _lootScans;
    private readonly AppDataPaths _paths;
    private readonly TimeProvider _clock;
    // Optional: naming what sold and which quest fired is a readability improvement over the
    // raw ids the history stores, not something the workspace needs to function without.
    private readonly IItemRepository? _items;
    private readonly IQuestCatalog? _questCatalog;
    private readonly IPlayerProfileService? _profileService;
    private readonly QuestTrackingOptions? _questOptions;
    private readonly DebriefSavedViewStore _savedViewStore;
    private readonly Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _taskNames = new(StringComparer.Ordinal);
    private bool _taskCatalogLoaded;
    private RaidHistoryEntry? _selected;
    private IReadOnlyList<ScreenshotPosition> _selectedPositions = [];
    private RaidPlannedRoute? _selectedPlannedRoute;
    private RaidFactSources _selectedSources = new(
        RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown);
    private bool _selectedLoadRecorded;
    private string _status = DebriefText.Loading;
    private PageLoadState _loadState = PageLoadState.Loading;
    private string _correctedOutcome = string.Empty;
    private string _correctedNotes = string.Empty;
    private Func<string, string?> _mapName = _ => null;

    // Search and filters (#291 package 2). Every raid's side, evidence sources and load time are
    // read once per load, off the interface thread; filtering and the per-map stats that follow it
    // are then pure in-memory work, so typing in the search box costs no I/O.
    private IReadOnlyList<DebriefRaidRecord> _allRecords = [];
    private string _searchText = string.Empty;
    private string? _mapFilter;
    private string? _tagFilter;
    private DebriefOutcomeFilter _outcomeFilter = DebriefOutcomeFilter.Any;
    private DebriefSideFilter _sideFilter = DebriefSideFilter.Any;
    private DateTimeOffset? _dateFrom;
    private DateTimeOffset? _dateTo;
    private IReadOnlyList<DebriefMapFilterOption> _mapFilterOptions = [new(null, DebriefText.AllMaps)];
    private IReadOnlyList<DebriefTagFilterOption> _tagFilterOptions = [new(null, DebriefText.AllTags)];
    private ICommand? _clearSearch;
    private ICommand? _clearFilters;
    private string _newTag = string.Empty;
    private string _savedViewName = string.Empty;
    private DebriefSavedView? _selectedSavedView;

    // Delete and undo (#291 package 3). A delete soft-deletes; the one-press undo below is what
    // makes it safe. See SqliteRaidHistoryService.PurgeDeletedAsync and the load above for when a
    // soft-deleted raid is actually removed.
    private IReadOnlyList<Guid> _pendingDeleteIds = [];
    private bool _isConfirmingDelete;
    private bool _isConfirmingBulkDelete;
    private DateTimeOffset? _deleteBeforeDate;

    // Manual fields (#291 package 4): kills and the value brought out, typed by hand on a raid.
    private decimal? _manualPmcKills;
    private decimal? _manualScavKills;
    private decimal? _manualBossKills;
    private decimal? _manualValueRoubles;

    /// <summary>One raid plus what the workspace already knows about it, built once per load.</summary>
    private sealed record DebriefRaidRecord(
        RaidHistoryEntry Raid,
        string? Side,
        RaidFactSources Sources,
        double? LoadSeconds,
        RaidManualMetadata? Manual,
        IReadOnlyList<string> Tags)
    {
        /// <summary>What the raid's photographed extract lists offered; null when none was photographed.</summary>
        public IReadOnlyList<string>? OfferedExtracts { get; init; }

        /// <summary>The extract the player recorded leaving by, or null.</summary>
        public string? UsedExtract { get; init; }

        public bool IsArchived { get; init; }

        /// <summary>[#269] How the raid stands against the active profile's mode and wipe.</summary>
        public RaidContextMatch Match { get; init; }

        public string? Wipe { get; init; }
    }

    public DebriefWorkspaceViewModel(
        IRaidHistoryService raidHistoryService,
        AppDataPaths paths,
        TimeProvider? clock = null,
        IItemRepository? items = null,
        IQuestCatalog? questCatalog = null,
        IPlayerProfileService? profileService = null,
        QuestTrackingOptions? questOptions = null,
        IWorkspaceLayoutStore? layoutStore = null,
        ILootScanHistoryStore? lootScans = null,
        IRaidContextSource? raidContext = null,
        IRaidEndSignal? raidEnds = null,
        IMapDataService? maps = null,
        Action<Action>? dispatch = null,
        // [#712 2-3] The recap's hand-in line reads the player's own quest board.
        IQuestReadService? questRead = null)
    {
        _questRead = questRead;
        _lootScans = lootScans;
        _raidContext = raidContext;
        _raidHistoryService = raidHistoryService ?? throw new ArgumentNullException(nameof(raidHistoryService));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? TimeProvider.System;
        _items = items;
        _questCatalog = questCatalog;
        _profileService = profileService;
        _questOptions = questOptions;
        _savedViewStore = new(layoutStore);
        RestorePageState(layoutStore);
        SavedViews = _savedViewStore.Load();

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        SaveCorrectionCommand = new AsyncDelegateCommand(SaveCorrectionAsync);
        ExportCsvCommand = new AsyncDelegateCommand(ExportCsvAsync);
        ExportJsonCommand = new AsyncDelegateCommand(ExportJsonAsync);
        WatchOnMapCommand = new DelegateCommand(WatchOnMap);
        BeginDeleteCommand = new DelegateCommand(BeginDelete);
        CancelDeleteCommand = new DelegateCommand(CancelDelete);
        ConfirmDeleteCommand = new AsyncDelegateCommand(ConfirmDeleteAsync);
        BeginBulkDeleteCommand = new DelegateCommand(BeginBulkDelete);
        CancelBulkDeleteCommand = new DelegateCommand(CancelBulkDelete);
        ConfirmBulkDeleteCommand = new AsyncDelegateCommand(ConfirmBulkDeleteAsync);
        UndoDeleteCommand = new AsyncDelegateCommand(UndoDeleteAsync);
        SaveManualMetadataCommand = new AsyncDelegateCommand(SaveManualMetadataAsync);
        AddTagCommand = new AsyncDelegateCommand(AddTagAsync);
        SaveViewCommand = new DelegateCommand(SaveView);
        ApplySavedViewCommand = new DelegateCommand(ApplySavedView);
        DeleteSavedViewCommand = new DelegateCommand(DeleteSavedView);
        InitialiseAfterRaid(raidEnds, maps, dispatch);
    }

    public IReadOnlyList<DebriefRaidRowViewModel> Raids { get; private set; } = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasRaids => Raids.Count > 0;

    public bool HasNoRaids => !HasRaids;

    /// <summary>#871/#872: loading, empty, loaded or failed — the view shows one message for one state.</summary>
    public PageLoadState LoadState
    {
        get => _loadState;
        private set
        {
            if (SetProperty(ref _loadState, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(ShowsNoRaids));
            }
        }
    }

    public bool IsLoading => _loadState == PageLoadState.Loading;

    /// <summary>The empty message, only once a read has succeeded: a failed or unfinished read is not an empty history.</summary>
    public bool ShowsNoRaids => HasNoRaids && _loadState.HasRead();

    /// <summary>Shown in place of the table when the history could not be read, with Retry.</summary>
    public LoadFaultNoticeViewModel LoadFault => _loadFault ??= new(() => LoadAsync(CancellationToken.None));

    private LoadFaultNoticeViewModel? _loadFault;

    /// <summary>Distinguishes an empty history from a filter that matched nothing in it.</summary>
    public string NoRaidsMessage => _showArchived
        ? DebriefText.NoArchivedRaids
        : _allRecords.Count == 0
            ? DebriefText.NoRaidsYet
            : !ContextRecords.Any()
            ? NoRaidsInContextMessage
            : DebriefText.NoRaidsMatch;

    public bool HasSelection => _selected is not null;

    public bool HasNoSelection => !HasSelection;

    public string SelectedMapLabel => _selected?.MapId is { } mapId ? MapLabel(mapId) : string.Empty;

    public string SelectedStartedLabel => LocalTime.Moment(_selected?.StartedUtc) ?? DebriefText.Unknown;

    /// <summary>Package 17 (home): said once, beside the field it explains, instead of in the status line.</summary>
    public string OutcomeHint { get; } = DebriefText.OutcomeHint;

    public string SelectedModeLabel => TarkovCompanion.App.Services.V2.Shell.GameModeLabel.OfStored(_selected?.Mode);

    public string SelectedDurationLabel => Duration(_selected);

    /// <summary>Package 29 (parity): V1's "Ended" column, which the list's Duration column only implied.</summary>
    public string SelectedEndedLabel => LocalTime.Moment(_selected?.EndedUtc) ?? DebriefText.InProgress;

    /// <summary>
    /// How far the raid went, as V1's History page said it ("at least 1.4 km"); empty until there are
    /// two screenshots to measure between.
    /// </summary>
    /// <remarks>
    /// A floor, not a measurement: straight lines between screenshots minutes apart. The wording
    /// says so rather than the page implying the route.
    /// </remarks>
    public string SelectedDistanceLabel
    {
        get
        {
            if (_selectedPositions.Count < 2)
            {
                return string.Empty;
            }

            var metres = HistoryPageViewModel.PathMetres(_selectedPositions);
            return metres >= 1000
                ? DebriefText.AtLeastKm(metres / 1000)
                : DebriefText.AtLeastMetres(metres);
        }
    }

    public bool HasDistance => SelectedDistanceLabel.Length > 0;

    /// <summary>Whether the selected raid has screenshots to watch back — the same test V1 used to show "Watch it".</summary>
    public bool CanWatch => _selectedPositions.Count > 0;

    public string SelectedOutcomeLabel => _selected?.Outcome ?? DebriefText.NotRecorded;

    public string SelectedNotesLabel => _selected?.Notes ?? string.Empty;

    public string SelectedPathLabel => _selectedPositions.Count switch
    {
        0 => DebriefText.NoScreenshots,
        var count => DebriefText.ScreenshotsRecorded(count),
    };

    /// <summary>The plan/actual comparison is bounded by screenshots; it is not live tracking.</summary>
    public string SelectedRouteComparisonLabel
    {
        get
        {
            if (_selectedPlannedRoute is null)
            {
                return string.Empty;
            }

            var prefix = DebriefText.PlannedTo(_selectedPlannedRoute.Extract);
            return RaidRouteDeviation.Measure(_selectedPlannedRoute.Points, _selectedPositions) is not { } deviation
                ? prefix
                : DebriefText.RouteDeviation(prefix, deviation.AverageMetres, deviation.FurthestMetres);
        }
    }

    public bool HasSelectedRouteComparison => _selectedPlannedRoute is not null;

    /// <summary>Where the map came from, beside the raid's name in the detail heading.</summary>
    public string SelectedMapKindLabel => DebriefText.Kind(_selectedSources.Map);

    public bool HasSelectedMapKind => SelectedMapKindLabel.Length > 0;

    /// <summary>The distance is a floor built from straight lines between screenshots, so it is an estimate.</summary>
    public string SelectedDistanceKindLabel => HasDistance ? DebriefText.Kind(RaidFactKind.Estimated) : string.Empty;

    /// <summary>The detail facts of the selected raid, each with the kind of evidence behind it.</summary>
    public IReadOnlyList<DebriefFactRowViewModel> SelectedFacts { get; private set; } = [];

    /// <summary>What was scanned during the selected raid.</summary>
    public IReadOnlyList<DebriefScanRowViewModel> SelectedScans { get; private set; } = [];

    public bool HasSelectedScans => SelectedScans.Count > 0;

    /// <summary>"3 scans · 2 recognised": how many were taken and how many named an item, unavailable ones counted apart.</summary>
    public string SelectedScanSummary { get; private set; } = DebriefText.NoScans;

    /// <summary>How long matchmaking and loading took before this raid began, if it was seen.</summary>
    public string SelectedLoadTimeLabel { get; private set; } = DebriefText.LoadNotRecorded;

    public IReadOnlyList<DebriefSaleRowViewModel> SelectedSales { get; private set; } = [];

    public bool HasSelectedSales => SelectedSales.Count > 0;

    /// <summary>What the game reported starting, failing or finishing while this raid was open.</summary>
    public IReadOnlyList<DebriefQuestEventRowViewModel> SelectedQuestEvents { get; private set; } = [];

    public bool HasSelectedQuestEvents => SelectedQuestEvents.Count > 0;

    /// <summary>
    /// What the companion has measured per map: how many raids, how long, and — where a
    /// matchmaking line was seen — how long loading took. Never survival: the game records no
    /// outcome, and this workspace does not invent one from what a player typed by hand.
    /// </summary>
    public IReadOnlyList<DebriefMapStatRowViewModel> MapStats { get; private set; } = [];

    public bool HasMapStats => MapStats.Count > 0;

    /// <summary>Words that must all appear in a raid's notes for it to stay in the list.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearchText));
                ApplyFilters();
            }
        }
    }

    public bool HasSearchText => _searchText.Length > 0;

    public ICommand ClearSearchCommand => _clearSearch ??= new DelegateCommand(() => SearchText = string.Empty);

    /// <summary>Every map filter choice: "All maps" plus one per map this history has a raid on.</summary>
    public IReadOnlyList<DebriefMapFilterOption> MapFilterOptions => _mapFilterOptions;

    public DebriefMapFilterOption SelectedMapFilterOption
    {
        get => _mapFilterOptions.FirstOrDefault(option => option.MapId == _mapFilter) ?? _mapFilterOptions[0];
        set
        {
            var mapId = value?.MapId;
            if (_mapFilter != mapId)
            {
                _mapFilter = mapId;
                OnPropertyChanged();
                ApplyFilters();
            }
        }
    }

    public IReadOnlyList<DebriefTagFilterOption> TagFilterOptions => _tagFilterOptions;

    public DebriefTagFilterOption SelectedTagFilterOption
    {
        get => _tagFilterOptions.FirstOrDefault(option => string.Equals(option.Tag, _tagFilter, StringComparison.OrdinalIgnoreCase))
            ?? _tagFilterOptions[0];
        set
        {
            var tag = value?.Tag;
            if (!string.Equals(_tagFilter, tag, StringComparison.OrdinalIgnoreCase))
            {
                _tagFilter = tag;
                OnPropertyChanged();
                ApplyFilters();
            }
        }
    }

    /// <summary>Survived / Died / MIA / Run-through, or Any — see <see cref="DebriefOutcomeFilter"/>.</summary>
    public IReadOnlyList<DebriefFilterChipViewModel> OutcomeFilterChips => CreateOutcomeChips();

    /// <summary>PMC / Scav, or Any — see <see cref="DebriefSideFilter"/>.</summary>
    public IReadOnlyList<DebriefFilterChipViewModel> SideFilterChips => CreateSideChips();

    public DateTimeOffset? DateFrom
    {
        get => _dateFrom;
        set
        {
            if (SetProperty(ref _dateFrom, value))
            {
                ApplyFilters();
            }
        }
    }

    public DateTimeOffset? DateTo
    {
        get => _dateTo;
        set
        {
            if (SetProperty(ref _dateTo, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool HasActiveFilters =>
        _mapFilter is not null
        || _tagFilter is not null
        || _outcomeFilter != DebriefOutcomeFilter.Any
        || _sideFilter != DebriefSideFilter.Any
        || _dateFrom is not null
        || _dateTo is not null
        || HasSearchText;

    public ICommand ClearFiltersCommand => _clearFilters ??= new DelegateCommand(ClearFilters);

    public IReadOnlyList<DebriefSavedView> SavedViews { get; private set; }

    public bool HasSavedViews => SavedViews.Count > 0;

    public DebriefSavedView? SelectedSavedView
    {
        get => _selectedSavedView;
        set => SetProperty(ref _selectedSavedView, value);
    }

    public string SavedViewName
    {
        get => _savedViewName;
        set => SetProperty(ref _savedViewName, value ?? string.Empty);
    }

    public ICommand SaveViewCommand { get; }

    public ICommand ApplySavedViewCommand { get; }

    public ICommand DeleteSavedViewCommand { get; }

    public string NewTag
    {
        get => _newTag;
        set => SetProperty(ref _newTag, value ?? string.Empty);
    }

    public IReadOnlyList<DebriefTagViewModel> SelectedTags { get; private set; } = [];

    public bool HasSelectedTags => SelectedTags.Count > 0;

    public ICommand AddTagCommand { get; }

    public string CorrectedOutcome
    {
        get => _correctedOutcome;
        set => SetProperty(ref _correctedOutcome, value);
    }

    public string CorrectedNotes
    {
        get => _correctedNotes;
        set => SetProperty(ref _correctedNotes, value);
    }

    /// <summary>Kills and the value brought out, typed by hand on the selected raid. Always Manual.</summary>
    public decimal? ManualPmcKills
    {
        get => _manualPmcKills;
        set => SetProperty(ref _manualPmcKills, value);
    }

    public decimal? ManualScavKills
    {
        get => _manualScavKills;
        set => SetProperty(ref _manualScavKills, value);
    }

    public decimal? ManualBossKills
    {
        get => _manualBossKills;
        set => SetProperty(ref _manualBossKills, value);
    }

    public decimal? ManualValueRoubles
    {
        get => _manualValueRoubles;
        set => SetProperty(ref _manualValueRoubles, value);
    }

    public ICommand SaveManualMetadataCommand { get; }

    private void SetManualFields(RaidManualMetadata? metadata)
    {
        ManualPmcKills = metadata?.PmcKills;
        ManualScavKills = metadata?.ScavKills;
        ManualBossKills = metadata?.BossKills;
        ManualValueRoubles = metadata?.ValueRoubles;
    }

    private async Task SaveManualMetadataAsync()
    {
        if (_selected is null)
        {
            return;
        }

        var raidId = _selected.Id;
        var metadata = new RaidManualMetadata(
            (int?)ManualPmcKills,
            (int?)ManualScavKills,
            (int?)ManualBossKills,
            (long?)ManualValueRoubles);
        try
        {
            await _raidHistoryService.SetManualMetadataAsync(raidId, metadata, CancellationToken.None).ConfigureAwait(true);
            Status = DebriefText.Saved;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
            // LoadAsync only reselects when nothing is selected; this raid still is, so the fact
            // rows (built from the selection, not the reload) need their own refresh to pick up
            // what was just saved.
            await SelectRaidAsync(raidId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = DebriefText.SaveFailed(exception.Message);
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand SaveCorrectionCommand { get; }

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportJsonCommand { get; }

    /// <summary>Draws the selected raid's trail on the Raid map.</summary>
    public ICommand WatchOnMapCommand { get; }

    /// <summary>Raised when somebody asks to watch the selected raid; the shell owns the map and the router.</summary>
    public event EventHandler<DebriefReplayRequest>? ReplayRequested;

    /// <summary>Whether the selected raid's delete has a preview showing, waiting on Confirm or Cancel.</summary>
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set => SetProperty(ref _isConfirmingDelete, value); }

    /// <summary>What deleting the selected raid removes, said before it happens.</summary>
    public string DeletePreviewLabel { get; private set; } = string.Empty;

    public ICommand BeginDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    /// <summary>The cutoff for "delete every raid before this date"; the player's own local day.</summary>
    public DateTimeOffset? DeleteBeforeDate
    {
        get => _deleteBeforeDate;
        set => SetProperty(ref _deleteBeforeDate, value);
    }

    public bool IsConfirmingBulkDelete { get => _isConfirmingBulkDelete; private set => SetProperty(ref _isConfirmingBulkDelete, value); }

    /// <summary>What "delete before" removes, said before it happens.</summary>
    public string BulkDeletePreviewLabel { get; private set; } = string.Empty;

    public ICommand BeginBulkDeleteCommand { get; }

    public ICommand CancelBulkDeleteCommand { get; }

    public ICommand ConfirmBulkDeleteCommand { get; }

    /// <summary>Whether a delete just happened and can still be undone.</summary>
    public bool CanUndoDelete => _pendingDeleteIds.Count > 0;

    /// <summary>"Deleted 1 raid (Customs, 9/20/2026 7:29 PM)." — what the undo banner names.</summary>
    public string UndoDeleteSummary { get; private set; } = string.Empty;

    public ICommand UndoDeleteCommand { get; }

    /// <summary>Shows what deleting the selected raid removes, and waits on Confirm or Cancel.</summary>
    private void BeginDelete()
    {
        if (_selected is null)
        {
            return;
        }

        DeletePreviewLabel = DebriefText.DeletePreview(
            SelectedMapLabel,
            SelectedStartedLabel,
            SelectedDurationLabel,
            DebriefText.ScanCount(SelectedScans.Count),
            DebriefText.ScreenshotCount(_selectedPositions.Count));
        IsConfirmingDelete = true;
        RaiseAll();
    }

    private void CancelDelete()
    {
        IsConfirmingDelete = false;
        RaiseAll();
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_selected is null)
        {
            IsConfirmingDelete = false;
            return;
        }

        var raidId = _selected.Id;
        var summary = DebriefText.DeletedOne(SelectedMapLabel, SelectedStartedLabel);
        await _raidHistoryService.SoftDeleteAsync([raidId], _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(true);
        _pendingDeleteIds = [raidId];
        UndoDeleteSummary = summary;
        IsConfirmingDelete = false;
        _selected = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Shows how many raids "delete before" removes, and waits on Confirm or Cancel.</summary>
    private void BeginBulkDelete()
    {
        if (_deleteBeforeDate is not { } before)
        {
            Status = DebriefText.PickADate;
            RaiseAll();
            return;
        }

        var matches = RecordsBefore(before);
        if (matches.Count == 0)
        {
            Status = DebriefText.NoRaidsBefore(LocalTime.Date(before));
            RaiseAll();
            return;
        }

        BulkDeletePreviewLabel = DebriefText.BulkDeletePreview(DebriefText.RaidCount(matches.Count), LocalTime.Date(before));
        IsConfirmingBulkDelete = true;
        RaiseAll();
    }

    private void CancelBulkDelete()
    {
        IsConfirmingBulkDelete = false;
        RaiseAll();
    }

    private async Task ConfirmBulkDeleteAsync()
    {
        if (_deleteBeforeDate is not { } before)
        {
            IsConfirmingBulkDelete = false;
            return;
        }

        var matches = RecordsBefore(before);
        if (matches.Count == 0)
        {
            IsConfirmingBulkDelete = false;
            RaiseAll();
            return;
        }

        var ids = matches.Select(record => record.Raid.Id).ToArray();
        await _raidHistoryService.SoftDeleteAsync(ids, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(true);
        _pendingDeleteIds = ids;
        UndoDeleteSummary = DebriefText.BulkDeleted(DebriefText.RaidCount(ids.Length), LocalTime.Date(before));
        IsConfirmingBulkDelete = false;
        if (_selected is not null && ids.Contains(_selected.Id))
        {
            _selected = null;
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private IReadOnlyList<DebriefRaidRecord> RecordsBefore(DateTimeOffset cutoff) =>
        [.. _allRecords.Where(record =>
            record.Raid.StartedUtc is { } started && LocalTime.ToLocal(started).Date < cutoff.Date)];

    private async Task UndoDeleteAsync()
    {
        if (_pendingDeleteIds.Count == 0)
        {
            return;
        }

        await _raidHistoryService.RestoreDeletedAsync(_pendingDeleteIds, CancellationToken.None).ConfigureAwait(true);
        _pendingDeleteIds = [];
        UndoDeleteSummary = string.Empty;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private void WatchOnMap()
    {
        if (_selected is null || _selectedPositions.Count == 0)
        {
            Status = DebriefText.NoScreenshotsToWatch;
            return;
        }

        ReplayRequested?.Invoke(
            this,
            new(
                _selected.MapId,
                $"{SelectedMapLabel} · {SelectedStartedLabel}",
                _selectedPositions,
                _selectedPlannedRoute?.Points ?? [],
                SelectedRouteComparisonLabel));
    }

    /// <summary>Says why a replay could not be opened, in the same status line every other Debrief failure uses.</summary>
    public void ReportReplayFailure(string reason) => Status = DebriefText.ReplayFailed(reason);

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    /// <summary>
    /// Package 17 (home): shows a raid's map by its catalog name ("Customs") rather than the id the
    /// history stores ("customs"); an id the catalog doesn't know is shown as it is.
    /// </summary>
    public void UseMapNames(Func<string, string?> mapName) =>
        _mapName = mapName ?? throw new ArgumentNullException(nameof(mapName));

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            LoadFaultInjection.ThrowIfInjected("debrief");
            await LoadHold.WaitIfHeldAsync("debrief", cancellationToken).ConfigureAwait(true);
            // A raid stays soft-deleted, and so undoable, for exactly as long as the one-press undo
            // that covers it could still be pressed: this purges anything soft-deleted that is not
            // in the batch the undo banner currently names — a previous batch a new delete just
            // superseded, or, on the first load of a session, whatever an earlier session never
            // purged (its own undo cannot be pressed any more either).
            await _raidHistoryService.PurgeDeletedAsync(_pendingDeleteIds, cancellationToken).ConfigureAwait(true);
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            // #453's rule: reading and projecting hundreds of raids — a corrections query and a
            // state-events query each — belongs on the pool, not the dispatcher. Filtering and the
            // per-map stats that follow are then pure in-memory work over the result.
            _allRecords = StampContext(await OffInterfaceThread.Run(
                () => BuildRecordsAsync(raids, cancellationToken), cancellationToken).ConfigureAwait(true));
            RebuildMapFilterOptions();
            RebuildTagFilterOptions();
            ApplyFilters();
            if (_selected is null && Raids.Count > 0)
            {
                // Master-detail: the newest raid is what a debrief is almost always about.
                await SelectRaidAsync(Raids[0].RaidId, cancellationToken).ConfigureAwait(true);
            }

            LoadFault.Clear();
            LoadState = PageLoadStates.Read(_allRecords.Count > 0);
            RaiseAll();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception goes to the log; the page says in words that the read failed, and
            // does not also say the history is empty (#871).
            _allRecords = [];
            Raids = [];
            MapStats = [];
            Status = string.Empty;
            LoadFault.Show(DebriefText.LoadFailed, DebriefText.LoadFailedDetail);
            LoadState = PageLoadState.Failed;
            WorkspaceFault.Record("debrief", "load", exception);
            RaiseAll();
        }
    }

    /// <summary>Every raid plus its side, evidence sources and load time — the read-heavy part of a load.</summary>
    private async Task<IReadOnlyList<DebriefRaidRecord>> BuildRecordsAsync(
        IReadOnlyList<RaidHistoryEntry> raids,
        CancellationToken cancellationToken)
    {
        var records = new List<DebriefRaidRecord>(raids.Count);
        foreach (var raid in raids)
        {
            // A raid with no outcome or notes has nothing a correction could have written, so it
            // needs no events read to say where its outcome came from.
            var sources = RaidFactRules.Classify(
                raid,
                string.IsNullOrWhiteSpace(raid.Outcome)
                    ? []
                    : await LoadCorrectionsAsync(raid.Id, cancellationToken).ConfigureAwait(false));
            var facts = await ReadStateFactsAsync(raid.Id, cancellationToken).ConfigureAwait(false);
            var manual = await _raidHistoryService.GetManualMetadataAsync(raid.Id, cancellationToken).ConfigureAwait(false);
            var tags = DebriefRaidTags.Resolve(await _raidHistoryService
                .ListEventPayloadsAsync(raid.Id, DebriefRaidTags.EventType, cancellationToken)
                .ConfigureAwait(false));
            records.Add(new DebriefRaidRecord(raid, facts.Side, sources, facts.LoadSeconds, manual, tags)
            {
                OfferedExtracts = RaidOfferedExtracts.Union(await _raidHistoryService
                    .ListEventPayloadsAsync(raid.Id, RaidOfferedExtracts.EventType, cancellationToken)
                    .ConfigureAwait(false)),
                UsedExtract = RaidExtractUsed.Latest(await _raidHistoryService
                    .ListEventPayloadsAsync(raid.Id, RaidExtractUsed.EventType, cancellationToken)
                    .ConfigureAwait(false)),
                IsArchived = RaidArchive.IsArchived(await _raidHistoryService
                    .ListEventPayloadsAsync(raid.Id, RaidArchive.EventType, cancellationToken)
                    .ConfigureAwait(false)),
            });
        }

        return records;
    }

    /// <summary>Rebuilds the map filter's choices from the maps this history actually has a raid on.</summary>
    private void RebuildMapFilterOptions()
    {
        var maps = _allRecords
            .Select(record => record.Raid.MapId)
            .Where(mapId => !string.IsNullOrEmpty(mapId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(mapId => new DebriefMapFilterOption(mapId, MapLabel(mapId!)))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase);
        _mapFilterOptions = [new(null, DebriefText.AllMaps), .. maps];
        // [#902 P8] A remembered map this history no longer has would hide every raid unseen.
        if (_mapFilter is not null && !_mapFilterOptions.Any(option =>
                string.Equals(option.MapId, _mapFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _mapFilter = null;
        }

        OnPropertyChanged(nameof(MapFilterOptions));
        OnPropertyChanged(nameof(SelectedMapFilterOption));
    }

    private void RebuildTagFilterOptions()
    {
        var tags = _allRecords
            .SelectMany(record => record.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .Select(tag => new DebriefTagFilterOption(tag, tag));
        _tagFilterOptions = [new(null, DebriefText.AllTags), .. tags];
        if (_tagFilter is not null && !_tagFilterOptions.Any(option =>
                string.Equals(option.Tag, _tagFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _tagFilter = null;
        }

        OnPropertyChanged(nameof(TagFilterOptions));
        OnPropertyChanged(nameof(SelectedTagFilterOption));
    }

    /// <summary>
    /// Applies the current search text and filters to <see cref="_allRecords"/>: the visible list,
    /// and the per-map stats, which follow the same filtered set rather than the whole history.
    /// </summary>
    private void ApplyFilters()
    {
        // An archived raid is out of the list and every total; the archive view lists only those.
        // [#269] ...and a raid from another mode or wipe is out of both until "Show all" is on.
        SavePageState();
        var inContext = ContextRecords.ToArray();
        var filtered = inContext.Where(record => record.IsArchived == _showArchived).Where(MatchesFilters).ToArray();
        var active = _showArchived
            ? inContext.Where(record => !record.IsArchived).Where(MatchesFilters).ToArray()
            : filtered;
        var rows = new List<DebriefRaidRowViewModel>(filtered.Length);
        foreach (var record in filtered)
        {
            var raid = record.Raid;
            rows.Add(new DebriefRaidRowViewModel(
                raid.Id,
                raid.MapId is { } mapId ? MapLabel(mapId) : DebriefText.UnknownMap,
                TarkovCompanion.App.Services.V2.Shell.GameModeLabel.OfStored(raid.Mode),
                LocalTime.Moment(raid.StartedUtc) ?? DebriefText.Unknown,
                LocalTime.Moment(raid.EndedUtc) ?? DebriefText.InProgress,
                Duration(raid),
                raid.Outcome ?? DebriefText.NotRecorded)
            {
                SelectCommand = new AsyncDelegateCommand(() => SelectRaidAsync(raid.Id, CancellationToken.None)),
                IsSelected = _selected?.Id == raid.Id,
                OutcomeKindLabel = DebriefText.Kind(record.Sources.Outcome),
                TagsLabel = string.Join(" · ", record.Tags),
                WipeLabel = record.Wipe ?? string.Empty,
            });
        }

        Raids = rows;
        MapStats = BuildMapStats(active);
        RebuildCoverage(active);
        Status = (_showArchived
            ? DebriefText.ArchivedRaidCount(filtered.Length)
            : BuildStatusLabel(filtered.Length, inContext.Count(record => !record.IsArchived), _allRecords.Count > 0)) + ContextSuffix;
        RaiseAll();
    }

    // [#902 P10] "No raids recorded yet · PvE · Wipe 4" was false while other contexts held raids:
    // the header now says "0 raids" for the context and the list says what is hidden.
    private static string BuildStatusLabel(int shown, int total, bool anyRecorded)
    {
        if (total == 0 && !anyRecorded)
        {
            return DebriefText.NoRaidsYet;
        }

        var totalLabel = DebriefText.RaidCount(total);
        return shown == total ? totalLabel : DebriefText.Of(shown, totalLabel);
    }

    private bool MatchesFilters(DebriefRaidRecord record)
    {
        var raid = record.Raid;
        if (_mapFilter is { } mapFilter && !string.Equals(raid.MapId, mapFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_tagFilter is { } tagFilter && !record.Tags.Contains(tagFilter, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_outcomeFilter != DebriefOutcomeFilter.Any && !OutcomeMatches(raid.Outcome, _outcomeFilter))
        {
            return false;
        }

        if (_sideFilter != DebriefSideFilter.Any && !SideMatches(record.Side, _sideFilter))
        {
            return false;
        }

        if (_dateFrom is { } from
            && (raid.StartedUtc is not { } startedFrom || LocalTime.ToLocal(startedFrom).Date < from.Date))
        {
            return false;
        }

        if (_dateTo is { } to
            && (raid.StartedUtc is not { } startedTo || LocalTime.ToLocal(startedTo).Date > to.Date))
        {
            return false;
        }

        return _searchText.Length == 0
            || (raid.Notes is { Length: > 0 } notes && notes.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The outcome is free text the player typed (see <see cref="DebriefOutcomeFilter"/>), so a
    /// bucket is a keyword match rather than an exact one; text that matches none of them only
    /// shows under "Any outcome".
    /// </summary>
    private static bool OutcomeMatches(string? outcome, DebriefOutcomeFilter filter)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return false;
        }

        return filter switch
        {
            DebriefOutcomeFilter.Survived => outcome.Contains("surviv", StringComparison.OrdinalIgnoreCase),
            DebriefOutcomeFilter.Died => outcome.Contains("die", StringComparison.OrdinalIgnoreCase)
                || outcome.Contains("kill", StringComparison.OrdinalIgnoreCase),
            DebriefOutcomeFilter.Mia => outcome.Contains("mia", StringComparison.OrdinalIgnoreCase)
                || outcome.Contains("missing", StringComparison.OrdinalIgnoreCase),
            DebriefOutcomeFilter.RunThrough => outcome.Contains("run", StringComparison.OrdinalIgnoreCase)
                || outcome.Contains("transit", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool SideMatches(string? side, DebriefSideFilter filter) => filter switch
    {
        DebriefSideFilter.Pmc => string.Equals(side, "PMC", StringComparison.OrdinalIgnoreCase),
        DebriefSideFilter.Scav => string.Equals(side, "scav", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _mapFilter = null;
        _tagFilter = null;
        _outcomeFilter = DebriefOutcomeFilter.Any;
        _sideFilter = DebriefSideFilter.Any;
        _dateFrom = null;
        _dateTo = null;
        ApplyFilters();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(SelectedMapFilterOption));
        OnPropertyChanged(nameof(SelectedTagFilterOption));
        OnPropertyChanged(nameof(DateFrom));
        OnPropertyChanged(nameof(DateTo));
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    /// <summary>Survived / Died / MIA / Run-through, or Any. Public so a render can put the list on one.</summary>
    public DebriefOutcomeFilter OutcomeFilter
    {
        get => _outcomeFilter;
        set
        {
            if (_outcomeFilter == value)
            {
                return;
            }

            _outcomeFilter = value;
            ApplyFilters();
        }
    }

    /// <summary>PMC / Scav, or Any. Public so a render can put the list on one.</summary>
    public DebriefSideFilter SideFilter
    {
        get => _sideFilter;
        set
        {
            if (_sideFilter == value)
            {
                return;
            }

            _sideFilter = value;
            ApplyFilters();
        }
    }

    // Read on each use, not once per process, so the labels follow the chosen culture.
    private static (DebriefOutcomeFilter Filter, string Label)[] OutcomeFilterOptions =>
    [
        (DebriefOutcomeFilter.Any, DebriefText.AnyOutcome),
        (DebriefOutcomeFilter.Survived, DebriefText.Survived),
        (DebriefOutcomeFilter.Died, DebriefText.Died),
        (DebriefOutcomeFilter.Mia, DebriefText.Mia),
        (DebriefOutcomeFilter.RunThrough, DebriefText.RunThrough),
    ];

    private static (DebriefSideFilter Filter, string Label)[] SideFilterOptions =>
    [
        (DebriefSideFilter.Any, DebriefText.AnySide),
        (DebriefSideFilter.Pmc, DebriefText.Pmc),
        (DebriefSideFilter.Scav, DebriefText.Scav),
    ];

    private IReadOnlyList<DebriefFilterChipViewModel> CreateOutcomeChips() =>
        [.. OutcomeFilterOptions.Select(option => new DebriefFilterChipViewModel(
            option.Label,
            $"v2-debrief-filter-outcome-{option.Filter.ToString().ToLowerInvariant()}",
            () => OutcomeFilter = option.Filter)
        {
            IsSelected = option.Filter == _outcomeFilter,
        })];

    private IReadOnlyList<DebriefFilterChipViewModel> CreateSideChips() =>
        [.. SideFilterOptions.Select(option => new DebriefFilterChipViewModel(
            option.Label,
            $"v2-debrief-filter-side-{option.Filter.ToString().ToLowerInvariant()}",
            () => SideFilter = option.Filter)
        {
            IsSelected = option.Filter == _sideFilter,
        })];

    private void SaveView()
    {
        var requested = new DebriefSavedView(
            SavedViewName,
            SearchText,
            _mapFilter,
            _outcomeFilter,
            _sideFilter,
            _dateFrom,
            _dateTo,
            _tagFilter);
        if (!_savedViewStore.Save(requested))
        {
            Status = DebriefText.NameTheView(DebriefSavedViewStore.MaximumNameLength, DebriefSavedViewStore.MaximumViews);
            return;
        }

        SavedViews = _savedViewStore.Load();
        OnPropertyChanged(nameof(SavedViews));
        OnPropertyChanged(nameof(HasSavedViews));
        SelectedSavedView = SavedViews.First(view =>
            string.Equals(view.Name, requested.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        SavedViewName = string.Empty;
        Status = DebriefText.SavedView(SelectedSavedView.Name);
        RaiseAll();
    }

    private void ApplySavedView()
    {
        if (SelectedSavedView is not { } view)
        {
            Status = DebriefText.ChooseSavedView;
            return;
        }

        _searchText = view.SearchText;
        _mapFilter = view.MapId;
        _outcomeFilter = view.Outcome;
        _sideFilter = view.Side;
        _dateFrom = view.DateFrom;
        _dateTo = view.DateTo;
        _tagFilter = view.Tag;
        ApplyFilters();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(SelectedMapFilterOption));
        OnPropertyChanged(nameof(SelectedTagFilterOption));
        OnPropertyChanged(nameof(DateFrom));
        OnPropertyChanged(nameof(DateTo));
        Status = DebriefText.AppliedView(view.Name);
        RaiseAll();
    }

    private void DeleteSavedView()
    {
        if (SelectedSavedView is not { } view || !_savedViewStore.Delete(view.Name))
        {
            return;
        }

        SavedViews = _savedViewStore.Load();
        OnPropertyChanged(nameof(SavedViews));
        OnPropertyChanged(nameof(HasSavedViews));
        SelectedSavedView = null;
        Status = DebriefText.DeletedView(view.Name);
        RaiseAll();
    }

    internal async Task AddTagAsync()
    {
        if (_selected is null || DebriefRaidTags.Normalize(NewTag) is not { } tag)
        {
            Status = DebriefText.TagTooLong(DebriefRaidTags.MaximumTagLength);
            return;
        }

        var current = _allRecords.FirstOrDefault(record => record.Raid.Id == _selected.Id)?.Tags ?? [];
        if (current.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            NewTag = string.Empty;
            return;
        }

        if (current.Count >= DebriefRaidTags.MaximumTagsPerRaid)
        {
            Status = DebriefText.TooManyTags(DebriefRaidTags.MaximumTagsPerRaid);
            return;
        }

        await ChangeTagAsync(tag, present: true).ConfigureAwait(true);
        NewTag = string.Empty;
    }

    internal Task RemoveTagAsync(string tag) => ChangeTagAsync(tag, present: false);

    private async Task ChangeTagAsync(string tag, bool present)
    {
        if (_selected is null)
        {
            return;
        }

        var raidId = _selected.Id;
        await _raidHistoryService.RecordEventAsync(
            raidId,
            DebriefRaidTags.EventType,
            _clock.GetUtcNow(),
            DebriefRaidTags.ToPayload(tag, present),
            CancellationToken.None).ConfigureAwait(true);
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        await SelectRaidAsync(raidId, CancellationToken.None).ConfigureAwait(true);
        Status = present ? DebriefText.AddedTag(tag) : DebriefText.RemovedTag(tag);
    }

    public async Task SelectRaidAsync(Guid raidId, CancellationToken cancellationToken)
    {
        // A delete preview names one raid; selecting another while it is showing must not leave a
        // stale preview whose Confirm button would act on the raid now selected instead.
        IsConfirmingDelete = false;
        var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
        _selected = raids.FirstOrDefault(raid => raid.Id == raidId);
        _selectedPositions = _selected is null
            ? []
            : await _raidHistoryService.ListPositionsAsync(raidId, cancellationToken).ConfigureAwait(true);
        CorrectedOutcome = _selected?.Outcome ?? string.Empty;
        CorrectedNotes = _selected?.Notes ?? string.Empty;
        if (_selected is null)
        {
            SelectedSales = [];
            SelectedQuestEvents = [];
            SelectedScans = [];
            SelectedScanSummary = DebriefText.NoScans;
            SelectedLoadTimeLabel = DebriefText.LoadNotRecorded;
            _selectedLoadRecorded = false;
            _selectedSources = RaidFactRules.Classify(
                new RaidHistoryEntry(Guid.Empty, Guid.Empty, null, string.Empty, null, null, null, null),
                []);
            SelectedTags = [];
            _selectedPlannedRoute = null;
            SetManualFields(null);
        }
        else
        {
            var selectedRecord = _allRecords.FirstOrDefault(record => record.Raid.Id == raidId);
            SelectedTags = selectedRecord is null
                ? []
                : [.. selectedRecord.Tags.Select(tag => new DebriefTagViewModel(
                    tag,
                    new AsyncDelegateCommand(() => RemoveTagAsync(tag))))];
            _selectedPlannedRoute = RaidPlannedRoute.Latest(await _raidHistoryService
                .ListEventPayloadsAsync(raidId, RaidPlannedRoute.EventType, cancellationToken)
                .ConfigureAwait(true));
            if (!string.Equals(_selectedPlannedRoute?.MapId, _selected.MapId, StringComparison.OrdinalIgnoreCase))
            {
                _selectedPlannedRoute = null;
            }
            SelectedSales = await LoadSalesAsync(raidId, cancellationToken).ConfigureAwait(true);
            SelectedQuestEvents = await LoadQuestEventsAsync(raidId, cancellationToken).ConfigureAwait(true);
            var stateFacts = await ReadStateFactsAsync(raidId, cancellationToken).ConfigureAwait(true);
            _selectedLoadRecorded = stateFacts.LoadSeconds is not null;
            SelectedLoadTimeLabel = stateFacts.LoadSeconds is { } loadSeconds
                ? DebriefText.QueueLoadSeconds(loadSeconds)
                : DebriefText.LoadNotRecorded;
            _selectedSources = RaidFactRules.Classify(
                _selected,
                await LoadCorrectionsAsync(raidId, cancellationToken).ConfigureAwait(true));
            await LoadScansAsync(raidId, cancellationToken).ConfigureAwait(true);
            SetManualFields(await _raidHistoryService.GetManualMetadataAsync(raidId, cancellationToken).ConfigureAwait(true));
        }

        SelectedFacts = BuildFacts();
        RebuildExtractChoices();

        Raids = Raids.Select(row => row with { IsSelected = row.RaidId == raidId }).ToArray();
        await RefreshAfterRaidRecapAsync(cancellationToken).ConfigureAwait(true);
        await RefreshPersonalPatternAsync(cancellationToken).ConfigureAwait(true);
        RaiseAll();
    }

    private async Task<IReadOnlyList<RaidCorrection>> LoadCorrectionsAsync(Guid raidId, CancellationToken cancellationToken) =>
        RaidCorrection.ParseAll(
            await _raidHistoryService
                .ListEventPayloadsAsync(raidId, RaidCorrection.EventType, cancellationToken)
                .ConfigureAwait(true));

    /// <summary>
    /// The facts the detail panel shows, each with the kind of evidence behind it: read from the log,
    /// worked out by the companion, a bound rather than a reading, or typed by the player.
    /// </summary>
    private IReadOnlyList<DebriefFactRowViewModel> BuildFacts()
    {
        if (_selected is null)
        {
            return [];
        }

        var facts = new List<DebriefFactRowViewModel>
        {
            new(DebriefText.Mode, SelectedModeLabel, DebriefText.Kind(_selectedSources.Mode)),
            new(DebriefText.Wipe, SelectedWipeLabel, SelectedRecord?.Wipe is null ? string.Empty : DebriefText.Kind(RaidFactKind.Inferred)),
            new(DebriefText.Started, SelectedStartedLabel, DebriefText.Kind(_selectedSources.Started)),
            new(DebriefText.Ended, SelectedEndedLabel, DebriefText.Kind(_selectedSources.Ended)),
            new(DebriefText.Duration, SelectedDurationLabel, DebriefText.Kind(_selectedSources.Duration)),
            new(DebriefText.QueueLoad, SelectedLoadTimeLabel, _selectedLoadRecorded ? DebriefText.Kind(RaidFactKind.Observed) : string.Empty),
            new(DebriefText.Outcome, SelectedOutcomeLabel, DebriefText.Kind(_selectedSources.Outcome)),
        };
        if (SelectedNotesLabel.Length > 0)
        {
            facts.Add(new(DebriefText.Notes, SelectedNotesLabel, DebriefText.Kind(_selectedSources.Notes)));
        }

        // Manual fields (#291 package 4): a player-entered zero is a real answer ("no PMC kills")
        // and is shown; only an unentered field is left out.
        if (ManualPmcKills is not null)
        {
            facts.Add(new(DebriefText.PmcKills, ManualPmcKills.Value.ToString(CultureInfo.CurrentCulture), DebriefText.Kind(RaidFactKind.Manual)));
        }

        if (ManualScavKills is not null)
        {
            facts.Add(new(DebriefText.ScavKills, ManualScavKills.Value.ToString(CultureInfo.CurrentCulture), DebriefText.Kind(RaidFactKind.Manual)));
        }

        if (ManualBossKills is not null)
        {
            facts.Add(new(DebriefText.BossKills, ManualBossKills.Value.ToString(CultureInfo.CurrentCulture), DebriefText.Kind(RaidFactKind.Manual)));
        }

        if (ManualValueRoubles is not null)
        {
            facts.Add(new(
                DebriefText.ValueBroughtOut,
                DebriefText.Roubles(ManualValueRoubles.Value),
                DebriefText.Kind(RaidFactKind.Manual)));
        }

        return facts;
    }

    /// <summary>What was scanned while this raid was open, and how much of it named an item.</summary>
    private async Task LoadScansAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var events = await _raidHistoryService
            .ListEventsAsync(raidId, "scan", cancellationToken)
            .ConfigureAwait(true);
        var scans = events
            .Select(stored => RaidScanFact.TryParse(stored.Id, stored.PayloadJson))
            .OfType<RaidScanFact>()
            .OrderBy(scan => scan.ObservedUtc)
            .ToArray();
        var wrongScanIds = RaidScanCorrection.WrongScanIds(await _raidHistoryService
            .ListEventPayloadsAsync(raidId, RaidScanCorrection.EventType, cancellationToken)
            .ConfigureAwait(true));
        var lootScans = _lootScans is null
            ? []
            : await _lootScans.ListForRaidAsync(raidId, cancellationToken).ConfigureAwait(true);
        var rows = new List<(DateTimeOffset At, DebriefScanRowViewModel Row)>(scans.Length + lootScans.Count);
        foreach (var scan in scans)
        {
            var isWrong = wrongScanIds.Contains(scan.Id);
            var itemName = scan.Recognised
                ? scan.ItemName ?? (scan.ItemId is null ? DebriefText.Item : await ResolveItemNameAsync(scan.ItemId, cancellationToken).ConfigureAwait(true))
                : scan.IsAvailable ? DebriefText.NothingRecognised : DebriefText.ScanUnavailable;
            var detail = new List<string>();
            if (scan.Recognised && scan.Confidence is { } confidence)
            {
                detail.Add(DebriefText.PercentSure(confidence));
            }

            if (scan.Recommendation is { Length: > 0 } recommendation)
            {
                detail.Add(recommendation);
            }

            rows.Add((scan.ObservedUtc, new(
                LocalTime.ShortTime(scan.ObservedUtc),
                itemName,
                DebriefText.Kind(scan.IdentityKind),
                scan.ValueRoubles is { } roubles
                    ? DebriefText.ApproxRoubles(roubles)
                    : string.Empty,
                DebriefText.Kind(scan.ValueKind),
                string.Join(" · ", detail))
            {
                ScanId = scan.Id,
                ItemId = scan.Recognised ? scan.ItemId ?? string.Empty : string.Empty,
                IsWrong = isWrong,
                CorrectionCommand = new AsyncDelegateCommand(() => CorrectScanAsync(scan.Id, !isWrong)),
            }));
        }

        foreach (var loot in lootScans)
        {
            rows.Add((loot.EvaluatedUtc, LootScanRow(loot, wrongScanIds.Contains(loot.CorrectionId))));
        }

        SelectedScans = rows.OrderBy(row => row.At).Select(row => row.Row).ToArray();
        var counted = scans.Where(scan => !wrongScanIds.Contains(scan.Id)).ToArray();
        var countedLoot = lootScans.Where(scan => !wrongScanIds.Contains(scan.CorrectionId)).ToArray();
        var wrongCount = scans.Length - counted.Length + lootScans.Count - countedLoot.Length;
        var recognised = counted.Count(scan => scan.Recognised) + countedLoot.Count(scan => scan.Items.Any(item => item.ItemId is not null));
        var unavailable = counted.Count(scan => !scan.IsAvailable);
        NoteScanTotals(
            counted.Length + countedLoot.Length,
            counted.Sum(scan => scan.ValueRoubles ?? 0) + countedLoot.Sum(scan => scan.TakenValueRoubles));
        SelectedScanSummary = scans.Length + lootScans.Count == 0
            ? DebriefText.NoScans
            : string.Join(" · ", new[]
            {
                DebriefText.ScanCount(counted.Length + countedLoot.Length),
                DebriefText.Recognised(recognised),
                unavailable > 0 ? DebriefText.UnavailableCount(unavailable) : null,
                wrongCount > 0 ? DebriefText.MarkedWrongCount(wrongCount) : null,
            }.OfType<string>());
    }

    /// <summary>
    /// A saved Loot Scan (#274): its calls, its value as an estimate, and the rules that made it.
    /// "Wrong" appends the same correction a screenshot scan gets, under the scan's own id.
    /// </summary>
    private DebriefScanRowViewModel LootScanRow(SavedLootScan loot, bool isWrong)
    {
        var culture = CultureInfo.CurrentCulture;
        var taken = loot.TakenValueRoubles;
        return new DebriefScanRowViewModel(
            LocalTime.ShortTime(loot.EvaluatedUtc),
            LootScanHistoryViewModel.Summary(loot, culture),
            DebriefText.Kind(RaidFactKind.Inferred),
            taken > 0 ? DebriefText.RoublesTaken(taken) : string.Empty,
            DebriefText.Kind(RaidFactKind.Estimated),
            DebriefText.LootScan(LootScanHistoryViewModel.RulesLabel(loot)) + (loot.IsComplete ? string.Empty : " · " + DebriefText.NeededReview))
        {
            ScanId = loot.CorrectionId,
            IsWrong = isWrong,
            CorrectionCommand = new AsyncDelegateCommand(() => CorrectScanAsync(loot.CorrectionId, !isWrong)),
            VerdictLines = loot.Items
                .Where(item => item.Verdict != LootScanVerdict.Leave)
                .Concat(loot.Items.Where(item => item.Verdict == LootScanVerdict.Leave))
                .Take(MaximumVerdictLines)
                .Select(item => item.ValueRoubles is { } value
                    ? $"{item.Verdict.ToString().ToUpperInvariant()} {item.Name} · {LootScanDecisionViewModel.CompactRoubles(value, culture)}"
                    : $"{item.Verdict.ToString().ToUpperInvariant()} {item.Name}")
                .Concat(loot.Items.Count > MaximumVerdictLines
                    ? [DebriefText.MoreVerdicts(loot.Items.Count - MaximumVerdictLines)]
                    : [])
                .ToArray(),
        };
    }

    private const int MaximumVerdictLines = 6;

    internal async Task CorrectScanAsync(string scanId, bool isWrong)
    {
        if (_selected is null)
        {
            return;
        }

        var correctedUtc = _clock.GetUtcNow().ToUniversalTime();
        await _raidHistoryService.RecordEventAsync(
            _selected.Id,
            RaidScanCorrection.EventType,
            correctedUtc,
            new RaidScanCorrection(scanId, isWrong, correctedUtc).ToPayload(),
            CancellationToken.None).ConfigureAwait(true);
        await LoadScansAsync(_selected.Id, CancellationToken.None).ConfigureAwait(true);
        Status = isWrong ? DebriefText.ScanMarkedWrong : DebriefText.ScanRestored;
        RaiseAll();
    }

    /// <summary>What sold on the flea while this raid was open, counted per item.</summary>
    private async Task<IReadOnlyList<DebriefSaleRowViewModel>> LoadSalesAsync(
        Guid raidId,
        CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService
            .ListEventPayloadsAsync(raidId, "sale", cancellationToken)
            .ConfigureAwait(true);
        if (payloads.Count == 0)
        {
            return [];
        }

        var byItem = new Dictionary<string, (int Count, DateTimeOffset Latest)>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (ReadPayload<FleaSaleObservation>(payload) is not { } sale)
            {
                continue;
            }

            var key = sale.HandbookItemId ?? string.Empty;
            var quantity = Math.Max(1, sale.Count);
            byItem[key] = byItem.TryGetValue(key, out var known)
                ? (known.Count + quantity, sale.ObservedUtc > known.Latest ? sale.ObservedUtc : known.Latest)
                : (quantity, sale.ObservedUtc);
        }

        var rows = new List<DebriefSaleRowViewModel>(byItem.Count);
        foreach (var (itemId, info) in byItem)
        {
            var name = itemId.Length == 0
                ? DebriefText.ItemNotInCatalog
                : await ResolveItemNameAsync(itemId, cancellationToken).ConfigureAwait(true);
            rows.Add(new(
                name,
                DebriefText.Sold(info.Count),
                LocalTime.ShortTime(info.Latest)));
        }

        return rows.OrderBy(row => row.ItemLabel, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>What the game announced starting, failing or finishing while this raid was open.</summary>
    private async Task<IReadOnlyList<DebriefQuestEventRowViewModel>> LoadQuestEventsAsync(
        Guid raidId,
        CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService
            .ListEventPayloadsAsync(raidId, "quest", cancellationToken)
            .ConfigureAwait(true);
        if (payloads.Count == 0)
        {
            return [];
        }

        var rows = new List<DebriefQuestEventRowViewModel>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (ReadPayload<QuestStatusObservation>(payload) is not { } quest)
            {
                continue;
            }

            var name = await ResolveTaskNameAsync(quest.TaskId, cancellationToken).ConfigureAwait(true);
            rows.Add(new(
                name,
                DescribeTaskState(quest.State),
                LocalTime.ShortTime(quest.ObservedUtc)));
        }

        return rows;
    }

    /// <summary>
    /// Raids, durations and — where seen — load times, grouped by map, over whichever records
    /// passed the current filters. Never survival: the game records no outcome, so this does not
    /// compute one from a field the player fills in by hand.
    /// </summary>
    private IReadOnlyList<DebriefMapStatRowViewModel> BuildMapStats(IReadOnlyList<DebriefRaidRecord> records)
    {
        var byMap = records
            .Where(record => record.Raid.MapId is { Length: > 0 })
            .GroupBy(record => record.Raid.MapId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count());

        var rows = new List<DebriefMapStatRowViewModel>();
        foreach (var group in byMap)
        {
            var raidsOnMap = group.ToArray();
            var durations = raidsOnMap
                .Where(record => record.Raid.StartedUtc is not null
                    && record.Raid.EndedUtc is { } end && end > record.Raid.StartedUtc)
                .Select(record => record.Raid.EndedUtc!.Value - record.Raid.StartedUtc!.Value)
                .ToArray();

            var loadTimes = raidsOnMap
                .Where(record => record.LoadSeconds is not null)
                .Select(record => record.LoadSeconds!.Value)
                .ToList();

            var manualLabel = BuildManualStatsLabel(raidsOnMap);
            rows.Add(new(
                MapLabel(group.Key),
                DebriefText.RaidCount(raidsOnMap.Length),
                durations.Length == 0
                    ? DebriefText.DurationUnknown
                    : DebriefText.Average(FormatDuration(AverageTicks(durations))),
                loadTimes.Count == 0
                    ? DebriefText.LoadTimeUnknown
                    : DebriefText.AverageLoad(loadTimes.Average(), DebriefText.RaidCount(loadTimes.Count)),
                manualLabel,
                manualLabel.Length == 0 ? string.Empty : DebriefText.Kind(RaidFactKind.Manual)));
        }

        return rows;
    }

    /// <summary>
    /// Kills and the value brought out, summed across whichever raids on this map have each field
    /// entered — a field nobody entered on any raid here is left out rather than shown as zero.
    /// </summary>
    private static string BuildManualStatsLabel(IReadOnlyList<DebriefRaidRecord> raidsOnMap)
    {
        var manual = raidsOnMap.Where(record => record.Manual is not null).Select(record => record.Manual!).ToArray();
        if (manual.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (manual.Any(entry => entry.PmcKills is not null))
        {
            parts.Add(DebriefText.ManualPmc(manual.Sum(entry => entry.PmcKills ?? 0)));
        }

        if (manual.Any(entry => entry.ScavKills is not null))
        {
            parts.Add(DebriefText.ManualScav(manual.Sum(entry => entry.ScavKills ?? 0)));
        }

        if (manual.Any(entry => entry.BossKills is not null))
        {
            parts.Add(DebriefText.ManualBoss(manual.Sum(entry => entry.BossKills ?? 0)));
        }

        if (manual.Any(entry => entry.ValueRoubles is not null))
        {
            parts.Add(DebriefText.Roubles(manual.Sum(entry => entry.ValueRoubles ?? 0)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What a raid's "state" events carry beyond the lifecycle: queue/load time and which
    /// side it was played on, both written once by the coordinator that observed them.</summary>
    private readonly record struct RaidStateFacts(double? LoadSeconds, string? Side);

    /// <summary>
    /// The queue/load time and the side are both carried on the raid's state events, so both are
    /// read from there in one pass rather than one query per fact.
    /// </summary>
    private async Task<RaidStateFacts> ReadStateFactsAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService
            .ListEventPayloadsAsync(raidId, "state", cancellationToken)
            .ConfigureAwait(true);
        double? loadSeconds = null;
        string? side = null;
        foreach (var payload in payloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (loadSeconds is null
                    && document.RootElement.TryGetProperty("LoadSeconds", out var load)
                    && load.ValueKind == JsonValueKind.Number
                    && load.TryGetDouble(out var seconds)
                    && seconds > 0)
                {
                    loadSeconds = seconds;
                }

                if (document.RootElement.TryGetProperty("Side", out var sideElement)
                    && sideElement.ValueKind == JsonValueKind.String
                    && sideElement.GetString() is { Length: > 0 } sideValue)
                {
                    // The newest state event wins: side is inferred once the profile is known and
                    // does not change mid-raid, but the newest reading is still the best guess.
                    side = sideValue;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new RaidStateFacts(loadSeconds, side);
    }

    private async Task<string> ResolveItemNameAsync(string itemId, CancellationToken cancellationToken)
    {
        if (_itemNames.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        if (_items is null)
        {
            return itemId;
        }

        try
        {
            var item = await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
            var name = item?.Name ?? itemId;
            _itemNames[itemId] = name;
            return name;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return itemId;
        }
    }

    private async Task<string> ResolveTaskNameAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!_taskNames.TryGetValue(taskId, out var cached))
        {
            await EnsureTaskCatalogAsync(cancellationToken).ConfigureAwait(true);
            cached = _taskNames.GetValueOrDefault(taskId, taskId);
        }

        return cached;
    }

    /// <summary>
    /// Loaded once per view model lifetime rather than per lookup, and never refreshed: a
    /// catalog sync mid-session renaming a quest is not worth a second read for what is already
    /// a cosmetic lookup with the id as a safe fallback.
    /// </summary>
    private async Task EnsureTaskCatalogAsync(CancellationToken cancellationToken)
    {
        if (_taskCatalogLoaded || _questCatalog is null || _profileService is null)
        {
            return;
        }

        _taskCatalogLoaded = true;
        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            // Debrief reads the same translated catalog as the rest of quest tracking. A literal
            // English request made past raid events switch language when every live quest view did.
            var language = _questOptions?.NormalizedLanguage ?? "en";
            var catalog = await _questCatalog.GetAsync(profile.GameMode, language, cancellationToken).ConfigureAwait(true);
            if (catalog is null)
            {
                return;
            }

            foreach (var task in catalog.Tasks)
            {
                _taskNames[task.Id] = task.Name;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The task id remains a usable, if less friendly, label.
        }
    }

    private static string DescribeTaskState(RecordedTaskState state) => state switch
    {
        RecordedTaskState.Completed => DebriefText.HandedIn,
        RecordedTaskState.Failed => DebriefText.Failed,
        RecordedTaskState.Active => DebriefText.Started,
        _ => state.ToString(),
    };

    private static TimeSpan AverageTicks(IReadOnlyList<TimeSpan> durations) =>
        TimeSpan.FromTicks((long)durations.Average(duration => duration.Ticks));

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\h\ mm\m" : @"mm\m\ ss\s", CultureInfo.InvariantCulture);

    /// <summary>Reads one stored payload, or nothing rather than failing the whole workspace.</summary>
    private static T? ReadPayload<T>(string payload)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveCorrectionAsync()
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            await _raidHistoryService.CorrectAsync(
                _selected.Id,
                string.IsNullOrWhiteSpace(CorrectedOutcome) ? null : CorrectedOutcome.Trim(),
                string.IsNullOrWhiteSpace(CorrectedNotes) ? null : CorrectedNotes.Trim(),
                CancellationToken.None).ConfigureAwait(true);
            Status = DebriefText.CorrectionSaved;
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = DebriefText.CorrectionFailed(exception.Message);
        }
    }

    private Task ExportCsvAsync() => ExportAsync("raid-history.csv", _raidHistoryService.ExportCsvAsync);

    private Task ExportJsonAsync() => ExportAsync("raid-history.json", _raidHistoryService.ExportJsonAsync);

    private async Task ExportAsync(string fileName, Func<Stream, CancellationToken, Task> write)
    {
        try
        {
            var directory = Path.Combine(_paths.Root, "Exports");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(
                directory,
                $"{LocalTime.FileStamp(_clock.GetUtcNow())}-{fileName}");
            await using (var stream = File.Create(destination))
            {
                await write(stream, CancellationToken.None).ConfigureAwait(true);
            }

            Status = DebriefText.ExportedTo(destination);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = DebriefText.ExportFailed(exception.Message);
        }
    }

    private string MapLabel(string mapId) => _mapName(mapId) is { Length: > 0 } name ? name : mapId;

    private static string Duration(RaidHistoryEntry? raid)
    {
        if (raid?.StartedUtc is not { } started)
        {
            return DebriefText.Unknown;
        }

        if (raid.EndedUtc is null)
        {
            return DebriefText.InProgress;
        }

        var elapsed = raid.EndedUtc.Value - started;
        return elapsed <= TimeSpan.Zero ? DebriefText.Unknown : FormatDuration(elapsed);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Raids));
        OnPropertyChanged(nameof(HasRaids));
        OnPropertyChanged(nameof(HasNoRaids));
        OnPropertyChanged(nameof(ShowsNoRaids));
        OnPropertyChanged(nameof(ShowsEmptyReset));
        OnPropertyChanged(nameof(EmptyResetLabel));
        OnPropertyChanged(nameof(NoRaidsMessage));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(SelectedStartedLabel));
        OnPropertyChanged(nameof(SelectedMapLabel));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(SelectedDurationLabel));
        OnPropertyChanged(nameof(SelectedEndedLabel));
        OnPropertyChanged(nameof(SelectedDistanceLabel));
        OnPropertyChanged(nameof(HasDistance));
        OnPropertyChanged(nameof(CanWatch));
        OnPropertyChanged(nameof(SelectedOutcomeLabel));
        OnPropertyChanged(nameof(SelectedNotesLabel));
        OnPropertyChanged(nameof(SelectedPathLabel));
        OnPropertyChanged(nameof(SelectedRouteComparisonLabel));
        OnPropertyChanged(nameof(HasSelectedRouteComparison));
        OnPropertyChanged(nameof(SelectedLoadTimeLabel));
        OnPropertyChanged(nameof(SelectedSales));
        OnPropertyChanged(nameof(HasSelectedSales));
        OnPropertyChanged(nameof(SelectedQuestEvents));
        OnPropertyChanged(nameof(HasSelectedQuestEvents));
        OnPropertyChanged(nameof(MapStats));
        OnPropertyChanged(nameof(HasMapStats));
        OnPropertyChanged(nameof(SelectedMapKindLabel));
        OnPropertyChanged(nameof(HasSelectedMapKind));
        OnPropertyChanged(nameof(SelectedDistanceKindLabel));
        OnPropertyChanged(nameof(SelectedFacts));
        OnPropertyChanged(nameof(SelectedScans));
        OnPropertyChanged(nameof(HasSelectedScans));
        OnPropertyChanged(nameof(SelectedScanSummary));
        OnPropertyChanged(nameof(SelectedTags));
        OnPropertyChanged(nameof(HasSelectedTags));
        OnPropertyChanged(nameof(NewTag));
        OnPropertyChanged(nameof(TagFilterOptions));
        OnPropertyChanged(nameof(SelectedTagFilterOption));
        OnPropertyChanged(nameof(SavedViews));
        OnPropertyChanged(nameof(HasSavedViews));
        OnPropertyChanged(nameof(SelectedSavedView));
        OnPropertyChanged(nameof(SavedViewName));
        OnPropertyChanged(nameof(OutcomeFilterChips));
        OnPropertyChanged(nameof(SideFilterChips));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(IsConfirmingDelete));
        OnPropertyChanged(nameof(DeletePreviewLabel));
        OnPropertyChanged(nameof(IsConfirmingBulkDelete));
        OnPropertyChanged(nameof(BulkDeletePreviewLabel));
        OnPropertyChanged(nameof(CanUndoDelete));
        OnPropertyChanged(nameof(UndoDeleteSummary));
        OnPropertyChanged(nameof(ManualPmcKills));
        OnPropertyChanged(nameof(ManualScavKills));
        OnPropertyChanged(nameof(ManualBossKills));
        OnPropertyChanged(nameof(ManualValueRoubles));
    }
}
