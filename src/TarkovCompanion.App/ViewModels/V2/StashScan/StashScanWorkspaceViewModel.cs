using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.App.ViewModels.V2.StashScan;

/// <summary>One captured snapshot, in the picker.</summary>
public sealed record StashSnapshotRowViewModel(
    Guid SnapshotId,
    string RecordedLabel,
    bool IsCurrent,
    string CoverageLabel,
    string StatusLabel)
{
    public ICommand? SelectCommand { get; init; }

    public bool IsSelected { get; init; }
}

/// <summary>A scan target chip: what the next guided capture is for.</summary>
public sealed class StashScanTargetViewModel : BindableViewModel
{
    private bool _isSelected;

    public StashScanTargetViewModel(ScanIntent intent, string label, Action<ScanIntent> select)
    {
        Intent = intent;
        Label = label;
        SelectCommand = new DelegateCommand(() => select(intent));
    }

    public ScanIntent Intent { get; }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One recognized item, shown under its rough Keep/Sell/Use soon/Review group.</summary>
public sealed record StashItemRowViewModel(
    string ItemKey,
    string DisplayName,
    string ContainerPath,
    string QuantityLabel,
    string EvidenceLabel,
    StashPlanGroup Group)
{
    public string GroupLabel => Group.ToString();

    public ICommand? SelectCommand { get; init; }

    /// <summary>The catalog's wiki link for this item, when it has one.</summary>
    public string? WikiUri { get; init; }

    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(WikiUri);

    public ICommand? OpenWikiCommand { get; init; }
}

/// <summary>
/// One captured container, drawn as the grid it was read from: every footprint at its own anchor,
/// on a fixed square size the view scales down as a whole.
/// </summary>
public sealed class StashRegionViewModel
{
    public const double CellSize = 56;

    public StashRegionViewModel(string title, int? rows, int? columns, IReadOnlyList<StashGridTileViewModel> tiles)
    {
        Title = title;
        Tiles = tiles;
        var extentRows = tiles.Select(tile => tile.Row + tile.HeightCells).DefaultIfEmpty(1).Max();
        var extentColumns = tiles.Select(tile => tile.Column + tile.WidthCells).DefaultIfEmpty(1).Max();
        Rows = Math.Max(rows ?? extentRows, extentRows);
        Columns = Math.Max(columns ?? extentColumns, extentColumns);
        SizeLabel = rows is null || columns is null
            ? $"{tiles.Count.ToString(CultureInfo.CurrentCulture)} stacks"
            : $"{Columns.ToString(CultureInfo.CurrentCulture)} × {Rows.ToString(CultureInfo.CurrentCulture)} squares";
    }

    public string Title { get; }

    public int Rows { get; }

    public int Columns { get; }

    public string SizeLabel { get; }

    public double PixelWidth => Columns * CellSize;

    public double PixelHeight => Rows * CellSize;

    /// <summary>How far the view may scale a small container up before its squares stop reading as squares.</summary>
    public double MaxPixelWidth => PixelWidth * 1.15;

    public IReadOnlyList<StashGridTileViewModel> Tiles { get; }
}

/// <summary>One footprint on a reconstructed container grid.</summary>
public sealed class StashGridTileViewModel : BindableViewModel
{
    private const double Gap = 2;
    private bool _isSelected;

    public StashGridTileViewModel(
        GridCellAddress anchor,
        int widthCells,
        int heightCells,
        string name,
        string detail,
        StashTileKind kind,
        StashItemRowViewModel? row)
    {
        Row = anchor.Row;
        Column = anchor.Column;
        WidthCells = Math.Max(1, widthCells);
        HeightCells = Math.Max(1, heightCells);
        Name = name;
        Detail = detail;
        Kind = kind;
        ItemRow = row;
        SelectCommand = new DelegateCommand(() => row?.SelectCommand?.Execute(null));
    }

    public int Row { get; }

    public int Column { get; }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public double Left => (Column * StashRegionViewModel.CellSize) + Gap;

    public double Top => (Row * StashRegionViewModel.CellSize) + Gap;

    public double Width => (WidthCells * StashRegionViewModel.CellSize) - (2 * Gap);

    public double Height => (HeightCells * StashRegionViewModel.CellSize) - (2 * Gap);

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public StashTileKind Kind { get; }

    public StashItemRowViewModel? ItemRow { get; }

    public string ItemKey => ItemRow?.ItemKey ?? string.Empty;

    public ICommand SelectCommand { get; }

    public bool IsAmmo => Kind == StashTileKind.Ammo;

    public bool IsKey => Kind == StashTileKind.Key;

    public bool IsUnresolved => Kind == StashTileKind.Unresolved;

