using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels;

public abstract class BindableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

public sealed class AsyncDelegateCommand(Func<Task> execute) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter) => await ExecuteAsync().ConfigureAwait(true);

    public async Task ExecuteAsync()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class NavigationItem : BindableViewModel
{
    private bool _isSelected;
    private bool _hasNotice;

    internal NavigationItem(string name, string glyph, PageViewModel page, Action<NavigationItem> select)
    {
        Name = name;
        Glyph = glyph;
        Page = page;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Name { get; }

    /// <summary>
    /// The icon, as path geometry rather than a character.
    /// </summary>
    /// <remarks>
    /// These were Unicode characters picked by eye, from completely different blocks: a map
    /// symbol, a dingbat, an arrow, a rouble sign. Each block resolves to a different fallback
    /// font, so every row rendered at its own size, weight, stroke and baseline, and the rail
    /// looked ragged for a reason nobody could point at. Keys was tiny, Loadout was a dense
    /// block, Flea was a letter.
    ///
    /// Drawn here they share one grid, one stroke width and one cap style, because they are one
    /// set rather than fourteen characters that happened to be available.
    /// </remarks>
    public string Glyph { get; }

    public PageViewModel Page { get; }

    public ICommand SelectCommand { get; }

    /// <summary>
    /// Whether this destination is asking to be visited.
    /// </summary>
    /// <remarks>
    /// One mark, on the rail, so it is visible from whatever page somebody is on. Putting the
    /// news inside the page it concerns means the only way to learn there is news is to go
    /// looking for it, which is the state this replaces: a new build could sit in the feed for
    /// days because nobody opened Settings.
    /// </remarks>
    public bool HasNotice
    {
        get => _hasNotice;
        set => SetProperty(ref _hasNotice, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed record StatusChip(string Label, string Value, string Evidence, string Color)
{
    /// <summary>
    /// Whether the chip belongs to the raid readout rather than the health strip.
    /// </summary>
    /// <remarks>
    /// Map, raid and position are what a player glances at mid-raid. Whether the game is
    /// being observed, how old the data is and whether a scan is possible only matter when
    /// they are wrong, so the shell draws those smaller and lets their colour do the talking.
    /// </remarks>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// Says what the game is doing, in the words a player would use.
/// </summary>
/// <remarks>
/// These used to reach the interface through ToString(), so the readout a player glances at
/// mid-raid said "InRaid", "PostRaid" and, best of all, "LauncherOrGameDetected". Those are
/// names for the code's benefit. Nobody playing the game calls it that, and the longest one
/// appears exactly when somebody is first wondering whether the companion is working.
/// </remarks>
public static class RaidStateText
{
    public static string Describe(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.InRaid => "In raid",
        RaidLifecycleState.LoadingRaid => "Loading into a raid",
        RaidLifecycleState.PostRaid => "Raid over",
        RaidLifecycleState.Menu => "In the menus",
        RaidLifecycleState.LauncherOrGameDetected => "Game running",
        _ => "Not observed",
    };
}

public abstract class PageViewModel(string title, string description, string evidence) : BindableViewModel
{
    /// <summary>
    /// Whether the shell draws its title block above this page.
    /// </summary>
    /// <remarks>
    /// A page that is mostly one large picture wants the height more than it wants a heading
    /// that repeats what the picture already says. The map page names the current map on the
    /// map itself, so the shell's copy of it was eighty pixels spent saying it twice.
    /// </remarks>
    public virtual bool ShowsPageHeader => true;

    private string _title = title;
    private string _description = description;
    private string _evidence = evidence;

    public string Title
    {
        get => _title;
        protected set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        protected set => SetProperty(ref _description, value);
    }

    public string Evidence
    {
        get => _evidence;
        protected set => SetProperty(ref _evidence, value);
    }
}

public sealed class RaidPageViewModel : PageViewModel
{
    /// <inheritdoc />
    /// <remarks>
    /// The map is the page. Everything the header would have said is either on the map or in
    /// the status chips along the top of the window.
    /// </remarks>
    public override bool ShowsPageHeader => false;

    private readonly IRaidHistoryService? _raidHistoryService;
    private readonly List<string> _scannedThisRaid = [];
    private string _raidState = "No raid evidence";
    private string _position = "No last-known position";
    private string _extracts = "None observed";
    private RaidSummaryViewModel? _summary;
    private RaidSnapshot? _lastInRaid;
    private string? _lastInRaidMode;
    private RaidLifecycleState _previousState = RaidLifecycleState.Unknown;
    private Guid? _summaryRaidId;
    private Guid? _scanRaidId;
    private DateTimeOffset _lastScanObservedUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates the raid page.
    /// </summary>
    /// <remarks>
    /// The raid history service is optional because the summary does not depend on it: every
    /// line is built from state the page has already observed. History is read afterwards
    /// only to confirm the finished raid reached the local database, so when no service is
    /// supplied that one line says the history was not read and the rest is unaffected.
    /// </remarks>
    public RaidPageViewModel(MapViewModel map, IRaidHistoryService? raidHistoryService = null)
        : base("Raid reference", "The map, and where your screenshots put you", "No raid evidence")
    {
        Map = map;
        _raidHistoryService = raidHistoryService;
        DismissSummaryCommand = new DelegateCommand(DismissSummary);
    }

    public MapViewModel Map { get; }

    /// <summary>
    /// The raid that has just finished, or null when none has finished this session.
    /// </summary>
    /// <remarks>
    /// Deliberately sticky. It is set on the single transition out of a raid and then left
    /// alone: the snapshots that follow a raid end arrive seconds apart, and clearing on any
    /// of them would make the summary flash past before it could be read. The player
    /// dismisses it, or the next finished raid replaces it.
    /// </remarks>
    public RaidSummaryViewModel? Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(HasSummary));
            }
        }
    }

    /// <summary>Whether a finished raid is available to show.</summary>
    public bool HasSummary => Summary is not null;

    /// <summary>
    /// Puts the summary away.
    /// </summary>
    /// <remarks>
    /// The summary sits above the live raid panels, so without a way to dismiss it the panel
    /// would still be covering the top of the page during the next raid.
    /// </remarks>
    public DelegateCommand DismissSummaryCommand { get; }

    public string RaidState
    {
        get => _raidState;
        private set => SetProperty(ref _raidState, value);
    }

    public string Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public string Extracts
    {
        get => _extracts;
        private set => SetProperty(ref _extracts, value);
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var raid = snapshot.Raid;
        Title = raid.MapId is null ? "Raid reference" : $"{raid.MapId} raid";
        RaidState = raid.State == RaidLifecycleState.Unknown
            ? "No raid state has been observed."
            : $"{RaidStateText.Describe(raid.State)} · {FormatAge(raid.UpdatedUtc, nowUtc)}";
        Position = raid.LastKnownPosition is null
            ? "No position yet"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"X {raid.LastKnownPosition.Position.X:F1}, Y {raid.LastKnownPosition.Position.Y:F1}, Z {raid.LastKnownPosition.Position.Z:F1} · screenshot {FormatAge(raid.LastKnownPosition.Timestamp, nowUtc)}");
        Extracts = raid.ActiveExtracts.Count == 0
            ? "None observed"
            : string.Join(
                ", ",
                raid.ActiveExtracts.Select(extract =>
                    $"{extract.Name} ({extract.Confidence.Value:P0}, {extract.Source})"));
        Evidence = raid.UpdatedUtc == DateTimeOffset.UnixEpoch
            ? "No raid evidence"
            : $"{raid.Confidence.Value:P0} confidence · observed {FormatAge(raid.UpdatedUtc, nowUtc)}";
        ObserveLifecycle(snapshot);
    }

    /// <summary>
    /// Notices that a raid has ended and builds the summary of it.
    /// </summary>
    /// <remarks>
    /// The end of a raid is the single edge from InRaid to PostRaid or Menu. A transfer to
    /// another map keeps the state at InRaid by design, so no edge occurs and no summary is
    /// produced for a raid that is still being played.
    ///
    /// The details of the raid are taken from the last snapshot seen while it was running,
    /// not from the snapshot that ends it: returning to the menu clears the map, the start
    /// time and the last-known position out of the raid state.
    /// </remarks>
    private void ObserveLifecycle(ApplicationRuntimeSnapshot snapshot)
    {
        var raid = snapshot.Raid;
        TrackScans(snapshot);
        if (raid.State == RaidLifecycleState.InRaid)
        {
            _lastInRaid = raid;
            _lastInRaidMode = snapshot.Profile?.GameMode.ToString();
        }

        var finished = _previousState == RaidLifecycleState.InRaid
            && raid.State is RaidLifecycleState.PostRaid or RaidLifecycleState.Menu;
        _previousState = raid.State;
        if (!finished || _lastInRaid is not { } lastInRaid)
        {
            return;
        }

        // Cleared immediately so a further Menu snapshot after PostRaid cannot rebuild the
        // same summary and reset the history line that is already being filled in.
        _lastInRaid = null;
        _summaryRaidId = lastInRaid.RaidId;
        Summary = RaidSummaryViewModel.Create(
            ResolveMapName(lastInRaid.MapId),
            lastInRaid.StartedUtc,
            raid.UpdatedUtc,
            _lastInRaidMode ?? snapshot.Profile?.GameMode.ToString(),
            // The ending notification is preferred over the raid's own record, because a
            // transfer establishes the side outright and only arrives on the line that ends
            // the raid. The raid's own record is the fallback for every other ending.
            raid.Side ?? lastInRaid.Side,
            _scannedThisRaid.ToArray(),
            lastInRaid.LastKnownPosition,
            _raidHistoryService is null ? "Not read" : "Checking…");
        if (_raidHistoryService is not null && lastInRaid.RaidId is { } raidId)
        {
            // Deliberately not awaited: the summary is already complete and correct, and the
            // snapshot handler that called this runs on the UI thread.
            _ = ConfirmAgainstHistoryAsync(raidId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Remembers what the player scanned while this raid was open.
    /// </summary>
    /// <remarks>
    /// These are exactly the scans the raid coordinator writes to raid history, which records
    /// a scan event whenever a raid id is open. The history service exposes no way to read
    /// those events back, so the page counts the same scans as they pass through the runtime
    /// snapshot instead of querying for them.
    ///
    /// A new raid takes the scan already on screen as its baseline, so an item looked up in
    /// the menu beforehand is never attributed to the raid that follows it.
    /// </remarks>
    private void TrackScans(ApplicationRuntimeSnapshot snapshot)
    {
        if (snapshot.Raid.RaidId is not { } raidId)
        {
            return;
        }

        if (_scanRaidId != raidId)
        {
            _scanRaidId = raidId;
            _scannedThisRaid.Clear();
            _lastScanObservedUtc = snapshot.Scan.ObservedUtc;
            return;
        }

        var scan = snapshot.Scan;
        if (!scan.Succeeded
            || scan.ObservedUtc <= _lastScanObservedUtc
            || (snapshot.Raid.StartedUtc is { } startedUtc && scan.ObservedUtc < startedUtc))
        {
            return;
        }

        _lastScanObservedUtc = scan.ObservedUtc;
        _scannedThisRaid.Add(scan.ItemName ?? "Unnamed item");
    }

    /// <summary>
    /// Confirms that the finished raid reached the local history database.
    /// </summary>
    /// <remarks>
    /// This is the one part of the summary that cannot be answered from observed state, and
    /// it is worth answering: it is the difference between a raid the player can still export
    /// tomorrow and one that was only ever on screen. A failure is reported in the summary
    /// rather than thrown, because the rest of the summary remains true regardless.
    /// </remarks>
    private async Task ConfirmAgainstHistoryAsync(Guid raidId, CancellationToken cancellationToken)
    {
        if (_raidHistoryService is null)
        {
            return;
        }

        string history;
        try
        {
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            var entry = raids.FirstOrDefault(candidate => candidate.Id == raidId);
            history = entry is null
                ? "Not saved"
                : entry.EndedUtc is { } endedUtc
                    ? string.Create(CultureInfo.CurrentCulture, $"Saved · closed {endedUtc.ToLocalTime():g}")
                    : "Saved · no end time";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            history = $"Unreadable · {exception.Message}";
        }

        // A raid that finished while this was running has already replaced the summary, and
        // the older answer must not overwrite the newer one.
        if (_summaryRaidId == raidId && Summary is { } summary)
        {
            Summary = summary with { History = history };
        }
    }

    /// <summary>
    /// Names the map the way the map catalogue does, falling back to the logged token.
    /// </summary>
    /// <remarks>
    /// The game logs tokens such as "bigmap" rather than display names, and the catalogue is
    /// only loaded once maps have synced, so the raw token has to remain an acceptable answer.
    /// </remarks>
    private string ResolveMapName(string? mapId) => string.IsNullOrWhiteSpace(mapId)
        ? "Unknown map"
        : Map.Locations.FirstOrDefault(location =>
            string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase))?.Name ?? mapId;

    private void DismissSummary()
    {
        _summaryRaidId = null;
        Summary = null;
    }

    private static string FormatAge(DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - observedUtc;
        return age < TimeSpan.Zero ? "timestamp is in the future" : $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
    }
}

public sealed record ItemSearchResultViewModel(
    string Id,
    string Name,
    string ShortName,
    string Category,
    string Dimensions,
    string Match,
    string Price,
    string PriceSource,
    string Updated);

public sealed class ItemsPageViewModel : PageViewModel
{
    private readonly IItemSearchService _searchService;
    private readonly IItemRepository _itemRepository;
    private string _searchQuery = string.Empty;
    /// <summary>What the status line says when there is nothing wrong and nothing searched.</summary>
    private const string ReadyToSearch = "Search by name or short name";

    private bool _showingNoData;
    private string _searchStatus = ReadyToSearch;
    private IReadOnlyList<ItemSearchResultViewModel> _results = [];
    private ApplicationRuntimeSnapshot? _snapshot;

    public ItemsPageViewModel(IItemSearchService searchService, IItemRepository itemRepository)
        : base("Items", "Search the normalized local json.tarkov.dev cache", "Runtime state not loaded")
    {
        _searchService = searchService;
        _itemRepository = itemRepository;
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
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

    public IReadOnlyList<ItemSearchResultViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public AsyncDelegateCommand SearchCommand { get; }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.IsDemoMode == true)
        {
            SearchQuery = "Graphics Card";
            await SearchAsync(cancellationToken).ConfigureAwait(true);
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
            var hits = await _searchService.SearchAsync(SearchQuery, 12, cancellationToken).ConfigureAwait(true);
            var results = new List<ItemSearchResultViewModel>(hits.Count);
            foreach (var hit in hits)
            {
                var price = await _itemRepository.GetPriceAsync(hit.Item.Id, cancellationToken).ConfigureAwait(true);
                var bestValue = price?.BestEconomicValue ?? 0;
                var channel = price?.BestSaleChannel.ToString() ?? "Unavailable";
                results.Add(new(
                    hit.Item.Id,
                    hit.Item.Name,
                    hit.Item.ShortName,
                    hit.Item.Category.ToString(),
                    $"{hit.Item.Dimensions.Width} × {hit.Item.Dimensions.Height} · {hit.Item.Dimensions.Slots} slot(s)",
                    $"{hit.Score:P0} · matched {hit.MatchedText}",
                    bestValue > 0 ? $"{bestValue:N0} ₽ · {hit.Item.ValuePerSlot(price!):N0} ₽ / slot" : "Price unavailable",
                    channel,
                    $"json.tarkov.dev · {hit.Item.Provenance.SourceUpdatedUtc?.ToUniversalTime():u}"));
            }

            Results = results;
            SearchStatus = results.Count == 0
                ? "No local item matched that query."
                : $"{results.Count} results";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Results = [];
            SearchStatus = $"Item search failed: {exception.Message}";
        }
    }
}

public sealed class ScannerPageViewModel : PageViewModel
{
    private readonly IRuntimeScanUseCase _scanUseCase;
    private string _itemName = "No item scanned";
    private string _value = "Unavailable";
    private string _recommendation = "No recommendation without observed evidence.";
    private string _confidence = "Unavailable";
    private string _source = "No capture source";
    private string _detail = "Nothing has been scanned yet.";
    private bool _hasResult;

    public ScannerPageViewModel(IRuntimeScanUseCase scanUseCase)
        : base("Scanner", "Read the last screenshot you took", "Runtime state not loaded")
    {
        _scanUseCase = scanUseCase;
        ScanCommand = new AsyncDelegateCommand(ScanAsync);
    }

    public AsyncDelegateCommand ScanCommand { get; }

    public string ItemName
    {
        get => _itemName;
        private set => SetProperty(ref _itemName, value);
    }

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Recommendation
    {
        get => _recommendation;
        private set => SetProperty(ref _recommendation, value);
    }

    public string Confidence
    {
        get => _confidence;
        private set => SetProperty(ref _confidence, value);
    }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>
    /// Whether the last scan produced something to read.
    /// </summary>
    /// <remarks>
    /// The page at rest and the page after a scan want different layouts. Before anything has
    /// been scanned, a readout whose every row says "unavailable" tells the player less than
    /// one line naming the shortcut does, and it repeated the same sentence four times.
    /// </remarks>
    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (SetProperty(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(HasNoResult));
            }
        }
    }

    public bool HasNoResult => !HasResult;

    public Task ScanAsync() => ScanAsync(CancellationToken.None);

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _scanUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = $"Scan failed: {exception.Message}";
        }
    }

    public void Apply(ScanExecutionResult scan)
    {
        HasResult = scan.Succeeded;
        ItemName = scan.Succeeded ? scan.ItemName ?? "Unnamed item" : "No item scanned";
        Value = scan.Succeeded && scan.ValueRoubles is { } value
            ? $"{value:N0} ₽ · {scan.ValuePerSlotRoubles.GetValueOrDefault():N0} ₽ / slot"
            : "Unavailable";
        Recommendation = scan.Succeeded
            ? scan.Recommendation ?? "No recommendation was produced."
            : "No recommendation without observed evidence.";
        Confidence = scan.Succeeded ? scan.Confidence.Value.ToString("P0", CultureInfo.CurrentCulture) : "Unavailable";
        Source = scan.Source;
        Detail = scan.Detail;
        Evidence = scan.Succeeded
            ? $"{Confidence} confidence · {scan.Source} · {scan.ObservedUtc.ToLocalTime():T}"
            : scan.Detail;
    }
}

