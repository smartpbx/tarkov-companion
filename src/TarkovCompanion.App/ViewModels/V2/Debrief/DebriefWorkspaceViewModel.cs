using TarkovCompanion.App.Services.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Common;

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
}

/// <summary>A raid's trail, handed to the shell to draw on the Raid map (V1's "Watch it").</summary>
/// <remarks>
/// Carries the raid's own map because a replay is only positions: V1 drew them on whatever map was
/// showing, so the shell chooses the map first.
/// </remarks>
public sealed record DebriefReplayRequest(string? MapId, string Title, IReadOnlyList<ScreenshotPosition> Positions);

/// <summary>One flea offer that sold during a raid, counted per item rather than per offer.</summary>
public sealed record DebriefSaleRowViewModel(string ItemLabel, string CountLabel, string TimeLabel);

/// <summary>One quest the game announced during a raid.</summary>
public sealed record DebriefQuestEventRowViewModel(string QuestLabel, string StateLabel, string TimeLabel);

/// <summary>What the companion has measured on one map: how often it's played and how it goes.</summary>
public sealed record DebriefMapStatRowViewModel(string MapLabel, string RaidsLabel, string DurationLabel, string LoadLabel);

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
public sealed class DebriefWorkspaceViewModel : BindableViewModel
{
    private readonly IRaidHistoryService _raidHistoryService;
    private readonly AppDataPaths _paths;
    private readonly TimeProvider _clock;
    // Optional: naming what sold and which quest fired is a readability improvement over the
    // raw ids the history stores, not something the workspace needs to function without.
    private readonly IItemRepository? _items;
    private readonly IQuestCatalog? _questCatalog;
    private readonly IPlayerProfileService? _profileService;
    private readonly Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _taskNames = new(StringComparer.Ordinal);
    private bool _taskCatalogLoaded;
    private RaidHistoryEntry? _selected;
    private IReadOnlyList<ScreenshotPosition> _selectedPositions = [];
    private RaidFactSources _selectedSources = new(
        RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown, RaidFactKind.Unknown);
    private bool _selectedLoadRecorded;
    private string _status = "Raid history has not been loaded.";
    private string _correctedOutcome = string.Empty;
    private string _correctedNotes = string.Empty;
    private Func<string, string?> _mapName = _ => null;

    // Search and filters (#291 package 2). Every raid's side, evidence sources and load time are
    // read once per load, off the interface thread; filtering and the per-map stats that follow it
    // are then pure in-memory work, so typing in the search box costs no I/O.
    private IReadOnlyList<DebriefRaidRecord> _allRecords = [];
    private string _searchText = string.Empty;
    private string? _mapFilter;
    private DebriefOutcomeFilter _outcomeFilter = DebriefOutcomeFilter.Any;
    private DebriefSideFilter _sideFilter = DebriefSideFilter.Any;
    private DateTimeOffset? _dateFrom;
    private DateTimeOffset? _dateTo;
    private IReadOnlyList<DebriefMapFilterOption> _mapFilterOptions = [new(null, "All maps")];
    private ICommand? _clearSearch;
    private ICommand? _clearFilters;

    /// <summary>One raid plus what the workspace already knows about it, built once per load.</summary>
    private sealed record DebriefRaidRecord(RaidHistoryEntry Raid, string? Side, RaidFactSources Sources, double? LoadSeconds);

    public DebriefWorkspaceViewModel(
        IRaidHistoryService raidHistoryService,
        AppDataPaths paths,
        TimeProvider? clock = null,
        IItemRepository? items = null,
        IQuestCatalog? questCatalog = null,
        IPlayerProfileService? profileService = null)
    {
        _raidHistoryService = raidHistoryService ?? throw new ArgumentNullException(nameof(raidHistoryService));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? TimeProvider.System;
        _items = items;
        _questCatalog = questCatalog;
        _profileService = profileService;

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        SaveCorrectionCommand = new AsyncDelegateCommand(SaveCorrectionAsync);
        ExportCsvCommand = new AsyncDelegateCommand(ExportCsvAsync);
        ExportJsonCommand = new AsyncDelegateCommand(ExportJsonAsync);
        WatchOnMapCommand = new DelegateCommand(WatchOnMap);
    }

