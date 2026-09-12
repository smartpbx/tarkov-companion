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
using TarkovCompanion.Application.Services.Input;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Input;
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

    internal NavigationItem(string name, string glyph, PageViewModel page, Action<NavigationItem> select)
    {
        Name = name;
        Glyph = glyph;
        Page = page;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Name { get; }

    public string Glyph { get; }

    public PageViewModel Page { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed record StatusChip(string Label, string Value, string Evidence, string Color);

public abstract class PageViewModel(string title, string description, string evidence) : BindableViewModel
{
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
    private readonly IRaidHistoryService? _raidHistoryService;
    private readonly List<string> _scannedThisRaid = [];
    private string _raidState = "No raid evidence";
    private string _position = "No last-known position";
    private string _extracts = "No extracts have been observed.";
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
        : base("Raid reference", "Interactive tarkov.dev maps with last-known external evidence", "No raid evidence")
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
            : $"{raid.State} · {FormatAge(raid.UpdatedUtc, nowUtc)}";
        Position = raid.LastKnownPosition is null
            ? "No last-known position; the map remains reference-only."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"X {raid.LastKnownPosition.Position.X:F1}, Y {raid.LastKnownPosition.Position.Y:F1}, Z {raid.LastKnownPosition.Position.Z:F1} · screenshot {FormatAge(raid.LastKnownPosition.Timestamp, nowUtc)}");
        Extracts = raid.ActiveExtracts.Count == 0
            ? "No extracts have been observed; none are marked active."
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
            lastInRaid.Side,
            _scannedThisRaid.ToArray(),
            lastInRaid.LastKnownPosition,
            _raidHistoryService is null
                ? "The local raid history was not read for this summary."
                : "Checking the local raid history…");
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
                ? "This raid has not been written to the local history."
                : entry.EndedUtc is { } endedUtc
                    ? string.Create(
                        CultureInfo.CurrentCulture,
                        $"Saved to the local history and closed at {endedUtc.ToLocalTime():g}.")
                    : "Saved to the local history, but its end time has not been written yet.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            history = $"The local raid history could not be read: {exception.Message}";
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
    private string _searchStatus = "Load local data, then search by item name or short name.";
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
            SearchStatus = snapshot.Data.Detail;
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
                : $"{results.Count} local result(s); no network request was made by search.";
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

    public ScannerPageViewModel(IRuntimeScanUseCase scanUseCase)
        : base("Scanner", "Dispatch a user-triggered scan through the configured use case", "Runtime state not loaded")
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
        : base("History", "Local raid rows created from external evidence transitions", "Runtime state not loaded")
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
    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly ScanHotkeyService _hotkeys;
    private string _dataStatus = "Runtime state not loaded";
    private string _profileContext = "Profile unavailable";
    private string _scanProvider = "Unavailable";
    private HotkeyBinding _pendingHotkey = HotkeyBinding.DefaultScan;
    private string _hotkeyStatus = "The scan shortcut has not been applied yet.";
    private bool _isRecordingHotkey;

    public SettingsPageViewModel(
        ApplicationStartupCoordinator startupCoordinator,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        IOcrEngineStatus ocrStatus,
        ScanHotkeyService hotkeys)
        : base("Settings & diagnostics", "Observable runtime configuration and manual data refresh", "Runtime state not loaded")
    {
        ArgumentNullException.ThrowIfNull(ocrStatus);
        ArgumentNullException.ThrowIfNull(hotkeys);
        _startupCoordinator = startupCoordinator;
        _hotkeys = hotkeys;
        // The engine explains exactly why it is unavailable - a missing Visual C++ runtime
        // reads very differently from an unsupported architecture - but until now only the
        // headless self-test ever read that reason, so the user saw a bare "Unavailable".
        RecognitionProvider = ocrStatus.Availability.IsAvailable
            ? $"Available · {ocrStatus.Availability.Provider}"
            : $"Unavailable · {ocrStatus.Availability.Provider} · {ocrStatus.Availability.Reason ?? "No reason was reported."}";
        IsOffline = options.Offline;
        DatabasePath = Path.Combine(paths.Database, "tarkov-companion.db");
        DiagnosticChannel = commandLine.DeveloperMode && !string.IsNullOrWhiteSpace(commandLine.DiagnosticChannelPath)
            ? "Requested; token validation occurs before the channel starts."
            : "Disabled (developer mode and an explicit path are required).";
        SyncCommand = new AsyncDelegateCommand(SyncAsync);
        ApplyHotkeyCommand = new AsyncDelegateCommand(ApplyHotkeyAsync);
        ResetHotkeyCommand = new AsyncDelegateCommand(ResetHotkeyAsync);
    }

    public bool IsOffline { get; }

    public string RecognitionProvider { get; }

    public string DatabasePath { get; }

    public string DiagnosticChannel { get; }

    public AsyncDelegateCommand SyncCommand { get; }

    public AsyncDelegateCommand ApplyHotkeyCommand { get; }

    public AsyncDelegateCommand ResetHotkeyCommand { get; }

    /// <summary>The shortcut shown in the editor, which may not be the active one yet.</summary>
    public HotkeyBinding PendingHotkey
    {
        get => _pendingHotkey;
        private set
        {
            if (SetProperty(ref _pendingHotkey, value))
            {
                OnPropertyChanged(nameof(PendingHotkeyDisplay));
            }
        }
    }

    public string PendingHotkeyDisplay => PendingHotkey.DisplayName;

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    public bool IsRecordingHotkey
    {
        get => _isRecordingHotkey;
        private set
        {
            if (SetProperty(ref _isRecordingHotkey, value))
            {
                OnPropertyChanged(nameof(RecordHotkeyLabel));
            }
        }
    }

    public string RecordHotkeyLabel => IsRecordingHotkey ? "Press a combination…" : "Record shortcut";

    /// <summary>Registers the stored shortcut and reports the outcome.</summary>
    public async Task InitializeHotkeyAsync(CancellationToken cancellationToken)
    {
        var state = await _hotkeys.InitializeAsync(cancellationToken).ConfigureAwait(true);
        PendingHotkey = state.Binding;
        HotkeyStatus = state.Detail;
    }

    public void BeginRecordingHotkey() => IsRecordingHotkey = true;

    public void CancelRecordingHotkey() => IsRecordingHotkey = false;

    /// <summary>Accepts a combination captured from the keyboard.</summary>
    public void RecordHotkey(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        IsRecordingHotkey = false;
        PendingHotkey = binding;
        HotkeyStatus = binding.IsValid(out var reason)
            ? $"{binding.DisplayName} is ready. Choose Apply to start using it."
            : reason;
    }

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
        ProfileContext = snapshot.Profile is null
            ? "Profile unavailable"
            : $"{snapshot.Profile.Name} · level {snapshot.Profile.Level} · {snapshot.Profile.GameMode} · updated {snapshot.Profile.UpdatedUtc.ToLocalTime():g}";
        ScanProvider = snapshot.Scan.IsAvailable
            ? snapshot.Scan.Succeeded ? $"Last result: {snapshot.Scan.Source}" : snapshot.Scan.Detail
            : snapshot.Scan.Detail;
        Evidence = snapshot.DatabaseReady ? "Persistent database initialized" : "Database not initialized";
    }

    public async Task ApplyHotkeyAsync()
    {
        var state = await _hotkeys.ApplyAsync(PendingHotkey, persist: true, CancellationToken.None)
            .ConfigureAwait(true);
        HotkeyStatus = state.Detail;
    }

    public async Task ResetHotkeyAsync()
    {
        PendingHotkey = HotkeyBinding.DefaultScan;
        await ApplyHotkeyAsync().ConfigureAwait(true);
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
    private readonly IRuntimeStateStore _stateStore;
    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly ScanHotkeyService _hotkeys;
    private string? _followedMapId;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private PageViewModel _currentPage;
    private IReadOnlyList<StatusChip> _status = [];
    private string _modeLabel = "External read-only companion";
    private string _lastScanName = "No item scanned";
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
        ScanHotkeyService hotkeys,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        MapViewModel map,
        QuestsPageViewModel quests,
        TimeProvider timeProvider,
        ILogger<MainWindowViewModel> logger)
    {
        _stateStore = stateStore;
        _startupCoordinator = startupCoordinator;
        _hotkeys = hotkeys;
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
        Settings = new(startupCoordinator, options, paths, commandLine, ocrStatus, hotkeys);
        Ammo = new(itemFactCatalog, itemRepository);
        Keys = new(itemFactCatalog, itemRepository);
        Loadout = new(itemFactCatalog, itemSearchService, itemRepository);
        Events = new(eventCatalog, eventTracker, itemRepository);
        Squad = new(itemRepository);
        _hotkeys.Triggered += ScanHotkeyPressed;

        Navigation =
        [
            CreateNavigation("Raid", "⌖", Raid),
            CreateNavigation("Squad", "⚇", Squad),
            CreateNavigation("Scanner", "⌁", Scanner),
            CreateNavigation("Items", "◇", Items),
            CreateNavigation("Ammo", "◉", Ammo),
            CreateNavigation("Keys", "⌑", Keys),
            CreateNavigation("Flea", "₽", Flea),
            CreateNavigation("Quests", "✓", Quests),
            CreateNavigation("Hideout", "⌂", Hideout),
            CreateNavigation("Events", "⚑", Events),
            CreateNavigation("Loadout", "▦", Loadout),
            CreateNavigation("History", "◷", History),
            CreateNavigation("Settings", "⚙", Settings),
        ];

        _currentPage = Navigation[0].Page;
        Navigation[0].IsSelected = true;
        _stateStore.Changed += RuntimeStateChanged;
        ApplySnapshot(_stateStore.Current);
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

    public IReadOnlyList<StatusChip> Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

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
            await Settings.InitializeHotkeyAsync(cancellationToken).ConfigureAwait(true);
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
        _hotkeys.Triggered -= ScanHotkeyPressed;
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
    private void ScanHotkeyPressed(object? sender, EventArgs arguments)
    {
        if (_disposed)
        {
            return;
        }

        if (_synchronizationContext is null)
        {
            RunScanFromHotkey();
            return;
        }

        _synchronizationContext.Post(_ => RunScanFromHotkey(), null);
    }

    private void RunScanFromHotkey()
    {
        if (_disposed)
        {
            return;
        }

        Navigate("Scanner");
        if (Scanner.ScanCommand.CanExecute(null))
        {
            Scanner.ScanCommand.Execute(null);
        }
    }

    /// <summary>
    /// Moves the map to whichever raid the player is now in.
    /// </summary>
    /// <remarks>
    /// This is the payoff of watching the game's log folder: the companion switches maps on
    /// its own, which is the whole reason not to alt-tab mid-raid. It fires only when the
    /// observed map actually changes, so it never fights a map the player chose by hand.
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
                ? "External read-only companion · offline"
                : "External read-only companion";
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
        Quests.ApplyRuntime(snapshot);
        Settings.Apply(snapshot);
        FollowRaidMap(snapshot);
        // The map's own marker comes straight off the raid snapshot, so a screenshot taken
        // mid-raid appears without the player having to touch the map page.
        Map.ShowPlayer(snapshot.Raid.LastKnownPosition);
        LastScanName = snapshot.Scan.Succeeded ? snapshot.Scan.ItemName ?? "Unnamed item" : "No item scanned";
        LastScanValue = snapshot.Scan.Succeeded && snapshot.Scan.ValueRoubles is { } value
            ? $"{value:N0} ₽ · {snapshot.Scan.ValuePerSlotRoubles.GetValueOrDefault():N0} ₽ per slot"
            : "Unavailable";
        LastScanAdvice = snapshot.Scan.Succeeded
            ? snapshot.Scan.Recommendation ?? "No recommendation was produced."
            : "No recommendation without observed evidence.";
        LastScanEvidence = snapshot.Scan.Detail;
    }

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
                observation.IsObserving ? "#77B895" : "#8F9BA6"),
            new(
                "Map",
                raid.MapId ?? "Unknown",
                raid.MapId is null
                    ? observation.IsWatchingLogs ? "Waiting for a raid to start" : "No current raid evidence"
                    : $"{raid.Confidence.Value:P0} · {FormatAge(raid.UpdatedUtc, nowUtc)}",
                "#56B8C6"),
            new(
                "Raid",
                raid.State.ToString(),
                raid.StartedUtc is null ? "No active session" : $"Started {raid.StartedUtc.Value.ToLocalTime():T}",
                "#C6A15B"),
            new(
                "Position",
                position is null ? "No evidence" : string.Create(CultureInfo.InvariantCulture, $"{position.Position.X:F0}, {position.Position.Z:F0}"),
                position is null
                    ? observation.IsWatchingScreenshots
                        ? "Take a screenshot in game to place yourself"
                        : "No screenshot observation"
                    : $"Screenshot · {FormatAge(position.Timestamp, nowUtc)}",
                "#56B8C6"),
            new(
                "Data",
                snapshot.Data.Availability.ToString(),
                $"{snapshot.Data.ItemCount:N0} items · {dataAge}",
                "#77B895"),
            new(
                "Scan",
                scan.IsAvailable ? scan.Succeeded ? "Observed" : "Ready" : "Unavailable",
                scan.Detail,
                "#C6A15B"),
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