public sealed record RaidHistoryEntryViewModel(
    string Id,
    string Map,
    string Mode,
    string Started,
    string Ended,
    string Outcome,
    string Notes);

public sealed class HistoryPageViewModel : PageViewModel
{
    private readonly IRaidHistoryService _raidHistoryService;
    private IReadOnlyList<RaidHistoryEntryViewModel> _entries = [];
    private string _status = "History has not been loaded.";

    public HistoryPageViewModel(IRaidHistoryService raidHistoryService)
        : base("History", "Every raid the companion has seen", "Runtime state not loaded")
    {
        _raidHistoryService = raidHistoryService;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    public IReadOnlyList<RaidHistoryEntryViewModel> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            Entries = raids.Select(raid => new RaidHistoryEntryViewModel(
                raid.Id.ToString("D"),
                raid.MapId ?? "Unknown map",
                raid.Mode,
                raid.StartedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown",
                raid.EndedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "In progress",
                raid.Outcome ?? "Not recorded",
                raid.Notes ?? "No notes")).ToArray();
            Status = Entries.Count == 0
                ? "No local raid history has been recorded."
                : $"{Entries.Count} local raid entr{(Entries.Count == 1 ? "y" : "ies")}.";
            Evidence = $"SQLite · {Status}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Entries = [];
            Status = $"Raid history unavailable: {exception.Message}";
            Evidence = Status;
        }
    }
}