    public string AutomationName
    {
        get
        {
            // The tile may draw a bare "?"; a screen reader gets the row's words instead.
            var name = ItemRow?.DisplayName ?? Name;
            return HasDetail ? $"{name}, {Detail}" : name;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public enum StashTileKind
{
    Item = 1,
    Ammo,
    Key,
    Unresolved,
}

/// <summary>A Keep / Sell / Use soon / Review tile over the sort plan.</summary>
public sealed record StashPlanTileViewModel(StashPlanGroup Group, string Label, int Count, bool IsWired)
{
    public string CountLabel => IsWired ? Count.ToString(CultureInfo.CurrentCulture) : "—";

    public bool IsKeep => Group == StashPlanGroup.Keep;

    public bool IsSell => Group == StashPlanGroup.Sell;

    public bool IsUseSoon => Group == StashPlanGroup.UseSoon;

    public bool IsReview => Group == StashPlanGroup.Review;
}

public sealed record StashAmmoSummaryRowViewModel(string Caliber, int RoundCount, int StackCount)
{
    public string RoundsLabel => $"{RoundCount.ToString(CultureInfo.CurrentCulture)} rounds";

    public string StacksLabel => $"{StackCount.ToString(CultureInfo.CurrentCulture)} stacks";
}

public sealed record StashKeySummaryRowViewModel(
    string DisplayName,
    int Duplicates,
    string MapLabel,
    string QuestUseLabel)
{
    public string DuplicatesLabel => $"{Duplicates.ToString(CultureInfo.CurrentCulture)} in stash";
}

public sealed record StashReviewCommandRowViewModel(string Action, string Target, string CreatedLabel, string? Reason)
{
    public string Summary => $"{Action} — {Target} · {CreatedLabel}";
}

/// <summary>
/// Browses and manages the stash-scan backend (#370) from V2: pick a snapshot, see its totals,
/// coverage, ammo and key summaries, correct or rescan an item, compare to a previous snapshot,
/// or delete one. Starting a new capture session is the shell's existing capture chrome, armed
/// with the requested intent; this workspace does not itself talk to the game.
/// </summary>
public sealed class StashScanWorkspaceViewModel : BindableViewModel
{
    private readonly IStashSnapshotStore _store;
    private readonly StashScanWorkflow _workflow;
    private readonly InMemoryStashReviewCommandSink _reviewCommands;
    private readonly IItemFactCatalog _catalog;
    private readonly IRuntimeStateStore _runtime;
    private readonly TimeProvider _clock;
    // Optional so a composition without an item catalog is still a valid composition: without
    // one, results rows have no wiki link, which is what they had until now.
    private readonly IItemRepository? _itemRepository;
    private readonly IWikiLinkOpener? _wikiOpener;
    // [V2 rough package 40] Optional for the same reason: without them a Stash scan is one
    // screenshot through the capture dialog, which is what it was before.
    private readonly GuidedStashScanService? _guidedScan;
    private readonly GuidedStashScanArming? _arming;
    private readonly IProfileRuntimeContextService? _profileContext;
    private readonly StashReconstructionProjector _projector = new();
    private StashReconstruction _reconstruction = StashReconstruction.Empty;
    private IReadOnlyDictionary<string, AmmoStats>? _ammoByItemId;
    private IReadOnlyDictionary<string, KeyFacts>? _keyFactsByItemId;
    private StashSnapshotRecord? _selected;
    private string _status = "Stash snapshots have not been loaded.";
    private string _identityCorrection = string.Empty;
    private string _quantityCorrection = string.Empty;
    private StashItemRowViewModel? _selectedItem;
    private ScanIntent _scanTarget = ScanIntent.Stash;
    private bool _isGridView = true;

    public StashScanWorkspaceViewModel(
        IStashSnapshotStore store,
        StashScanWorkflow workflow,
        InMemoryStashReviewCommandSink reviewCommands,
        IItemFactCatalog catalog,
        IRuntimeStateStore runtime,
        TimeProvider? clock = null,
        IItemRepository? itemRepository = null,
        IWikiLinkOpener? wikiOpener = null,
        GuidedStashScanService? guidedScan = null,
        GuidedStashScanArming? arming = null,
        IProfileRuntimeContextService? profileContext = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _reviewCommands = reviewCommands ?? throw new ArgumentNullException(nameof(reviewCommands));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? TimeProvider.System;
        _itemRepository = itemRepository;
        _wikiOpener = wikiOpener;
        _guidedScan = guidedScan;
        _arming = arming;
        _profileContext = profileContext;
        if (_guidedScan is not null)
        {
            _guidedScan.Changed += OnGuidedScanChanged;
        }

        if (_arming is not null)
        {
            _arming.Changed += OnGuidedScanChanged;
        }

        FinishScanCommand = new AsyncDelegateCommand(FinishScanAsync);
        UndoLastScreenshotCommand = new AsyncDelegateCommand(UndoLastScreenshotAsync);
        DiscardScanCommand = new AsyncDelegateCommand(DiscardScanAsync);
        ContinueScanCommand = new DelegateCommand(() => _arming?.Resume());

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        DeleteSelectedCommand = new AsyncDelegateCommand(DeleteSelectedAsync);
        CompareToPreviousCommand = new AsyncDelegateCommand(CompareToPreviousAsync);
        MarkSelectedUnknownCommand = new AsyncDelegateCommand(MarkSelectedUnknownAsync);
        CorrectIdentityCommand = new AsyncDelegateCommand(CorrectIdentityAsync);
        CorrectQuantityCommand = new AsyncDelegateCommand(CorrectQuantityAsync);
        StartFullScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Stash));
        StartAmmoScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Ammo));
        StartKeysScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Keys));
        StartSelectedScanCommand = new AsyncDelegateCommand(StartSelectedScanAsync);
        ShowGridCommand = new DelegateCommand(() => IsGridView = true);
        ShowListCommand = new DelegateCommand(() => IsGridView = false);
        ScanTargets =
        [
            new StashScanTargetViewModel(ScanIntent.Stash, "Full stash", SelectScanTarget) { IsSelected = true },
            new StashScanTargetViewModel(ScanIntent.Ammo, "Ammo", SelectScanTarget),
            new StashScanTargetViewModel(ScanIntent.Keys, "Keys", SelectScanTarget),
        ];
    }

    /// <summary>What the next guided capture is for; the chips above the scan button pick it.</summary>
    public ScanIntent ScanTarget
    {
        get => _scanTarget;
        private set => SetProperty(ref _scanTarget, value);
    }

    public IReadOnlyList<StashScanTargetViewModel> ScanTargets { get; }

    public ICommand StartSelectedScanCommand { get; }

    public ICommand FinishScanCommand { get; }

    public ICommand UndoLastScreenshotCommand { get; }

    public ICommand DiscardScanCommand { get; }

    public ICommand ContinueScanCommand { get; }

    /// <summary>A guided full-stash scan is collecting screenshots, in this run or a previous one.</summary>
    public bool IsScanInProgress => _guidedScan?.Current.IsCollecting == true;

    public bool IsScanIdle => !IsScanInProgress;

    /// <summary>The scan is waiting, but screenshots are not being taken as stash screenshots right now.</summary>
    public bool IsScanPaused => IsScanInProgress && _arming?.IsActive != true;

    public bool CanFinishScan => IsScanInProgress && _guidedScan!.Current.Screenshots > 0;

    public string GuidedHeadline => _guidedScan?.Current.Headline ?? string.Empty;

    public string GuidedNextStep => IsScanPaused
        ? PausedNextStep(_guidedScan!.Current)
        : _guidedScan?.Current.NextStep ?? string.Empty;

    private static string PausedNextStep(GuidedStashScanProgress progress) => progress.Screenshots == 0
        ? "An unfinished scan is waiting. Press Keep going, then scroll to the top of your stash and take a screenshot."
        : $"An unfinished scan is waiting: {progress.Screenshots.ToString(CultureInfo.CurrentCulture)} screenshot{(progress.Screenshots == 1 ? string.Empty : "s")}, rows 1–{progress.RowsCovered.ToString(CultureInfo.CurrentCulture)}. Keep going, finish with what you have, or discard it.";

    public ICommand ShowGridCommand { get; }

    public ICommand ShowListCommand { get; }

    /// <summary>The reconstructed stash is shown as its grids by default, or as a plain list.</summary>
    public bool IsGridView
    {
        get => _isGridView;
        private set
        {
            if (SetProperty(ref _isGridView, value))
            {
                OnPropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsListView => !IsGridView;

    public IReadOnlyList<StashRegionViewModel> Regions { get; private set; } = [];

    public bool HasRegions => Regions.Count > 0;

    public IReadOnlyList<StashPlanTileViewModel> PlanTiles { get; private set; } = [];

    /// <summary>"218 stacks · 3 cells unresolved", or just the stack count when the scan read every cell.</summary>
    public string ReconstructionLabel
    {
        get
        {
            if (_selected is null && !IsScanInProgress)
            {
                return string.Empty;
            }

            // Counted from the one reconstructed grid, so a row two screenshots share is one row.
            var culture = CultureInfo.CurrentCulture;
            var label = $"{_reconstruction.KnownTiles.ToString(culture)} named";
            if (_reconstruction.UnknownTiles > 0)
            {
                label += $" · {_reconstruction.UnknownTiles.ToString(culture)} unknown";
            }

            if (_reconstruction.UnplacedRegions > 0)
            {
                label += $" · {_reconstruction.UnplacedRegions.ToString(culture)} screenshot{(_reconstruction.UnplacedRegions == 1 ? string.Empty : "s")} not placed";
            }

            return IsScanInProgress ? $"Scanning · {label}" : label;
        }
    }

    /// <summary>The snapshot's known value, compactly: "₽18.6M". Empty when no price was read.</summary>
    public string StashValueLabel
    {
        get
        {
            // A scan that priced nothing sums to zero, and "₽0" reads as "worthless" rather
            // than "no price was read", so zero is shown as absent too.
            var value = _selected?.Recognition.Result.Value?.TotalKnownValueRoubles.Value;
            return value is null or 0 ? string.Empty : CompactRoubles(value.Value);
        }
    }

    public bool HasStashValue => StashValueLabel.Length > 0;

    public string UpdatedLabel => _selected is null ? string.Empty : $"Scanned {RecordedLabel}";

    public string RecordedLabel => _selected is null
        ? string.Empty
        : _selected.RecordedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string AmmoCountLabel => $"{AmmoSummary.Count.ToString(CultureInfo.CurrentCulture)} calibres";

    public string KeyCountLabel => $"{KeySummary.Count.ToString(CultureInfo.CurrentCulture)} keys";

    public string ItemCountLabel => $"{Items.Count.ToString(CultureInfo.CurrentCulture)} items";

    private void SelectScanTarget(ScanIntent intent)
    {
        ScanTarget = intent;
        foreach (var target in ScanTargets)
        {
            target.IsSelected = target.Intent == intent;
        }
    }

    private static string CompactRoubles(long value) => Math.Abs(value) switch
    {
        >= 1_000_000 => "₽" + (value / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 10_000 => "₽" + (value / 1_000d).ToString("0", CultureInfo.CurrentCulture) + "k",
        >= 1_000 => "₽" + (value / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "k",
        _ => "₽" + value.ToString("N0", CultureInfo.CurrentCulture),
    };

    /// <summary>Raised when the player asks to start a guided capture session for this workspace.</summary>
    /// <remarks>
    /// Arming and running the session is the shell's existing capture chrome
    /// (<c>V2ShellViewModel.CaptureIntents</c> / <c>ArmCaptureCommand</c>); this workspace only
    /// names which intent to arm.
    /// </remarks>
    public event EventHandler<ScanIntent>? ScanRequested;

    public IReadOnlyList<StashSnapshotRowViewModel> Snapshots { get; private set; } = [];

    public IReadOnlyList<StashItemRowViewModel> Items { get; private set; } = [];

    public IReadOnlyList<StashAmmoSummaryRowViewModel> AmmoSummary { get; private set; } = [];

    public IReadOnlyList<StashKeySummaryRowViewModel> KeySummary { get; private set; } = [];

    public IReadOnlyList<StashReviewCommandRowViewModel> PendingCorrections { get; private set; } = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasSnapshots => Snapshots.Count > 0;

    public bool HasNoSnapshots => !HasSnapshots;

    /// <summary>Nothing saved and nothing being scanned: the only time the grid has nothing to draw.</summary>
    public bool ShowsNothingScanned => HasNoSnapshots && !(IsScanInProgress && HasRegions);

    public bool HasSelection => _selected is not null;

    public bool HasAmmoSummary => AmmoSummary.Count > 0;

    public bool HasNoAmmoSummary => !HasAmmoSummary;

    public bool HasKeySummary => KeySummary.Count > 0;

    public bool HasNoKeySummary => !HasKeySummary;

    public bool HasPendingCorrections => PendingCorrections.Count > 0;

    public bool HasSelectedItem => SelectedItem is not null;

    /// <summary>
    /// Never null so the "Correct the selected item" card can bind it directly: that card's
    /// bindings evaluate even while <see cref="HasSelectedItem"/> keeps it hidden, and a gallery
    /// walk that visits every state without ever selecting an item would otherwise bind
    /// <c>SelectedItem.DisplayName</c> against a null <see cref="SelectedItem"/>.
    /// </summary>
    public string SelectedItemDisplayName => SelectedItem?.DisplayName ?? string.Empty;

    public string TotalsLabel => _selected is null
        ? string.Empty
        : $"{_selected.Recognition.Result.Value!.TotalKnownValueRoubles.Value?.ToString("N0", CultureInfo.CurrentCulture) ?? "unknown"} roubles known · " +
          $"{_selected.Recognition.Result.Value!.UnresolvedCells.Value?.ToString(CultureInfo.CurrentCulture) ?? "unknown"} cells unresolved";

    public string CoverageLabel => _selected is null
        ? string.Empty
        : string.Join(" · ", _selected.Recognition.Result.Value!.Coverage.Select(CoverageDescription));

    public string RecommendationNotice { get; } = "Sorting into Keep/Sell/Use soon isn't wired yet — shown under Review.";

    public string CorrectionsNotice { get; } = "Corrections apply only to this session for now.";

    public string IdentityCorrection
    {
        get => _identityCorrection;
        set => SetProperty(ref _identityCorrection, value);
    }

    public string QuantityCorrection
    {
        get => _quantityCorrection;
        set => SetProperty(ref _quantityCorrection, value);
    }

    public StashItemRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                foreach (var tile in Regions.SelectMany(region => region.Tiles))
                {
                    tile.IsSelected = value is not null &&
                        string.Equals(tile.ItemKey, value.ItemKey, StringComparison.Ordinal);
                }

                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(SelectedItemDisplayName));
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand CompareToPreviousCommand { get; }

    public ICommand MarkSelectedUnknownCommand { get; }

    public ICommand CorrectIdentityCommand { get; }

    public ICommand CorrectQuantityCommand { get; }

    public ICommand StartFullScanCommand { get; }

    public ICommand StartAmmoScanCommand { get; }

    public ICommand StartKeysScanCommand { get; }

    private void RequestScan(ScanIntent intent) => ScanRequested?.Invoke(this, intent);

    /// <summary>
    /// A full-stash scan is a scroll-through, so it starts a guided scan; ammo and key scans are
    /// still one screenshot through the shared capture dialog.
    /// </summary>
    private async Task StartSelectedScanAsync()
    {
        if (ScanTarget != ScanIntent.Stash || _guidedScan is null || CurrentScope() is not { } scope)
        {
            RequestScan(ScanTarget);
            return;
        }

        await _guidedScan
            .StartAsync(
                scope,
                _profileContext?.Current.ActiveProfile?.Context.DataSnapshot.SnapshotId ?? "unversioned",
                CancellationToken.None)
            .ConfigureAwait(true);
        _arming?.Resume();
    }

    private async Task FinishScanAsync()
    {
        if (_guidedScan is null)
        {
            return;
        }

        var finished = await _guidedScan.FinishAsync(CancellationToken.None).ConfigureAwait(true);
        if (finished is null)
        {
            return;
        }

        _selected = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        Status = $"Scan saved. {finished.OwnedCounts.Summary}";
    }

    private async Task UndoLastScreenshotAsync()
    {
        if (_guidedScan is not null)
        {
            await _guidedScan.UndoLastAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task DiscardScanAsync()
    {
        if (_guidedScan is null)
        {
            return;
        }

        await _guidedScan.DiscardAsync(CancellationToken.None).ConfigureAwait(true);
        _selected = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>A screenshot landed, or the scan started, paused or ended: redraw from it.</summary>
    private void OnGuidedScanChanged(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnGuidedScanChanged(sender, eventArgs));
            return;
        }

        _ = ShowScanProgressAsync();
    }

    private async Task ShowScanProgressAsync()
    {
        await OverlayScanProgressAsync(CancellationToken.None).ConfigureAwait(true);
        RaiseAll();
    }

    /// <summary>While a scan is collecting, the grid shows that scan rather than the last saved one.</summary>
    private async Task OverlayScanProgressAsync(CancellationToken cancellationToken)
    {
        if (_guidedScan is { Current.IsCollecting: true } guided)
        {
            await BuildItemBreakdownAsync(guided.Current.Reconstruction, cancellationToken).ConfigureAwait(true);
            Status = IsScanPaused ? "A stash scan is waiting to be finished." : "Scanning your stash.";
        }
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var scope = CurrentScope();
        if (scope is null)
        {
            Snapshots = [];
            Status = "No profile is loaded yet, so there is no stash scope to browse.";
            RaiseAll();
            return;
        }

        try
        {
            // An unfinished scan from a previous run is read back here, the first time the
            // workspace is opened, and waits paused until the player says to keep going.
            if (_guidedScan is not null)
            {
                await _guidedScan.InitializeAsync(cancellationToken).ConfigureAwait(true);
            }

            _ammoByItemId ??= (await _catalog.GetAmmoAsync(cancellationToken).ConfigureAwait(true))
                .ToDictionary(ammo => ammo.ItemId, StringComparer.Ordinal);
            _keyFactsByItemId ??= (await _catalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(true))
                .ToDictionary(key => key.ItemId, StringComparer.Ordinal);

            var summaries = await _store.ListAsync(scope, 25, cancellationToken).ConfigureAwait(true);
            Snapshots = summaries
                .Select(summary => new StashSnapshotRowViewModel(
                    summary.SnapshotId,
                    summary.RecordedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    summary.IsCurrent,
                    summary.Coverage.Description ?? "Coverage not described",
                    summary.Status.Completeness.ToString())
                {
                    SelectCommand = new AsyncDelegateCommand(() => SelectSnapshotAsync(summary.SnapshotId, CancellationToken.None)),
                    IsSelected = _selected?.SnapshotId == summary.SnapshotId,
                })
                .ToArray();
            Status = Snapshots.Count switch
            {
                0 => "No stash snapshots yet. Start a scan to build the first one.",
                1 => "1 snapshot.",
                var count => $"{count.ToString(CultureInfo.CurrentCulture)} snapshots.",
            };

            if (_selected is null)
            {
                var current = summaries.FirstOrDefault(summary => summary.IsCurrent) ?? summaries.FirstOrDefault();
                if (current is not null)
                {
                    await SelectSnapshotAsync(current.SnapshotId, cancellationToken).ConfigureAwait(true);
                    return;
                }
            }

            await OverlayScanProgressAsync(cancellationToken).ConfigureAwait(true);
            RaiseAll();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Snapshots = [];
            Status = $"Stash snapshots unavailable: {exception.Message}";
            RaiseAll();
        }
    }

    public async Task SelectSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var scope = CurrentScope();
        if (scope is null)
        {
            return;
        }

        var record = await _store.ReadAsync(scope, snapshotId, cancellationToken).ConfigureAwait(true);
        if (record is null)
        {
            Status = "That snapshot no longer exists.";
            _selected = null;
            Items = [];
            Regions = [];
            AmmoSummary = [];
            KeySummary = [];
            RaiseAll();
            return;
        }

        _selected = record;
        await BuildItemBreakdownAsync(record, cancellationToken).ConfigureAwait(true);
        await OverlayScanProgressAsync(cancellationToken).ConfigureAwait(true);
        PendingCorrections = record.Recognition.Result.Value is { } recognized
            ? ReviewCommandsFor(recognized.SnapshotId)
            : [];
        Snapshots = Snapshots
            .Select(row => row with { IsSelected = row.SnapshotId == snapshotId })
            .ToArray();
        RaiseAll();
    }

    private async Task DeleteSelectedAsync()
    {
        var scope = CurrentScope();
        if (scope is null || _selected is null)
        {
            return;
        }

        var deletedId = _selected.SnapshotId;
        var result = await _workflow.DeleteAsync(scope, deletedId, CancellationToken.None).ConfigureAwait(true);
        _selected = null;
        Status = result.Deleted
            ? "Snapshot deleted."
            : "That snapshot was already gone.";
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task CompareToPreviousAsync()
    {
        var scope = CurrentScope();
        if (scope is null || _selected is null)
        {
            return;
        }

        var previous = Snapshots.FirstOrDefault(row => row.SnapshotId != _selected.SnapshotId);
        if (previous is null)
        {
            Status = "There is no earlier snapshot to compare against.";
            return;
        }

        var comparison = await _workflow.CompareCurrentToAsync(
            scope,
            previous.SnapshotId,
            _clock.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(true);
        Status = comparison is null
            ? "That comparison could not be read."
            : $"{comparison.Changes.Count} change(s) since {previous.RecordedLabel}.";
    }

    private async Task MarkSelectedUnknownAsync()
    {
        if (_selected is null || SelectedItem is null)
        {
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            SelectedRecognitionSnapshotId!,
            StashReviewActionKind.CorrectItemIdentity,
            [SelectedItem.ItemKey],
            _clock.GetUtcNow(),
            "v2.stash-workspace",
            correctedItemId: "unknown",
            reason: "Marked unknown from the V2 stash workspace.")).ConfigureAwait(true);
    }

    private async Task CorrectIdentityAsync()
    {
        if (_selected is null || SelectedItem is null || string.IsNullOrWhiteSpace(IdentityCorrection))
        {
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            SelectedRecognitionSnapshotId!,
            StashReviewActionKind.CorrectItemIdentity,
            [SelectedItem.ItemKey],
            _clock.GetUtcNow(),
            "v2.stash-workspace",
            correctedItemId: IdentityCorrection.Trim())).ConfigureAwait(true);
        IdentityCorrection = string.Empty;
    }

    private async Task CorrectQuantityAsync()
    {
        if (_selected is null || SelectedItem is null ||
            !int.TryParse(QuantityCorrection, NumberStyles.Integer, CultureInfo.CurrentCulture, out var quantity) ||
            quantity < 1)
        {
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            SelectedRecognitionSnapshotId!,
            StashReviewActionKind.CorrectQuantity,
            [SelectedItem.ItemKey],
            _clock.GetUtcNow(),
            "v2.stash-workspace",
            correctedQuantity: quantity)).ConfigureAwait(true);
        QuantityCorrection = string.Empty;
    }

    private async Task SubmitReviewAsync(StashReviewCommand command)
    {
        try
        {
            await _workflow.ReviewAsync(command, CancellationToken.None).ConfigureAwait(true);
            Status = "Correction recorded for this session.";
        }
        catch (InvalidOperationException exception)
        {
            Status = exception.Message;
            return;
        }

        if (SelectedRecognitionSnapshotId is { } snapshotId)
        {
            PendingCorrections = ReviewCommandsFor(snapshotId);
            OnPropertyChanged(nameof(PendingCorrections));
            OnPropertyChanged(nameof(HasPendingCorrections));
        }
    }

    /// <summary>
    /// The recognition-level snapshot id a <see cref="StashReviewCommand"/> targets, distinct from
    /// the durable <see cref="StashSnapshotRecord.SnapshotId"/> used to read, delete or compare
    /// records in <see cref="IStashSnapshotStore"/>.
    /// </summary>
    private string? SelectedRecognitionSnapshotId => _selected?.Recognition.Result.Value?.SnapshotId;

    private IReadOnlyList<StashReviewCommandRowViewModel> ReviewCommandsFor(string snapshotId) =>
        _reviewCommands.List(snapshotId)
            .Select(command => new StashReviewCommandRowViewModel(
                command.Action.ToString(),
                string.Join(", ", command.TargetItemKeys),
                command.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                command.Reason))
            .ToArray();

    private Task BuildItemBreakdownAsync(StashSnapshotRecord record, CancellationToken cancellationToken) =>
        BuildItemBreakdownAsync(_projector.Project(record.Recognition.Result.Value!), cancellationToken);

    /// <summary>
    /// Draws the stash from its one reconstructed grid rather than screenshot by screenshot.
    /// </summary>
    /// <remarks>
    /// Listing each captured region separately showed every shared row twice - 36 extra items on
    /// the measured three-screen scan - and dropped every cell without a name. A tile nobody could
    /// name is now drawn where it is, as unknown.
    /// </remarks>
    private async Task BuildItemBreakdownAsync(StashReconstruction reconstruction, CancellationToken cancellationToken)
    {
        _reconstruction = reconstruction;
        var ammoByItemId = _ammoByItemId ?? new Dictionary<string, AmmoStats>(StringComparer.Ordinal);
        var keyFactsByItemId = _keyFactsByItemId ?? new Dictionary<string, KeyFacts>(StringComparer.Ordinal);
        var wikiUriByItemId = new Dictionary<string, string?>(StringComparer.Ordinal);

        var items = new List<StashItemRowViewModel>();
        var regions = new List<StashRegionViewModel>();
        var ammoRounds = new Dictionary<string, (int Rounds, int Stacks)>(StringComparer.Ordinal);
        var keyOccurrences = new Dictionary<string, (string DisplayName, int Duplicates)>(StringComparer.Ordinal);

        foreach (var container in reconstruction.Containers)
        {
            var tiles = new List<StashGridTileViewModel>();
            foreach (var tile in container.Tiles)
            {
                var canonicalId = tile.ItemId;
                var displayName = tile.DisplayName
                    ?? (tile.CandidateNames.Count > 0 ? $"{tile.CandidateNames[0]}?" : "Unknown item");

                // A one-cell tile has room for about six characters, and "Unknown" is seven.
                var tileName = tile.IsKnown || tile.CandidateNames.Count > 0 ? displayName : "?";
                var quantity = tile.Quantity ?? 1;

                var wikiUri = await WikiUriForAsync(canonicalId, wikiUriByItemId, cancellationToken).ConfigureAwait(true);
                var bare = new StashItemRowViewModel(
                    tile.ItemKey,
                    displayName,
                    container.ContainerPath,
                    quantity == 1 ? "x1" : $"x{quantity.ToString(CultureInfo.CurrentCulture)}",
                    DescribeProvenance(tile.Provenance),
                    StashPlanGroup.Review)
                {
                    WikiUri = wikiUri,
                };
                var row = bare with
                {
                    SelectCommand = new DelegateCommand(() => SelectedItem = bare),
                    OpenWikiCommand = new DelegateCommand(() => _wikiOpener?.TryOpen(wikiUri)),
                };

                // Every read footprint is drawn on its container's grid, including the ammo and
                // key stacks the summaries below fold together rather than list.
                var kind = StashTileKind.Item;
                if (canonicalId is null)
                {
                    kind = StashTileKind.Unresolved;
                }
                else if (ammoByItemId.TryGetValue(canonicalId, out var ammo))
                {
                    var running = ammoRounds.GetValueOrDefault(ammo.Caliber);
                    ammoRounds[ammo.Caliber] = (running.Rounds + quantity, running.Stacks + 1);
                    kind = StashTileKind.Ammo;
                }
                else if (keyFactsByItemId.TryGetValue(canonicalId, out _))
                {
                    var running = keyOccurrences.GetValueOrDefault(
                        canonicalId,
                        (DisplayName: displayName, Duplicates: 0));
                    keyOccurrences[canonicalId] = (displayName, running.Duplicates + 1);
                    kind = StashTileKind.Key;
                }
                else
                {
                    items.Add(row);
                }

                tiles.Add(new StashGridTileViewModel(
                    new GridCellAddress(tile.Row, tile.Column),
                    tile.Width,
                    tile.Height,
                    tileName,
                    quantity > 1 ? $"x{quantity.ToString(CultureInfo.CurrentCulture)}" : string.Empty,
                    kind,
                    row));
            }

            regions.Add(new StashRegionViewModel(
                ContainerTitle(container.ContainerPath),
                container.Rows,
                container.Columns,
                tiles));
        }

        Items = items;
        Regions = regions;
        PlanTiles =
        [
            new(StashPlanGroup.Keep, "Keep", Count(items, StashPlanGroup.Keep), IsWired: false),
            new(StashPlanGroup.Sell, "Sell", Count(items, StashPlanGroup.Sell), IsWired: false),
            new(StashPlanGroup.UseSoon, "Use soon", Count(items, StashPlanGroup.UseSoon), IsWired: false),
            new(StashPlanGroup.Review, "Review", Count(items, StashPlanGroup.Review), IsWired: true),
        ];
        AmmoSummary = ammoRounds
            .Select(entry => new StashAmmoSummaryRowViewModel(entry.Key, entry.Value.Rounds, entry.Value.Stacks))
            .OrderBy(row => row.Caliber, StringComparer.Ordinal)
            .ToArray();
        KeySummary = keyOccurrences
            .Select(entry =>
            {
                keyFactsByItemId.TryGetValue(entry.Key, out var facts);
                return new StashKeySummaryRowViewModel(
                    entry.Value.DisplayName,
                    entry.Value.Duplicates,
                    facts?.MapId ?? "Map not catalogued",
                    facts is { RelevantTaskIds.Count: > 0 }
                        ? $"{facts.RelevantTaskIds.Count} quest(s)"
                        : "No quest use catalogued");
            })
            .OrderBy(row => row.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string?> WikiUriForAsync(
        string? canonicalId,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (canonicalId is null || _itemRepository is null)
        {
            return null;
        }

        if (cache.TryGetValue(canonicalId, out var cached))
        {
            return cached;
        }

        var definition = await _itemRepository.GetAsync(canonicalId, cancellationToken).ConfigureAwait(true);
        cache[canonicalId] = definition?.WikiUri;
        return definition?.WikiUri;
    }

    private static int Count(IReadOnlyList<StashItemRowViewModel> rows, StashPlanGroup group) =>
        rows.Count(row => row.Group == group);

    /// <summary>"stash/ammo case" reads as "Ammo case"; a raw container path never reaches the view.</summary>
    private static string ContainerTitle(string containerPath)
    {
        var last = containerPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? containerPath;
        last = last.Replace('-', ' ').Replace('_', ' ').Trim();
        return last.Length == 0
            ? containerPath
            : char.ToUpper(last[0], CultureInfo.CurrentCulture) + last[1..];
    }

    private static string CoverageDescription(StashContainerCoverage coverage)
    {
        var observed = coverage.ObservedCells.Value;
        var total = coverage.TotalCells.Value;
        var container = ContainerTitle(coverage.ContainerPath);
        return observed is null || total is null
            ? $"{container}: coverage unresolved"
            : $"{container}: {observed}/{total} cells";
    }

    private static string DescribeProvenance(EvidenceProvenance provenance) =>
        $"{provenance.SourceClass} · {provenance.Confidence.Score?.ToString("P0", CultureInfo.InvariantCulture) ?? "unscored"}";

    private InventoryProfileScope? CurrentScope()
    {
        var profile = _runtime.Current.Profile;
        return profile is null
            ? null
            : new InventoryProfileScope(profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Snapshots));
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasNoSnapshots));
        OnPropertyChanged(nameof(ShowsNothingScanned));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(AmmoSummary));
        OnPropertyChanged(nameof(HasAmmoSummary));
        OnPropertyChanged(nameof(HasNoAmmoSummary));
        OnPropertyChanged(nameof(KeySummary));
        OnPropertyChanged(nameof(HasKeySummary));
        OnPropertyChanged(nameof(HasNoKeySummary));
        OnPropertyChanged(nameof(PendingCorrections));
        OnPropertyChanged(nameof(HasPendingCorrections));
        OnPropertyChanged(nameof(TotalsLabel));
        OnPropertyChanged(nameof(CoverageLabel));
        OnPropertyChanged(nameof(Regions));
        OnPropertyChanged(nameof(HasRegions));
        OnPropertyChanged(nameof(PlanTiles));
        OnPropertyChanged(nameof(ReconstructionLabel));
        OnPropertyChanged(nameof(StashValueLabel));
        OnPropertyChanged(nameof(HasStashValue));
        OnPropertyChanged(nameof(RecordedLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(AmmoCountLabel));
        OnPropertyChanged(nameof(KeyCountLabel));
        OnPropertyChanged(nameof(ItemCountLabel));
        OnPropertyChanged(nameof(IsScanInProgress));
        OnPropertyChanged(nameof(IsScanIdle));
        OnPropertyChanged(nameof(IsScanPaused));
        OnPropertyChanged(nameof(CanFinishScan));
        OnPropertyChanged(nameof(GuidedHeadline));
        OnPropertyChanged(nameof(GuidedNextStep));
    }
}