    public IReadOnlyList<DebriefRaidRowViewModel> Raids { get; private set; } = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasRaids => Raids.Count > 0;

    public bool HasNoRaids => !HasRaids;

    /// <summary>Distinguishes an empty history from a filter that matched nothing in it.</summary>
    public string NoRaidsMessage => _allRecords.Count == 0
        ? "No raids recorded yet."
        : "No raids match these filters.";

    public bool HasSelection => _selected is not null;

    public bool HasNoSelection => !HasSelection;

    public string SelectedMapLabel => _selected?.MapId is { } mapId ? MapLabel(mapId) : string.Empty;

    public string SelectedStartedLabel => LocalTime.Moment(_selected?.StartedUtc) ?? "Unknown";

    /// <summary>Package 17 (home): said once, beside the field it explains, instead of in the status line.</summary>
    public string OutcomeHint { get; } = "The game doesn't record outcomes; enter one by hand.";

    public string SelectedModeLabel => _selected?.Mode ?? string.Empty;

    public string SelectedDurationLabel => Duration(_selected);

    /// <summary>Package 29 (parity): V1's "Ended" column, which the list's Duration column only implied.</summary>
    public string SelectedEndedLabel => LocalTime.Moment(_selected?.EndedUtc) ?? "In progress";

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
                ? string.Create(CultureInfo.CurrentCulture, $"At least {metres / 1000:F1} km")
                : string.Create(CultureInfo.CurrentCulture, $"At least {metres:F0} m");
        }
    }

    public bool HasDistance => SelectedDistanceLabel.Length > 0;

    /// <summary>Whether the selected raid has screenshots to watch back — the same test V1 used to show "Watch it".</summary>
    public bool CanWatch => _selectedPositions.Count > 0;

    public string SelectedOutcomeLabel => _selected?.Outcome ?? "Not recorded";

    public string SelectedNotesLabel => _selected?.Notes ?? string.Empty;

    public string SelectedPathLabel => _selectedPositions.Count switch
    {
        0 => "No screenshots recorded for this raid.",
        1 => "1 screenshot recorded.",
        var count => $"{count.ToString(CultureInfo.CurrentCulture)} screenshots recorded.",
    };

    /// <summary>Where the map came from, beside the raid's name in the detail heading.</summary>
    public string SelectedMapKindLabel => _selectedSources.Map.Label();

    public bool HasSelectedMapKind => SelectedMapKindLabel.Length > 0;

    /// <summary>The distance is a floor built from straight lines between screenshots, so it is an estimate.</summary>
    public string SelectedDistanceKindLabel => HasDistance ? RaidFactKind.Estimated.Label() : string.Empty;

    /// <summary>The detail facts of the selected raid, each with the kind of evidence behind it.</summary>
    public IReadOnlyList<DebriefFactRowViewModel> SelectedFacts { get; private set; } = [];

    /// <summary>What was scanned during the selected raid.</summary>
    public IReadOnlyList<DebriefScanRowViewModel> SelectedScans { get; private set; } = [];

    public bool HasSelectedScans => SelectedScans.Count > 0;

    /// <summary>"3 scans · 2 recognised": how many were taken and how many named an item, unavailable ones counted apart.</summary>
    public string SelectedScanSummary { get; private set; } = "No scans during this raid.";

    /// <summary>How long matchmaking and loading took before this raid began, if it was seen.</summary>
    public string SelectedLoadTimeLabel { get; private set; } = "Load time not recorded.";

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
        || _outcomeFilter != DebriefOutcomeFilter.Any
        || _sideFilter != DebriefSideFilter.Any
        || _dateFrom is not null
        || _dateTo is not null
        || HasSearchText;

    public ICommand ClearFiltersCommand => _clearFilters ??= new DelegateCommand(ClearFilters);

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

    public ICommand RefreshCommand { get; }

    public ICommand SaveCorrectionCommand { get; }

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportJsonCommand { get; }

    /// <summary>Draws the selected raid's trail on the Raid map.</summary>
    public ICommand WatchOnMapCommand { get; }

    /// <summary>Raised when somebody asks to watch the selected raid; the shell owns the map and the router.</summary>
    public event EventHandler<DebriefReplayRequest>? ReplayRequested;

    private void WatchOnMap()
    {
        if (_selected is null || _selectedPositions.Count == 0)
        {
            Status = "That raid has no screenshots to watch.";
            return;
        }

        ReplayRequested?.Invoke(
            this,
            new(_selected.MapId, $"{SelectedMapLabel} · {SelectedStartedLabel}", _selectedPositions));
    }

    /// <summary>Says why a replay could not be opened, in the same status line every other Debrief failure uses.</summary>
    public void ReportReplayFailure(string reason) => Status = $"That raid could not be opened on the map: {reason}";

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
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            // #453's rule: reading and projecting hundreds of raids — a corrections query and a
            // state-events query each — belongs on the pool, not the dispatcher. Filtering and the
            // per-map stats that follow are then pure in-memory work over the result.
            _allRecords = await OffInterfaceThread.Run(
                () => BuildRecordsAsync(raids, cancellationToken), cancellationToken).ConfigureAwait(true);
            RebuildMapFilterOptions();
            ApplyFilters();
            if (_selected is null && Raids.Count > 0)
            {
                // Master-detail: the newest raid is what a debrief is almost always about.
                await SelectRaidAsync(Raids[0].RaidId, cancellationToken).ConfigureAwait(true);
            }

            RaiseAll();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _allRecords = [];
            Raids = [];
            MapStats = [];
            Status = $"Raid history unavailable: {exception.Message}";
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
            records.Add(new DebriefRaidRecord(raid, facts.Side, sources, facts.LoadSeconds));
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
        _mapFilterOptions = [new(null, "All maps"), .. maps];
        OnPropertyChanged(nameof(MapFilterOptions));
        OnPropertyChanged(nameof(SelectedMapFilterOption));
    }

    /// <summary>
    /// Applies the current search text and filters to <see cref="_allRecords"/>: the visible list,
    /// and the per-map stats, which follow the same filtered set rather than the whole history.
    /// </summary>
    private void ApplyFilters()
    {
        var filtered = _allRecords.Where(MatchesFilters).ToArray();
        var rows = new List<DebriefRaidRowViewModel>(filtered.Length);
        foreach (var record in filtered)
        {
            var raid = record.Raid;
            rows.Add(new DebriefRaidRowViewModel(
                raid.Id,
                raid.MapId is { } mapId ? MapLabel(mapId) : "Unknown map",
                raid.Mode,
                LocalTime.Moment(raid.StartedUtc) ?? "Unknown",
                LocalTime.Moment(raid.EndedUtc) ?? "In progress",
                Duration(raid),
                raid.Outcome ?? "Not recorded")
            {
                SelectCommand = new AsyncDelegateCommand(() => SelectRaidAsync(raid.Id, CancellationToken.None)),
                IsSelected = _selected?.Id == raid.Id,
                OutcomeKindLabel = record.Sources.Outcome.Label(),
            });
        }

        Raids = rows;
        MapStats = BuildMapStats(filtered);
        Status = BuildStatusLabel(filtered.Length, _allRecords.Count);
        RaiseAll();
    }

    private static string BuildStatusLabel(int shown, int total)
    {
        if (total == 0)
        {
            return "No raids recorded yet.";
        }

        var totalLabel = CountLabel(total, "raid");
        return shown == total ? totalLabel : $"{shown.ToString(CultureInfo.CurrentCulture)} of {totalLabel}";
    }

    private bool MatchesFilters(DebriefRaidRecord record)
    {
        var raid = record.Raid;
        if (_mapFilter is { } mapFilter && !string.Equals(raid.MapId, mapFilter, StringComparison.OrdinalIgnoreCase))
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
        _outcomeFilter = DebriefOutcomeFilter.Any;
        _sideFilter = DebriefSideFilter.Any;
        _dateFrom = null;
        _dateTo = null;
        ApplyFilters();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(SelectedMapFilterOption));
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

    private static readonly (DebriefOutcomeFilter Filter, string Label)[] OutcomeFilterOptions =
    [
        (DebriefOutcomeFilter.Any, "Any outcome"),
        (DebriefOutcomeFilter.Survived, "Survived"),
        (DebriefOutcomeFilter.Died, "Died"),
        (DebriefOutcomeFilter.Mia, "MIA"),
        (DebriefOutcomeFilter.RunThrough, "Run-through"),
    ];

    private static readonly (DebriefSideFilter Filter, string Label)[] SideFilterOptions =
    [
        (DebriefSideFilter.Any, "Any side"),
        (DebriefSideFilter.Pmc, "PMC"),
        (DebriefSideFilter.Scav, "Scav"),
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

    public async Task SelectRaidAsync(Guid raidId, CancellationToken cancellationToken)
    {
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
            SelectedScanSummary = "No scans during this raid.";
            SelectedLoadTimeLabel = "Load time not recorded.";
            _selectedLoadRecorded = false;
            _selectedSources = RaidFactRules.Classify(
                new RaidHistoryEntry(Guid.Empty, Guid.Empty, null, string.Empty, null, null, null, null),
                []);
        }
        else
        {
            SelectedSales = await LoadSalesAsync(raidId, cancellationToken).ConfigureAwait(true);
            SelectedQuestEvents = await LoadQuestEventsAsync(raidId, cancellationToken).ConfigureAwait(true);
            var stateFacts = await ReadStateFactsAsync(raidId, cancellationToken).ConfigureAwait(true);
            _selectedLoadRecorded = stateFacts.LoadSeconds is not null;
            SelectedLoadTimeLabel = stateFacts.LoadSeconds is { } loadSeconds
                ? $"{loadSeconds.ToString("0.0", CultureInfo.CurrentCulture)}s queue/load"
                : "Load time not recorded.";
            _selectedSources = RaidFactRules.Classify(
                _selected,
                await LoadCorrectionsAsync(raidId, cancellationToken).ConfigureAwait(true));
            await LoadScansAsync(raidId, cancellationToken).ConfigureAwait(true);
        }

        SelectedFacts = BuildFacts();

        Raids = Raids.Select(row => row with { IsSelected = row.RaidId == raidId }).ToArray();
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
            new("Mode", SelectedModeLabel, _selectedSources.Mode.Label()),
            new("Started", SelectedStartedLabel, _selectedSources.Started.Label()),
            new("Ended", SelectedEndedLabel, _selectedSources.Ended.Label()),
            new("Duration", SelectedDurationLabel, _selectedSources.Duration.Label()),
            new("Queue/load", SelectedLoadTimeLabel, _selectedLoadRecorded ? RaidFactKind.Observed.Label() : string.Empty),
            new("Outcome", SelectedOutcomeLabel, _selectedSources.Outcome.Label()),
        };
        if (SelectedNotesLabel.Length > 0)
        {
            facts.Add(new("Notes", SelectedNotesLabel, _selectedSources.Notes.Label()));
        }

        return facts;
    }

    /// <summary>What was scanned while this raid was open, and how much of it named an item.</summary>
    private async Task LoadScansAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService
            .ListEventPayloadsAsync(raidId, "scan", cancellationToken)
            .ConfigureAwait(true);
        var scans = payloads.Select(RaidScanFact.TryParse).OfType<RaidScanFact>().OrderBy(scan => scan.ObservedUtc).ToArray();
        var rows = new List<DebriefScanRowViewModel>(scans.Length);
        foreach (var scan in scans)
        {
            var itemName = scan.Recognised
                ? scan.ItemName ?? (scan.ItemId is null ? "Item" : await ResolveItemNameAsync(scan.ItemId, cancellationToken).ConfigureAwait(true))
                : scan.IsAvailable ? "Nothing recognised" : "Scan unavailable";
            var detail = new List<string>();
            if (scan.Recognised && scan.Confidence is { } confidence)
            {
                detail.Add(string.Create(CultureInfo.CurrentCulture, $"{confidence:P0} sure"));
            }

            if (scan.Recommendation is { Length: > 0 } recommendation)
            {
                detail.Add(recommendation);
            }

            rows.Add(new(
                LocalTime.ShortTime(scan.ObservedUtc),
                itemName,
                scan.IdentityKind.Label(),
                scan.ValueRoubles is { } roubles
                    ? string.Create(CultureInfo.CurrentCulture, $"≈ {roubles:N0} roubles")
                    : string.Empty,
                scan.ValueKind.Label(),
                string.Join(" · ", detail)));
        }

        SelectedScans = rows;
        var recognised = scans.Count(scan => scan.Recognised);
        var unavailable = scans.Count(scan => !scan.IsAvailable);
        SelectedScanSummary = scans.Length == 0
            ? "No scans during this raid."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{CountLabel(scans.Length, "scan")} · {recognised:N0} recognised{(unavailable > 0 ? $" · {unavailable:N0} unavailable" : string.Empty)}");
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
                ? "Item not in the synced catalog"
                : await ResolveItemNameAsync(itemId, cancellationToken).ConfigureAwait(true);
            rows.Add(new(
                name,
                info.Count == 1 ? "1 sold" : $"{info.Count.ToString(CultureInfo.CurrentCulture)} sold",
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

            rows.Add(new(
                MapLabel(group.Key),
                CountLabel(raidsOnMap.Length, "raid"),
                durations.Length == 0
                    ? "Duration unknown"
                    : $"avg {FormatDuration(AverageTicks(durations))}",
                loadTimes.Count == 0
                    ? "Load time unknown"
                    : $"avg {loadTimes.Average().ToString("0.0", CultureInfo.CurrentCulture)}s "
                        + $"({CountLabel(loadTimes.Count, "raid")} measured)"));
        }

        return rows;
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
            var catalog = await _questCatalog.GetAsync(profile.GameMode, "en", cancellationToken).ConfigureAwait(true);
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
        RecordedTaskState.Completed => "Handed in",
        RecordedTaskState.Failed => "Failed",
        RecordedTaskState.Active => "Started",
        _ => state.ToString(),
    };

    private static TimeSpan AverageTicks(IReadOnlyList<TimeSpan> durations) =>
        TimeSpan.FromTicks((long)durations.Average(duration => duration.Ticks));

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\h\ mm\m" : @"mm\m\ ss\s", CultureInfo.InvariantCulture);

    private static string CountLabel(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.CurrentCulture)} {noun}s";

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
            Status = "Correction saved.";
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"That correction could not be saved: {exception.Message}";
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

            Status = $"Exported to {destination}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Export failed: {exception.Message}";
        }
    }

    private string MapLabel(string mapId) => _mapName(mapId) is { Length: > 0 } name ? name : mapId;

    private static string Duration(RaidHistoryEntry? raid)
    {
        if (raid?.StartedUtc is not { } started)
        {
            return "Unknown";
        }

        if (raid.EndedUtc is null)
        {
            return "In progress";
        }

        var elapsed = raid.EndedUtc.Value - started;
        return elapsed <= TimeSpan.Zero ? "Unknown" : FormatDuration(elapsed);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Raids));
        OnPropertyChanged(nameof(HasRaids));
        OnPropertyChanged(nameof(HasNoRaids));
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
        OnPropertyChanged(nameof(OutcomeFilterChips));
        OnPropertyChanged(nameof(SideFilterChips));
        OnPropertyChanged(nameof(HasActiveFilters));
    }
}