public sealed class SettingsPageViewModel : PageViewModel
{
    /// <summary>
    /// Updating the application from inside it, rather than by hand.
    /// </summary>
    /// <remarks>
    /// Every build reached its machine tonight because a person downloaded an artifact,
    /// checked a hash and swapped a folder. That is a great deal of ceremony to change one
    /// line, and it means a fix only arrives while somebody is awake to carry it.
    ///
    /// Only builds that passed Windows verification are ever offered, and the download is
    /// refused unless its checksum matches what was published, so the shortcut does not come
    /// at the cost of the checks.
    /// </remarks>

    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly IScreenshotRetentionStore _retentionSettings;
    private readonly IRecycleBin _recycleBin;
    private ScreenshotRetentionSettings _retention = ScreenshotRetentionSettings.Default;
    private string _retentionStatus = "Reading how long screenshots are kept…";
    private readonly VelopackUpdateGateway? _updates;
    private string _updateStatus = "Updates have not been checked this session.";
    private string _installedBuild = "Build unknown";
    private bool _isBusyWithUpdate;
    private bool _canDownloadUpdate;
    private bool _canRestartForUpdate;
    private string _dataStatus = "Runtime state not loaded";
    private string _profileContext = "Profile unavailable";
    private string _scanProvider = "Unavailable";

    public SettingsPageViewModel(
        ApplicationStartupCoordinator startupCoordinator,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        IOcrEngineStatus ocrStatus,
        IScreenshotRetentionStore retentionSettings,
        IRecycleBin recycleBin,
        VelopackUpdateGateway? updates = null,
        // Optional so a composition without stored settings still builds a Settings page,
        // which is what the tests that construct this by hand rely on.
        IEftPathOverrideStore? gameFolders = null,
        RaidObservationService? observation = null)
        : base("Settings & diagnostics", "Runtime configuration and a manual data refresh", "Not loaded")
    {
        ArgumentNullException.ThrowIfNull(ocrStatus);
        ArgumentNullException.ThrowIfNull(retentionSettings);
        ArgumentNullException.ThrowIfNull(recycleBin);
        _startupCoordinator = startupCoordinator;
        _retentionSettings = retentionSettings;
        _recycleBin = recycleBin;
        _updates = updates;
        _gameFolders = gameFolders;
        _observation = observation;
        CheckForUpdateCommand = new AsyncDelegateCommand(CheckForUpdateAsync);
        DownloadUpdateCommand = new AsyncDelegateCommand(DownloadUpdateAsync);
        RestartForUpdateCommand = new DelegateCommand(RestartForUpdate);
        if (_updates is not null)
        {
            _installedBuild = _updates.InstalledBuild;
            if (!_updates.IsInstalled)
            {
                _updateStatus = "Run from a folder, so it cannot update itself";
            }
        }
        // The engine explains exactly why it is unavailable - a missing Visual C++ runtime
        // reads very differently from an unsupported architecture - but until now only the
        // headless self-test ever read that reason, so the user saw a bare "Unavailable".
        RecognitionProvider = ocrStatus.Availability.IsAvailable
            ? $"Available · {ocrStatus.Availability.Provider}"
            : $"Unavailable · {ocrStatus.Availability.Provider} · {ocrStatus.Availability.Reason ?? "No reason was reported."}";
        IsOffline = options.Offline;
        DatabasePath = Path.Combine(paths.Database, "tarkov-companion.db");
        DiagnosticChannel = commandLine.DeveloperMode && !string.IsNullOrWhiteSpace(commandLine.DiagnosticChannelPath)
            ? "Requested"
            : "Off · needs developer mode and a path";
        SyncCommand = new AsyncDelegateCommand(SyncAsync);
        ToggleScreenshotTidyingCommand = new AsyncDelegateCommand(ToggleScreenshotTidyingAsync);
        ChooseRetentionCommand = new AsyncDelegateCommand(ChooseRetentionAsync);
        SaveGameFoldersCommand = new AsyncDelegateCommand(SaveGameFoldersAsync);
        _ = LoadRetentionAsync();
        _ = LoadGameFoldersAsync();
    }

    private readonly IEftPathOverrideStore? _gameFolders;
    private readonly RaidObservationService? _observation;
    private string _screenshotFolder = string.Empty;
    private string _logFolder = string.Empty;
    private string _gameFolderStatus = "Found on their own";
    private string _watchedFolders = "Looking for the game…";

    /// <summary>
    /// Where the game keeps its screenshots, when the companion cannot work it out.
    /// </summary>
    /// <remarks>
    /// It guesses from the install, from Documents, from Pictures, and from wherever OneDrive
    /// says it has put those. That covers the arrangements we know of and will never cover all
    /// of them: the first person to install this who does not use OneDrive had no screenshots
    /// detected and no way at all to say where they were.
    ///
    /// Left blank, the guessing stands.
    /// </remarks>
    public string ScreenshotFolder
    {
        get => _screenshotFolder;
        set => SetProperty(ref _screenshotFolder, value);
    }

    /// <summary>Where the game writes one log folder per launch.</summary>
    public string LogFolder
    {
        get => _logFolder;
        set => SetProperty(ref _logFolder, value);
    }

    public string GameFolderStatus
    {
        get => _gameFolderStatus;
        private set => SetProperty(ref _gameFolderStatus, value);
    }

    /// <summary>The folders actually being watched right now, which is the answer to "is it on".</summary>
    public string WatchedFolders
    {
        get => _watchedFolders;
        private set => SetProperty(ref _watchedFolders, value);
    }

    public AsyncDelegateCommand SaveGameFoldersCommand { get; }

    private async Task LoadGameFoldersAsync()
    {
        if (_gameFolders is null)
        {
            return;
        }

        try
        {
            var stored = await _gameFolders.GetAsync(CancellationToken.None).ConfigureAwait(true);
            ScreenshotFolder = stored.ScreenshotRoot ?? string.Empty;
            LogFolder = stored.LogRoot ?? string.Empty;
            GameFolderStatus = stored.IsEmpty ? "Found on their own" : "Set by you";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameFolderStatus = $"Unreadable · {exception.Message}";
        }
    }

    private async Task SaveGameFoldersAsync()
    {
        if (_gameFolders is null)
        {
            return;
        }

        try
        {
            var chosen = new EftPathOverrides(ScreenshotFolder, LogFolder).Normalized();
            await _gameFolders.SaveAsync(chosen, CancellationToken.None).ConfigureAwait(true);
            // Discovery only runs between watching sessions, and a session runs until it is
            // cancelled. Without this the folder just typed would do nothing until the next
            // launch, which reads as the setting being ignored.
            _observation?.Rediscover();
            GameFolderStatus = Describe(chosen);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameFolderStatus = $"Not saved · {exception.Message}";
        }
    }

    /// <summary>
    /// Says whether what was typed is going to be used, before the player goes looking.
    /// </summary>
    /// <remarks>
    /// A folder that is not there is ignored rather than honoured, so that a typo leaves the
    /// guessing working instead of leaving the companion watching nothing. Somebody who has
    /// just typed one has to be told that, or the setting looks broken rather than skipped.
    /// </remarks>
    private static string Describe(EftPathOverrides chosen)
    {
        if (chosen.IsEmpty)
        {
            return "Cleared · found on their own again";
        }

        var missing = new[] { chosen.ScreenshotRoot, chosen.LogRoot }
            .Where(path => path is { Length: > 0 } && !Directory.Exists(path))
            .ToArray();
        return missing.Length == 0
            ? "Saved · watching starts within a minute"
            : $"Saved, but not there: {string.Join(", ", missing)}";
    }

    public bool IsOffline { get; }

    public AsyncDelegateCommand CheckForUpdateCommand { get; }

    public AsyncDelegateCommand DownloadUpdateCommand { get; }

    public DelegateCommand RestartForUpdateCommand { get; }

    /// <summary>Which build is running, so a report of a bug can name it.</summary>
    public string InstalledBuild
    {
        get => _installedBuild;
        private set => SetProperty(ref _installedBuild, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    /// <summary>Raised when a build starts or stops waiting, so the rail can mark itself.</summary>
    public event EventHandler<bool>? UpdateWaitingChanged;

    /// <summary>Whether an offered build has not yet been fetched.</summary>
    public bool CanDownloadUpdate
    {
        get => _canDownloadUpdate;
        private set
        {
            if (SetProperty(ref _canDownloadUpdate, value))
            {
                UpdateWaitingChanged?.Invoke(this, value || CanRestartForUpdate);
            }
        }
    }

    /// <summary>Whether a build is unpacked and waiting for the application to close.</summary>
    public bool CanRestartForUpdate
    {
        get => _canRestartForUpdate;
        private set
        {
            if (SetProperty(ref _canRestartForUpdate, value))
            {
                UpdateWaitingChanged?.Invoke(this, value || CanDownloadUpdate);
            }
        }
    }

    /// <summary>
    /// Looks for a newer build, on a timer, without anybody asking.
    /// </summary>
    /// <remarks>
    /// The only way to learn a build existed was to open this page and press a button, so one
    /// could sit in the feed for days. This checks shortly after launch and every few hours
    /// after that.
    ///
    /// Delayed rather than immediate at startup because it is a network call and startup is
    /// already doing the work somebody is waiting on. Hours rather than minutes because builds
    /// land a few times a day at most, and the check exists to be noticed between raids rather
    /// than during one.
    /// </remarks>
    public async Task WatchForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_updates is null || !_updates.IsInstalled)
        {
            return;
        }

        var delay = TimeSpan.FromMinutes(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromHours(4);
            if (IsBusyWithUpdate || CanRestartForUpdate)
            {
                continue;
            }

            Apply(await _updates.CheckAsync(cancellationToken).ConfigureAwait(true));
        }
    }

    public bool IsBusyWithUpdate
    {
        get => _isBusyWithUpdate;
        private set
        {
            if (SetProperty(ref _isBusyWithUpdate, value))
            {
                OnPropertyChanged(nameof(IsUpdateIdle));
            }
        }
    }

    public bool IsUpdateIdle => !IsBusyWithUpdate;

    public bool SupportsUpdates => _updates is not null;

    private async Task CheckForUpdateAsync()
    {
        if (_updates is null || IsBusyWithUpdate)
        {
            return;
        }

        IsBusyWithUpdate = true;
        CanDownloadUpdate = false;
        UpdateStatus = "Checking for a newer build…";
        try
        {
            Apply(await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            IsBusyWithUpdate = false;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_updates is null || IsBusyWithUpdate)
        {
            return;
        }

        IsBusyWithUpdate = true;
        CanDownloadUpdate = false;
        UpdateStatus = "Downloading…";
        try
        {
            Apply(await _updates.DownloadAsync(CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            IsBusyWithUpdate = false;
        }
    }

    private void Apply(UpdateProgress progress)
    {
        UpdateStatus = progress.Status;
        CanDownloadUpdate = progress.CanDownload;
        CanRestartForUpdate = progress.CanApply;
    }

    /// <summary>
    /// Installs and reopens. Normally does not return.
    /// </summary>
    /// <remarks>
    /// Closing is no longer this application's job. The updater replaces the files and starts
    /// the new build itself, which is the part the previous hand-written swap script could
    /// never do reliably: it had to wait for a process to release a folder that a file-sync
    /// client was holding open, and on a real machine that never happened.
    /// </remarks>
    private void RestartForUpdate()
    {
        if (_updates is null)
        {
            return;
        }

        try
        {
            CanRestartForUpdate = false;
            UpdateStatus = "Installing · it will close and reopen";
            _updates.ApplyAndRestart();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus = $"The update could not be started: {exception.Message}";
            CanRestartForUpdate = true;
        }
    }

    /// <summary>
    /// Tidying the game's screenshot folder, which is the one folder this application empties.
    /// </summary>
    /// <remarks>
    /// Every screenshot is the player's own file, taken deliberately, and the companion only
    /// reads them. Tidying them is therefore stated plainly on this page rather than done
    /// quietly: what will go, when it will go, and where it goes so it can be got back.
    ///
    /// A day is the shortest span that still covers an evening's play and the following
    /// morning, which is when somebody actually goes looking for the screenshot they meant to
    /// keep.
    /// </remarks>
    public AsyncDelegateCommand ToggleScreenshotTidyingCommand { get; }

    public AsyncDelegateCommand ChooseRetentionCommand { get; }

    /// <summary>Whether the folder is swept at all.</summary>
    public bool TidiesScreenshots => _retention.IsEnabled;

    public string ScreenshotTidyingLabel => TidiesScreenshots ? "Stop tidying" : "Start tidying";

    /// <summary>How long screenshots are kept, in the player's words rather than in hours.</summary>
    public string RetentionDisplay => _retention.SafeRetentionHours switch
    {
        24 => "24 hours",
        72 => "3 days",
        168 => "7 days",
        var hours when hours % 24 == 0 => $"{hours / 24} days",
        var hours => $"{hours} hours",
    };

    public string RetentionStatus
    {
        get => _retentionStatus;
        private set => SetProperty(ref _retentionStatus, value);
    }

    /// <summary>Whether tidying can happen at all on this machine.</summary>
    public bool CanTidyScreenshots => _recycleBin.IsAvailable;

    private async Task LoadRetentionAsync()
    {
        try
        {
            ApplyRetention(await _retentionSettings.GetAsync(CancellationToken.None).ConfigureAwait(true));
            RetentionStatus = DescribeRetention();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RetentionStatus = $"Unreadable · {exception.Message}";
        }
    }

    private Task ToggleScreenshotTidyingAsync() =>
        SaveRetentionAsync(_retention with { IsEnabled = !_retention.IsEnabled });

    /// <summary>Moves to the next span, which is a button rather than a list because there are four.</summary>
    private Task ChooseRetentionAsync() => SaveRetentionAsync(_retention with
    {
        RetentionHours = _retention.SafeRetentionHours switch
        {
            24 => 72,
            72 => 168,
            168 => 720,
            _ => 24,
        },
    });

    private async Task SaveRetentionAsync(ScreenshotRetentionSettings settings)
    {
        try
        {
            await _retentionSettings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
            ApplyRetention(settings);
            RetentionStatus = DescribeRetention();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RetentionStatus = $"Not saved · {exception.Message}";
        }
    }

    private void ApplyRetention(ScreenshotRetentionSettings settings)
    {
        _retention = settings;
        OnPropertyChanged(nameof(TidiesScreenshots));
        OnPropertyChanged(nameof(ScreenshotTidyingLabel));
        OnPropertyChanged(nameof(RetentionDisplay));
    }

    private string DescribeRetention()
    {
        if (!_recycleBin.IsAvailable)
        {
            return "No recycle bin here, so nothing is tidied";
        }

        return _retention.IsEnabled
            ? $"Older than {RetentionDisplay} go to the recycle bin · the newest is always kept"
            : "Left alone";
    }

    public string RecognitionProvider { get; }

    public string DatabasePath { get; }

    public string DiagnosticChannel { get; }

    public AsyncDelegateCommand SyncCommand { get; }

    public string DataStatus
    {
        get => _dataStatus;
        private set => SetProperty(ref _dataStatus, value);
    }

    public string ProfileContext
    {
        get => _profileContext;
        private set => SetProperty(ref _profileContext, value);
    }

    public string ScanProvider
    {
        get => _scanProvider;
        private set => SetProperty(ref _scanProvider, value);
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        DataStatus = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} items · {snapshot.Data.Detail}";
        WatchedFolders = snapshot.Observation switch
        {
            { ScreenshotRoot: { Length: > 0 } shots, LogRoot: { Length: > 0 } logs } =>
                $"Screenshots {shots} · logs {logs}",
            { ScreenshotRoot: { Length: > 0 } shots } => $"Screenshots {shots} · no log folder",
            { LogRoot: { Length: > 0 } logs } => $"Logs {logs} · no screenshot folder",
            _ => "Nothing found yet",
        };
        ProfileContext = snapshot.Profile is null
            ? "Profile unavailable"
            : $"{snapshot.Profile.Name} · level {snapshot.Profile.Level} · {snapshot.Profile.GameMode}";
        ScanProvider = snapshot.Scan.IsAvailable
            ? snapshot.Scan.Succeeded ? $"Last result: {snapshot.Scan.Source}" : snapshot.Scan.Detail
            : snapshot.Scan.Detail;
        Evidence = snapshot.DatabaseReady ? "Persistent database initialized" : "Database not initialized";
    }

    public async Task SyncAsync()
    {
        try
        {
            await _startupCoordinator.RefreshAsync(force: true, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DataStatus = $"Refresh failed: {exception.Message}";
        }
    }
}

public sealed class MainWindowViewModel : BindableViewModel, IDisposable
{
    /// <summary>Cancelled when the window goes, so background watchers stop with it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly GroupSessionService _group;
    private readonly IRuntimeStateStore _stateStore;
    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly SynchronizationContext? _synchronizationContext;

    // The chip palette mirrors Themes/InstrumentStyles.axaml. Grey is the resting state, and
    // each accent is reserved for one meaning so a glance at the bar reads the same way every
    // time: sage is live, cyan is a place, ochre is a value or a warning, coral is a fault.
    private const string RestingColor = "#8F9BA6";
    private const string InkColor = "#C6D0D8";
    private const string CyanColor = "#56B8C6";
    private const string OchreColor = "#C6A15B";
    private const string SageColor = "#77B895";
    private const string CoralColor = "#DF6A62";
    private string? _followedMapId;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private PageViewModel _currentPage;
    private IReadOnlyList<StatusChip> _status = [];
    private string _modeLabel = string.Empty;
    private string _lastScanName = "No item scanned";
    private bool _hasScan;
    private string _lastScanValue = "Unavailable";
    private string _lastScanAdvice = "No recommendation without observed evidence.";
    private string _lastScanEvidence = "No scan evidence";
    private bool _initialized;
    private bool _disposed;

    public MainWindowViewModel(
        IRuntimeStateStore stateStore,
        ApplicationStartupCoordinator startupCoordinator,
        IItemSearchService itemSearchService,
        IItemRepository itemRepository,
        IPriceHistoryService priceHistoryService,
        IRequirementCatalog requirementCatalog,
        IItemFactCatalog itemFactCatalog,
        IEventCatalog eventCatalog,
        IEventTrackerService eventTracker,
        IPlayerProfileService profileService,
        IRaidHistoryService raidHistoryService,
        IRuntimeScanUseCase scanUseCase,
        IOcrEngineStatus ocrStatus,
        IGroupSettingsStore groupSettings,
        GroupSessionService group,
        IScreenshotRetentionStore retentionSettings,
        IEftPathOverrideStore gameFolders,
        RaidObservationService observation,
        IRecycleBin recycleBin,
        VelopackUpdateGateway updates,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        MapViewModel map,
        QuestsPageViewModel quests,
        TimeProvider timeProvider,
        ILogger<MainWindowViewModel> logger)
    {
        _group = group;
        _stateStore = stateStore;
        _startupCoordinator = startupCoordinator;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        var synchronizationContext = SynchronizationContext.Current;
        _synchronizationContext = synchronizationContext?.GetType().Namespace?
            .StartsWith("Avalonia", StringComparison.Ordinal) == true
                ? synchronizationContext
                : null;

        Map = map;
        Raid = new(map, raidHistoryService);
        Scanner = new(scanUseCase);
        Items = new(itemSearchService, itemRepository);
        Quests = quests;
        History = new(raidHistoryService);
        Flea = new(itemSearchService, itemRepository, priceHistoryService);
        Hideout = new(requirementCatalog, profileService, itemRepository);
        Settings = new(
            startupCoordinator,
            options,
            paths,
            commandLine,
            ocrStatus,
            retentionSettings,
            recycleBin,
            updates,
            gameFolders,
            observation);
        Ammo = new(itemFactCatalog, itemRepository);
        Keys = new(itemFactCatalog, itemRepository);
        Loadout = new(itemFactCatalog, itemSearchService, itemRepository);
        Events = new(eventCatalog, eventTracker, itemRepository);
        Squad = new(itemRepository);
        Group = new(groupSettings);

        Navigation =
        [
            CreateNavigation("Raid", "M8,1.5 V4.5 M8,11.5 V14.5 M1.5,8 H4.5 M11.5,8 H14.5 M8,5.2 A2.8,2.8 0 1 1 7.99,5.2 Z", Raid),
            CreateNavigation("Squad", "M5.5,7 A2,2 0 1 1 5.49,7 Z M10.5,7 A2,2 0 1 1 10.49,7 Z M2,13.5 C2,11 3.6,10 5.5,10 C7.4,10 9,11 9,13.5 M9.6,10.1 C12,10.1 14,11.1 14,13.5", Squad),
            CreateNavigation("Group", "M2.5,6 H11 M9,3.5 L11.5,6 L9,8.5 M13.5,10.5 H5 M7,8 L4.5,10.5 L7,13", Group),
            CreateNavigation("Scanner", "M2,5 V2.5 H4.5 M11.5,2.5 H14 V5 M14,11.5 V14 H11.5 M4.5,14 H2 V11.5 M2.5,8 H13.5", Scanner),
            CreateNavigation("Items", "M8,2 L14,5.2 V10.8 L8,14 L2,10.8 V5.2 Z M2,5.2 L8,8.4 L14,5.2 M8,8.4 V14", Items),
            CreateNavigation("Ammo", "M8,1.5 C10,4 10.5,6 10.5,8.5 H5.5 C5.5,6 6,4 8,1.5 Z M5.5,8.5 H10.5 V12 H5.5 Z M5.5,12 H10.5 V14.5 H5.5 Z", Ammo),
            CreateNavigation("Keys", "M6,10 A3,3 0 1 1 5.99,10 Z M8.1,8.2 L13.5,2.8 M11.5,4.8 L13,6.3 M12.6,3.7 L14,5.1", Keys),
            CreateNavigation("Flea", "M2.5,8.5 L8.5,2.5 H13.5 V7.5 L7.5,13.5 Z M11,5 A0.9,0.9 0 1 1 10.99,5 Z", Flea),
            CreateNavigation("Quests", "M3,8.5 L6.5,12 L13,4", Quests),
            CreateNavigation("Hideout", "M2,7.5 L8,2 L14,7.5 M3.6,6.4 V14 H12.4 V6.4 M6.6,14 V9.5 H9.4 V14", Hideout),
            CreateNavigation("Events", "M4,14 V2 M4,2.6 H12.5 L10.4,5.8 L12.5,9 H4", Events),
            CreateNavigation("Loadout", "M2.5,2.5 H13.5 V13.5 H2.5 Z M2.5,8 H13.5 M8,2.5 V13.5", Loadout),
            CreateNavigation("History", "M8,1.5 A6.5,6.5 0 1 1 2.4,4.8 M2.4,4.8 V1.8 M2.4,4.8 H5.4 M8,4.5 V8.5 L11,10.2", History),
            CreateNavigation("Settings", "M2.5,4.5 H13.5 M2.5,8 H13.5 M2.5,11.5 H13.5 M6,2.9 V6.1 M10.5,6.4 V9.6 M5,9.9 V13.1", Settings),
        ];

        _currentPage = Navigation[0].Page;
        Navigation[0].IsSelected = true;

        // The rail carries the news, so it is visible from whatever page somebody is on.
        var settingsItem = Navigation.First(item => ReferenceEquals(item.Page, Settings));
        Settings.UpdateWaitingChanged += (_, waiting) => settingsItem.HasNotice = waiting;

        // The map knows where somebody clicked; the group session knows how to tell anybody.
        Map.GroupMarkRequested += (_, request) => _ = MarkForGroupAsync(request);

        _stateStore.Changed += RuntimeStateChanged;
        ApplySnapshot(_stateStore.Current);
    }

    /// <summary>
    /// Sends a mark and says what became of it.
    /// </summary>
    /// <remarks>
    /// One code path draws every mark, including your own, so nothing appears until the next
    /// exchange brings it back. That gap reads as the gesture not working unless something
    /// says otherwise, which is how it was reported.
    /// </remarks>
    private async Task MarkForGroupAsync(GroupMarkRequest request)
    {
        var sent = await _group
            .MarkAsync(request.MapId, request.Position, null, request.IsPing, CancellationToken.None)
            .ConfigureAwait(true);
        Map.ReportMark(request.IsPing, sent);
    }

    public bool IsDemoMode => _options.DemoMode;

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public FleaPageViewModel Flea { get; }

    public HideoutPageViewModel Hideout { get; }

    public AmmoPageViewModel Ammo { get; }

    public KeysPageViewModel Keys { get; }

    public LoadoutPageViewModel Loadout { get; }

    public EventsPageViewModel Events { get; }

    public SquadPageViewModel Squad { get; }

    public GroupPageViewModel Group { get; }

    public IReadOnlyList<StatusChip> Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(PrimaryStatus));
                OnPropertyChanged(nameof(SecondaryStatus));
            }
        }
    }

    /// <summary>The raid readout: map, raid state and position.</summary>
    public IReadOnlyList<StatusChip> PrimaryStatus => Status.Where(chip => chip.IsPrimary).ToArray();

    /// <summary>The health strip: observation, data freshness and scan readiness.</summary>
    public IReadOnlyList<StatusChip> SecondaryStatus => Status.Where(chip => !chip.IsPrimary).ToArray();

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public MapViewModel Map { get; }

    public RaidPageViewModel Raid { get; }

    public ScannerPageViewModel Scanner { get; }

    public ItemsPageViewModel Items { get; }

    public QuestsPageViewModel Quests { get; }

    public HistoryPageViewModel History { get; }

    public SettingsPageViewModel Settings { get; }

    public string ModeLabel
    {
        get => _modeLabel;
        private set => SetProperty(ref _modeLabel, value);
    }

    public string LastScanName
    {
        get => _lastScanName;
        private set => SetProperty(ref _lastScanName, value);
    }

    /// <summary>
    /// Whether a scan has actually produced a result to read.
    /// </summary>
    /// <remarks>
    /// Drives the scan readout between its one-line resting state and its full one. Before the
    /// first scan the detailed readout has nothing in it but the words "no item scanned", and
    /// the height it occupies is worth more to the map.
    /// </remarks>
    public bool HasScan
    {
        get => _hasScan;
        private set
        {
            if (SetProperty(ref _hasScan, value))
            {
                OnPropertyChanged(nameof(HasNoScan));
            }
        }
    }

    public bool HasNoScan => !HasScan;

    public string LastScanValue
    {
        get => _lastScanValue;
        private set => SetProperty(ref _lastScanValue, value);
    }

    public string LastScanAdvice
    {
        get => _lastScanAdvice;
        private set => SetProperty(ref _lastScanAdvice, value);
    }

    public string LastScanEvidence
    {
        get => _lastScanEvidence;
        private set => SetProperty(ref _lastScanEvidence, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            await Task.Run(
                    () => _startupCoordinator.InitializeAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            ApplySnapshot(_stateStore.Current);
            await Items.InitializeAsync(cancellationToken).ConfigureAwait(true);
            await Quests.InitializeAsync(cancellationToken).ConfigureAwait(true);
            await History.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Hideout.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Ammo.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Keys.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Events.LoadAsync(cancellationToken).ConfigureAwait(true);
            _startupCoordinator.BeginBackgroundRefresh();
            // Fire and forget, deliberately. Looking for a newer build must never be something
            // startup waits on, and a check that fails is not worth reporting at launch: the
            // gateway already reports a failure next to the button for anyone who goes looking.
            _ = Settings.WatchForUpdatesAsync(_lifetime.Token);
            await Group.InitializeAsync(cancellationToken).ConfigureAwait(true);
            _initialized = true;
            await Map.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Application startup failed.");
            _stateStore.Update(current => current with
            {
                DatabaseReady = false,
                Data = current.Data with
                {
                    Availability = DataAvailability.Error,
                    Detail = $"Application startup failed: {exception.Message}",
                },
            });
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public bool Navigate(string pageName)
    {
        var target = Navigation.FirstOrDefault(item => string.Equals(item.Name, pageName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        Select(target);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateStore.Changed -= RuntimeStateChanged;
        // Stops the update watcher, which otherwise outlives the window it reports to and
        // keeps making network calls for a process on its way out.
        _lifetime.Cancel();
        _lifetime.Dispose();
        _initializationLock.Dispose();
    }

    /// <summary>
    /// Runs a scan from the global shortcut.
    /// </summary>
    /// <remarks>
    /// The event arrives on the hotkey service's own message-pump thread, so the work is
    /// posted to the UI thread. The whole point of the shortcut is that the player is still
    /// in the game, so this also brings the Scanner page forward on the second monitor
    /// without anyone having to alt-tab and click.
    /// </remarks>
    private void FollowRaidMap(ApplicationRuntimeSnapshot snapshot)
    {
        var mapId = snapshot.Raid.MapId;
        if (string.IsNullOrWhiteSpace(mapId) ||
            snapshot.Raid.IsManualMapOverride ||
            string.Equals(mapId, _followedMapId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _followedMapId = mapId;
        _ = FollowRaidMapAsync(mapId);
    }

    private async Task FollowRaidMapAsync(string mapId)
    {
        try
        {
            await Map.FollowRaidAsync(mapId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not follow the raid onto {MapId}.", mapId);
        }
    }

    private NavigationItem CreateNavigation(string name, string glyph, PageViewModel page) =>
        new(name, glyph, page, Select);

    private void Select(NavigationItem selected)
    {
        foreach (var item in Navigation)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }

        CurrentPage = selected.Page;
    }

    private void RuntimeStateChanged(object? sender, EventArgs eventArgs)
    {
        var snapshot = _stateStore.Current;
        if (_synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            ApplySnapshot(snapshot);
            return;
        }

        _synchronizationContext.Post(_ => ApplySnapshot(snapshot), null);
    }

    private void ApplySnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow();
        ModeLabel = snapshot.IsDemoMode
            ? "Deterministic local fixture · no live game access"
            : snapshot.IsOffline
                ? "Offline"
                : string.Empty;
        Status = CreateStatus(snapshot, now);
        Raid.Apply(snapshot, now);
        Scanner.Apply(snapshot.Scan);
        Items.Apply(snapshot);
        Flea.Apply(snapshot);
        Hideout.Apply(snapshot);
        Ammo.Apply(snapshot);
        Keys.Apply(snapshot);
        Loadout.Apply(snapshot);
        Events.Apply(snapshot);
        Squad.Apply(snapshot);
        Group.Apply(snapshot);
        Quests.ApplyRuntime(snapshot);
        Settings.Apply(snapshot);
        FollowRaidMap(snapshot);
        // The map's own marker comes straight off the raid snapshot, so a screenshot taken
        // mid-raid appears without the player having to touch the map page.
        Map.ShowPlayer(snapshot.Raid.LastKnownPosition, snapshot.Raid.PositionTrail);
        // The extracts a raid actually offers come from the player scanning the list, so the
        // map can mark them out from the ten it knows the map has.
        Map.ShowActiveExtracts(snapshot.Raid.ActiveExtracts);
        Map.ShowGroup(snapshot.Group.Members);
        Map.ShowGroupMarks(snapshot.Group.Waypoints, snapshot.Group.Pings);
        HasScan = snapshot.Scan.Succeeded;
        LastScanName = snapshot.Scan.Succeeded ? snapshot.Scan.ItemName ?? "Unnamed item" : "No item scanned";
        LastScanValue = snapshot.Scan.Succeeded && snapshot.Scan.ValueRoubles is { } value
            ? $"{value:N0} ₽ · {snapshot.Scan.ValuePerSlotRoubles.GetValueOrDefault():N0} ₽ per slot"
            : "Unavailable";
        LastScanAdvice = snapshot.Scan.Succeeded
            ? snapshot.Scan.Recommendation ?? "No recommendation was produced."
            : "No recommendation without observed evidence.";
        LastScanEvidence = snapshot.Scan.Detail;
    }

    /// <summary>
    /// Builds the six chips along the top of the window.
    /// </summary>
    /// <remarks>
    /// A chip is grey until it has something to say. Four of the six used to carry their
    /// accent colour permanently, so at rest the bar was as colourful as it ever got and
    /// nothing changing could stand out. Now colour means an observation: sage for something
    /// live, cyan for a place, ochre for a value or a warning, coral for a fault.
    /// </remarks>
    private static IReadOnlyList<StatusChip> CreateStatus(
        ApplicationRuntimeSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        var raid = snapshot.Raid;
        var observation = snapshot.Observation;
        var dataAge = snapshot.Data.UpdatedUtc is { } updated
            ? FormatAge(updated, nowUtc)
            : "never synced";
        var position = raid.LastKnownPosition;
        var scan = snapshot.Scan;
        return
        [
            // The EFT chip used to be hardcoded to "Not observed" whatever was happening.
            // It now reports what the observation service is actually doing.
            new(
                "EFT",
                snapshot.IsDemoMode
                    ? "Demo fixture"
                    : observation.IsObserving ? "Observing" : observation.IsSupported ? "Not found" : "Unsupported",
                snapshot.IsDemoMode ? "No live game access" : observation.Detail,
                observation.IsObserving ? SageColor : observation.IsSupported || snapshot.IsDemoMode ? RestingColor : OchreColor),
            new(
                "Map",
                raid.MapId ?? "Unknown",
                raid.MapId is null
                    ? observation.IsWatchingLogs ? "Waiting for a raid to start" : "No current raid evidence"
                    : $"{raid.Confidence.Value:P0} · {FormatAge(raid.UpdatedUtc, nowUtc)}",
                raid.MapId is null ? RestingColor : CyanColor)
            {
                IsPrimary = true,
            },
            new(
                "Raid",
                RaidStateText.Describe(raid.State),
                raid.StartedUtc is null ? "No active session" : $"Started {raid.StartedUtc.Value.ToLocalTime():T}",
                raid.State switch
                {
                    RaidLifecycleState.InRaid => SageColor,
                    RaidLifecycleState.Unknown => RestingColor,
                    _ => InkColor,
                })
            {
                IsPrimary = true,
            },
            new(
                "Position",
                position is null ? "No evidence" : string.Create(CultureInfo.InvariantCulture, $"{position.Position.X:F0}, {position.Position.Z:F0}"),
                position is null
                    ? observation.IsWatchingScreenshots
                        ? "Screenshot to place yourself"
                        : "No screenshot observation"
                    : $"Screenshot · {FormatAge(position.Timestamp, nowUtc)}",
                position is null ? RestingColor : CyanColor)
            {
                IsPrimary = true,
            },
            new(
                "Data",
                snapshot.Data.Availability.ToString(),
                $"{snapshot.Data.ItemCount:N0} items · {dataAge}",
                snapshot.Data.Availability switch
                {
                    DataAvailability.Error => CoralColor,
                    DataAvailability.Unavailable => OchreColor,
                    _ => RestingColor,
                }),
            new(
                "Scan",
                scan.IsAvailable ? scan.Succeeded ? "Observed" : "Ready" : "Unavailable",
                scan.Detail,
                scan.IsAvailable ? scan.Succeeded ? OchreColor : RestingColor : OchreColor),
        ];
    }

    private static string FormatAge(DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - observedUtc;
        if (age < TimeSpan.Zero)
        {
            return "future timestamp";
        }

        return age < TimeSpan.FromMinutes(1)
            ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
            : age < TimeSpan.FromHours(1)
                ? $"{(int)age.TotalMinutes}m ago"
                : $"{(int)age.TotalHours}h ago";
    }
}
