using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
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
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Core.Common;

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

/// <summary>The few words a sort plan is shown in.</summary>
/// <remarks>
/// The engine's reasons are full sentences written for an evidence disclosure and name
/// contract fields. A row gets the strongest one that is not about evidence, and an item left
/// under Review gets what was missing, in the player's terms.
/// </remarks>
public static class StashSortWording
{
    public static string Label(StashPlanGroup group) => group switch
    {
        StashPlanGroup.UseSoon => "Use soon",
        _ => group.ToString(),
    };

    public static int Order(StashPlanGroup group) => group switch
    {
        StashPlanGroup.Keep => 0,
        StashPlanGroup.UseSoon => 1,
        StashPlanGroup.Sell => 2,
        StashPlanGroup.Organize => 3,
        _ => 4,
    };

    public static string Why(StashOrganizationItem? planned, IReadOnlyList<RecommendationReason>? reasons)
    {
        if (planned is null)
        {
            return string.Empty;
        }

        if (planned.ReasonCodes.Contains("stash.review-command.pinned", StringComparer.Ordinal))
        {
            return "Pinned for this snapshot.";
        }

        if (planned.Group == StashPlanGroup.Organize &&
            planned.ReasonCodes.FirstOrDefault(code =>
                code.StartsWith("stash.organize.move-together.", StringComparison.Ordinal)) is { } organizeCode &&
            int.TryParse(organizeCode["stash.organize.move-together.".Length..], out var moveTogetherCount))
        {
            return $"Move {moveTogetherCount.ToString(CultureInfo.CurrentCulture)} matching stacks together.";
        }

        if (planned.ReasonCodes.Any(code => code.StartsWith("stash.specialist.", StringComparison.Ordinal)))
        {
            // Gear now hands off to the loadout planner, and still says what it is worth here.
            var gear = planned.ReasonCodes.Contains("stash.specialist.gear-unresolved", StringComparer.Ordinal);
            return (gear, planned.NetValueRoubles.Value) switch
            {
                (true, { } worth) => $"Open the recognized kit in Loadout · about ₽{worth.ToString("N0", CultureInfo.CurrentCulture)} to a buyer.",
                (true, null) => "Open the recognized kit in Loadout.",
                _ => "Ammo and keys aren't sorted yet.",
            };
        }

        var ordered = (reasons ?? []).OrderByDescending(reason => reason.Priority).ToArray();
        if (planned.Group == StashPlanGroup.Sell && planned.NetValueRoubles.Value is { } net)
        {
            // The engine's own sentence here is the working: roubles across squares, the band,
            // both channels. A row wants where to sell it and for how much.
            var price = net.ToString("N0", CultureInfo.CurrentCulture);
            return planned.ReasonCodes.Any(code => code.StartsWith("economics.flea-net.", StringComparison.Ordinal))
                ? $"On the flea, about ₽{price} after the fee."
                : $"To a trader, ₽{price}.";
        }

        if (planned.Group != StashPlanGroup.Review)
        {
            return ordered.FirstOrDefault(reason => reason.Category != RecommendationReasonCategory.EvidenceQuality)?.Explanation
                   ?? string.Empty;
        }

        var gaps = ordered
            .Where(reason => reason.Category == RecommendationReasonCategory.EvidenceQuality)
            .Select(reason => reason.Code switch
            {
                "economics.flea-net-untrusted" => "what the flea returns after its fee",
                "economics.trader-untrusted" or "economics.price-missing" => "a current price",
                "economics.footprint-missing" => "how many squares it takes",
                "scarcity.unknown" or "scarcity.untrusted" => "how readily another turns up",
                "profile.incomplete" => "your quest progress",
                "profile.override-untrusted" => "what your rule for this item means",
                "candidate.fir-untrusted" => "whether it is found in raid",
                _ => null,
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return gaps.Length switch
        {
            0 => "Not enough is known to sort it.",
            1 => $"Not known: {gaps[0]}.",
            _ => $"Not known: {string.Join(", ", gaps[..^1])} and {gaps[^1]}.",
        };
    }
}

/// <summary>One recognized item, shown under its Keep/Sell/Use soon/Organize/Review group.</summary>
public sealed record StashItemRowViewModel(
    string ItemKey,
    string DisplayName,
    string ContainerPath,
    string QuantityLabel,
    string EvidenceLabel,
    StashPlanGroup Group)
{
    public string GroupLabel => IsIgnored ? "Ignored" : StashSortWording.Label(Group);

    /// <summary>One line on why the item is in its group, from the engine's own reasons.</summary>
    public string WhyLabel { get; init; } = string.Empty;

    public bool IsIgnored { get; init; }

    public bool IsKeep => !IsIgnored && Group == StashPlanGroup.Keep;

    public bool IsSell => !IsIgnored && Group == StashPlanGroup.Sell;

    public bool IsUseSoon => !IsIgnored && Group == StashPlanGroup.UseSoon;

    public bool IsOrganize => !IsIgnored && Group == StashPlanGroup.Organize;

    public bool IsReview => !IsIgnored && Group == StashPlanGroup.Review;

    public ICommand? SelectCommand { get; init; }

    /// <summary>The catalog's wiki link for this item, when it has one.</summary>
    public string? WikiUri { get; init; }

    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(WikiUri);

    public ICommand? OpenWikiCommand { get; init; }

    public ICommand? OpenLoadoutCommand { get; init; }

    public bool HasLoadoutLink => OpenLoadoutCommand is not null;
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

    /// <summary>
    /// "Keep" or "Sell" in the tile's corner, so the grid says what the plan decided.
    /// </summary>
    /// <remarks>
    /// Only what was sorted is tagged. Review is the rest of the grid and a tag on every other
    /// tile would say nothing; ammo and keys have their own edge colour and are not sorted here.
    /// </remarks>
    public string GroupTag => Kind == StashTileKind.Item && ItemRow is { IsReview: false } row ? row.GroupLabel : string.Empty;

    public bool HasGroupTag => GroupTag.Length > 0;

    public bool IsKeepTile => HasGroupTag && ItemRow is { IsKeep: true } or { IsUseSoon: true };

    public bool IsSellTile => HasGroupTag && ItemRow is { IsSell: true };

    public bool IsOrganizeTile => HasGroupTag && ItemRow is { IsOrganize: true };

    public bool IsIgnoredTile => HasGroupTag && ItemRow is { IsIgnored: true };

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

/// <summary>A Keep / Sell / Use soon / Organize / Review tile over the sort plan.</summary>
public sealed record StashPlanTileViewModel(StashPlanGroup Group, string Label, int Count, bool IsWired)
{
    public string CountLabel => IsWired ? Count.ToString(CultureInfo.CurrentCulture) : "—";

    public bool IsKeep => Group == StashPlanGroup.Keep;

    public bool IsSell => Group == StashPlanGroup.Sell;

    public bool IsUseSoon => Group == StashPlanGroup.UseSoon;

    public bool IsOrganize => Group == StashPlanGroup.Organize;

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
    private readonly IStashReviewCommandSink _reviewCommands;
    private readonly IItemFactCatalog _catalog;
    private readonly IRuntimeStateStore _runtime;
    private readonly TimeProvider _clock;
    private readonly AppDataPaths? _paths;
    // Optional so a composition without an item catalog is still a valid composition: without
    // one, results rows have no wiki link, which is what they had until now.
    private readonly IItemRepository? _itemRepository;
    private readonly IWikiLinkOpener? _wikiOpener;
    // [V2 rough package 40] Optional for the same reason: without them a Stash scan is one
    // screenshot through the capture dialog, which is what it was before.
    private readonly GuidedStashScanService? _guidedScan;
    private readonly GuidedStashScanArming? _arming;
    private readonly IProfileRuntimeContextService? _profileContext;
    private readonly StashPlanSource? _planSource;
    private readonly StashScanCaptureStatus? _captureStatus;
    private readonly StashReconstructionMerger _merger = new();
    private StashReviewCommandState _reviewState = StashReviewCommandState.Empty;
    private IReadOnlyList<StashReviewCommand> _reviewHistory = [];
    private bool _isSorted;
    private bool _sortFailed;
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
    private Func<IReadOnlyCollection<string>, Task>? _openLoadout;

    public StashScanWorkspaceViewModel(
        IStashSnapshotStore store,
        StashScanWorkflow workflow,
        IStashReviewCommandSink reviewCommands,
        IItemFactCatalog catalog,
        IRuntimeStateStore runtime,
        TimeProvider? clock = null,
        IItemRepository? itemRepository = null,
        IWikiLinkOpener? wikiOpener = null,
        GuidedStashScanService? guidedScan = null,
        GuidedStashScanArming? arming = null,
        IProfileRuntimeContextService? profileContext = null,
        StashPlanSource? planSource = null,
        AppDataPaths? paths = null,
        StashScanCaptureStatus? captureStatus = null)
    {
        _planSource = planSource;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _reviewCommands = reviewCommands ?? throw new ArgumentNullException(nameof(reviewCommands));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? TimeProvider.System;
        _paths = paths;
        _itemRepository = itemRepository;
        _wikiOpener = wikiOpener;
        _guidedScan = guidedScan;
        _arming = arming;
        _profileContext = profileContext;
        _captureStatus = captureStatus;
        if (_guidedScan is not null)
        {
            _guidedScan.Changed += OnGuidedScanChanged;
        }

        if (_arming is not null)
        {
            _arming.Changed += OnGuidedScanChanged;
        }

        if (_captureStatus is not null)
        {
            _captureStatus.Changed += OnCaptureStatusChanged;
        }

        FinishScanCommand = new AsyncDelegateCommand(FinishScanAsync);
        UndoLastScreenshotCommand = new AsyncDelegateCommand(UndoLastScreenshotAsync);
        DiscardScanCommand = new AsyncDelegateCommand(DiscardScanAsync);
        ContinueScanCommand = new DelegateCommand(() => _arming?.Resume());

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        DeleteSelectedCommand = new AsyncDelegateCommand(DeleteSelectedAsync);
        CompareToPreviousCommand = new AsyncDelegateCommand(CompareToPreviousAsync);
        ExportCsvCommand = new AsyncDelegateCommand(() => ExportLatestAsync("csv", StashSnapshotExport.Csv));
        ExportJsonCommand = new AsyncDelegateCommand(() => ExportLatestAsync("json", StashSnapshotExport.Json));
        MarkSelectedUnknownCommand = new AsyncDelegateCommand(MarkSelectedUnknownAsync);
        CorrectIdentityCommand = new AsyncDelegateCommand(CorrectIdentityAsync);
        CorrectQuantityCommand = new AsyncDelegateCommand(CorrectQuantityAsync);
        PinSelectedCommand = new AsyncDelegateCommand(PinSelectedAsync);
        IgnoreSelectedCommand = new AsyncDelegateCommand(IgnoreSelectedAsync);
        RescanSelectedRegionCommand = new AsyncDelegateCommand(RescanSelectedRegionAsync);
        MergePreviousSnapshotCommand = new AsyncDelegateCommand(MergePreviousSnapshotAsync);
        UndoReviewCommand = new AsyncDelegateCommand(UndoReviewAsync);
        StartFullScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Stash));
        StartAmmoScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Ammo));
        StartKeysScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Keys));
        StartSelectedScanCommand = new AsyncDelegateCommand(StartSelectedScanAsync);
        ShowGridCommand = new DelegateCommand(() => IsGridView = true);
        ShowListCommand = new DelegateCommand(() => IsGridView = false);
        ScanTargets =
        [
            // [f920 capture] "Ammo" and "Keys" were offered here and armed intents the stash
            // handoff ignored: the capture ran and this page never changed. #283 made them guided
            // case sub-scans, one screenshot per open case, which raise the owned counts the Ammo
            // and Keys pages read. Without a guided scan service there is nothing to run them.
            new StashScanTargetViewModel(ScanIntent.Stash, "Full stash", SelectScanTarget) { IsSelected = true },
            .. guidedScan is null
                ? Array.Empty<StashScanTargetViewModel>()
                :
                [
                    new StashScanTargetViewModel(ScanIntent.Ammo, "Ammo cases", SelectScanTarget),
                    new StashScanTargetViewModel(ScanIntent.Keys, "Key cases", SelectScanTarget),
                ],
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

    private static string PausedNextStep(GuidedStashScanProgress progress) => progress.Kind != StashScanKind.Full
        ? progress.Screenshots == 0
            ? "A case scan is waiting. Press Keep going, then " + StashSubScan.NextStep(progress.Kind, GuidedStashFrameOutcome.Added, 0).ToLower(CultureInfo.CurrentCulture)
            : $"An unfinished case scan is waiting: {progress.Screenshots.ToString(CultureInfo.CurrentCulture)} case{(progress.Screenshots == 1 ? string.Empty : "s")}. Keep going, finish, or discard it."
        : progress.Screenshots == 0
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
        : LocalTime.Moment(_selected.RecordedUtc);

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

    public bool CanMergePreviousSnapshot => MergeCandidate() is not null;

    public bool CanUndoReview => _reviewState.LatestUndoable is not null;

    public string UndoReviewLabel => _reviewState.LatestUndoable is { } command
        ? $"Undo {StashReviewCommandProjection.ActionLabel(command.Action)}"
        : "Undo last change";

    /// <summary>
    /// Never null so the "Correct the selected item" card can bind it directly: that card's
    /// bindings evaluate even while <see cref="HasSelectedItem"/> keeps it hidden, and a gallery
    /// walk that visits every state without ever selecting an item would otherwise bind
    /// <c>SelectedItem.DisplayName</c> against a null <see cref="SelectedItem"/>.
    /// </summary>
    public string SelectedItemDisplayName => SelectedItem?.DisplayName ?? string.Empty;

    public bool SelectedItemHasLoadoutLink => SelectedItem?.HasLoadoutLink == true;

    public ICommand? SelectedItemOpenLoadoutCommand => SelectedItem?.OpenLoadoutCommand;

    /// <summary>The selected item's group and the whole of its reason, which a row has to trim.</summary>
    public string SelectedItemSortLabel => SelectedItem is not { } item
        ? string.Empty
        : string.IsNullOrEmpty(item.WhyLabel) ? item.GroupLabel : $"{item.GroupLabel}. {item.WhyLabel}";

    public string TotalsLabel => _selected is null
        ? string.Empty
        : $"{_selected.Recognition.Result.Value!.TotalKnownValueRoubles.Value?.ToString("N0", CultureInfo.CurrentCulture) ?? "unknown"} roubles known · " +
          $"{_selected.Recognition.Result.Value!.UnresolvedCells.Value?.ToString(CultureInfo.CurrentCulture) ?? "unknown"} cells unresolved";

    public string CoverageLabel => _selected is null
        ? string.Empty
        : string.Join(" · ", _selected.Recognition.Result.Value!.Coverage.Select(CoverageDescription));

    /// <summary>
    /// What the sort plan is made from, or why there is none.
    /// </summary>
    /// <remarks>
    /// This was one fixed sentence saying the sort "isn't wired yet", true of every stash ever
    /// scanned. It is only said now when it is still the case: no profile to sort for.
    /// </remarks>
    public string RecommendationNotice => _isSorted
        ? "Sorted by your pins, quests, hideout and prices. Gear, ammo and keys are never marked Sell."
        : _sortFailed
            ? "This snapshot couldn't be sorted, so everything is under Review."
            : "No profile is active, so nothing is sorted. Everything is under Review.";

    public string CorrectionsNotice { get; } = "Saved with this snapshot.";

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
                OnPropertyChanged(nameof(SelectedItemSortLabel));
                OnPropertyChanged(nameof(SelectedItemHasLoadoutLink));
                OnPropertyChanged(nameof(SelectedItemOpenLoadoutCommand));
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand CompareToPreviousCommand { get; }

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportJsonCommand { get; }

    public ICommand MarkSelectedUnknownCommand { get; }

    public ICommand CorrectIdentityCommand { get; }

    public ICommand CorrectQuantityCommand { get; }

    public ICommand PinSelectedCommand { get; }

    public ICommand IgnoreSelectedCommand { get; }

    public ICommand RescanSelectedRegionCommand { get; }

    public ICommand MergePreviousSnapshotCommand { get; }

    public ICommand UndoReviewCommand { get; }

    public ICommand StartFullScanCommand { get; }

    public ICommand StartAmmoScanCommand { get; }

    public ICommand StartKeysScanCommand { get; }

    private void RequestScan(ScanIntent intent) => ScanRequested?.Invoke(this, intent);

    /// <summary>
    /// A full-stash scan is a scroll-through and an ammo or key scan a run of open cases; both
    /// start a guided scan that keeps capture armed until Finish.
    /// </summary>
    private async Task StartSelectedScanAsync()
    {
        var kind = ScanTarget switch
        {
            ScanIntent.Stash => StashScanKind.Full,
            ScanIntent.Ammo => StashScanKind.Ammo,
            ScanIntent.Keys => StashScanKind.Keys,
            _ => (StashScanKind?)null,
        };
        if (kind is null || _guidedScan is null || CurrentScope() is not { } scope)
        {
            RequestScan(ScanTarget);
            return;
        }

        await _guidedScan
            .StartAsync(
                scope,
                _profileContext?.Current.ActiveProfile?.Context.DataSnapshot.SnapshotId ?? "unversioned",
                CancellationToken.None,
                kind.Value)
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
        Status = $"{StashSubScan.SavedHeadline(finished.Kind)}. {finished.Summary}";
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

    private void OnCaptureStatusChanged(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnCaptureStatusChanged(sender, eventArgs));
            return;
        }

        if (_captureStatus?.LastMessage is { } message)
        {
            Status = message;
        }
    }

    private async Task ShowScanProgressAsync()
    {
        await OverlayScanProgressAsync(CancellationToken.None).ConfigureAwait(true);
        RaiseAll();
    }

    public void AttachLoadoutNavigation(Func<IReadOnlyCollection<string>, Task> openLoadout) =>
        _openLoadout = openLoadout ?? throw new ArgumentNullException(nameof(openLoadout));

    /// <summary>While a scan is collecting, the grid shows that scan rather than the last saved one.</summary>
    private async Task OverlayScanProgressAsync(CancellationToken cancellationToken)
    {
        if (_guidedScan is { Current.IsCollecting: true } guided)
        {
            await BuildItemBreakdownAsync(guided.Current.Reconstruction, cancellationToken).ConfigureAwait(true);
            Status = (IsScanPaused, guided.Current.Kind) switch
            {
                (true, _) => "A scan is waiting to be finished.",
                (false, StashScanKind.Ammo) => "Scanning your ammo cases.",
                (false, StashScanKind.Keys) => "Scanning your key cases.",
                _ => "Scanning your stash.",
            };
        }
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var scope = CurrentScope();
        if (scope is null)
        {
            Snapshots = [];
            _reviewHistory = [];
            _reviewState = StashReviewCommandState.Empty;
            PendingCorrections = [];
            Status = _captureStatus?.LastMessage
                ?? "No profile is loaded yet, so there is no stash scope to browse.";
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
                    LocalTime.Moment(summary.RecordedUtc),
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
            _reviewHistory = [];
            _reviewState = StashReviewCommandState.Empty;
            Items = [];
            Regions = [];
            AmmoSummary = [];
            KeySummary = [];
            RaiseAll();
            return;
        }

        _selected = record;
        await ReloadSelectedReviewAsync(cancellationToken).ConfigureAwait(true);
        await OverlayScanProgressAsync(cancellationToken).ConfigureAwait(true);
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
        _reviewHistory = [];
        _reviewState = StashReviewCommandState.Empty;
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

    private async Task ExportLatestAsync(string extension, Func<StashSnapshotRecord, string> format)
    {
        var scope = CurrentScope();
        if (scope is null || _paths is null)
        {
            Status = "A profile and export folder are required.";
            return;
        }

        try
        {
            var latest = (await _store.ListAsync(scope, 1, CancellationToken.None).ConfigureAwait(true)).SingleOrDefault();
            var snapshot = latest is null
                ? null
                : await _store.ReadAsync(scope, latest.SnapshotId, CancellationToken.None).ConfigureAwait(true);
            if (snapshot is null)
            {
                Status = "There is no stash snapshot to export.";
                return;
            }

            var directory = Path.Combine(_paths.Root, "Exports");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(
                directory,
                $"{LocalTime.FileStamp(_clock.GetUtcNow())}-stash-snapshot.{extension}");
            await File.WriteAllTextAsync(destination, format(snapshot), CancellationToken.None).ConfigureAwait(true);
            Status = $"Exported {extension.ToUpperInvariant()} to {destination}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Export failed: {exception.Message}";
        }
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

    private async Task PinSelectedAsync()
    {
        if (SelectedRecognitionSnapshotId is not { } snapshotId || SelectedItem is not { } item)
        {
            return;
        }

        if (_reviewState.PinnedItemKeys.Contains(item.ItemKey))
        {
            Status = "That item is already pinned.";
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            snapshotId,
            StashReviewActionKind.Pin,
            [item.ItemKey],
            _clock.GetUtcNow(),
            StashReviewCommandProjection.WorkspaceOrigin,
            reason: "Kept regardless of the current sort advice."), "Item pinned.").ConfigureAwait(true);
    }

    private async Task IgnoreSelectedAsync()
    {
        if (SelectedRecognitionSnapshotId is not { } snapshotId || SelectedItem is not { } item)
        {
            return;
        }

        if (_reviewState.IgnoredItemKeys.Contains(item.ItemKey))
        {
            Status = "That item is already ignored.";
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            snapshotId,
            StashReviewActionKind.Ignore,
            [item.ItemKey],
            _clock.GetUtcNow(),
            StashReviewCommandProjection.WorkspaceOrigin,
            reason: "Dropped from this snapshot's sort plan."), "Item ignored.").ConfigureAwait(true);
    }

    private async Task RescanSelectedRegionAsync()
    {
        if (SelectedRecognitionSnapshotId is not { } snapshotId || SelectedItem is not { } item)
        {
            return;
        }

        if (_reviewState.RescanContainerPaths.Contains(item.ContainerPath))
        {
            Status = "That region is already queued for rescan.";
            return;
        }

        var saved = await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            snapshotId,
            StashReviewActionKind.Rescan,
            [item.ContainerPath],
            _clock.GetUtcNow(),
            StashReviewCommandProjection.WorkspaceOrigin,
            reason: $"Recapture {ContainerTitle(item.ContainerPath)}."), "Region queued for rescan.").ConfigureAwait(true);
        if (!saved)
        {
            return;
        }

        if (_guidedScan is not null && CurrentScope() is { } scope)
        {
            await _guidedScan.StartAsync(
                scope,
                _profileContext?.Current.ActiveProfile?.Context.DataSnapshot.SnapshotId ?? "unversioned",
                CancellationToken.None).ConfigureAwait(true);
            _arming?.Resume();
            Status = $"Ready to recapture {ContainerTitle(item.ContainerPath)}.";
            RaiseAll();
            return;
        }

        RequestScan(ScanIntent.Stash);
    }

    private async Task MergePreviousSnapshotAsync()
    {
        if (_selected is null || SelectedRecognitionSnapshotId is not { } snapshotId)
        {
            return;
        }

        var previous = MergeCandidate();
        if (previous is null)
        {
            Status = "There is no earlier snapshot to merge.";
            return;
        }

        await SubmitReviewAsync(new StashReviewCommand(
            Guid.NewGuid(),
            snapshotId,
            StashReviewActionKind.MergeEntries,
            [_selected.SnapshotId.ToString("D"), previous.SnapshotId.ToString("D")],
            _clock.GetUtcNow(),
            StashReviewCommandProjection.SnapshotMergeOrigin,
            reason: $"Combined with the snapshot from {previous.RecordedLabel}."), "Snapshots combined.").ConfigureAwait(true);
    }

    private async Task UndoReviewAsync()
    {
        if (_reviewState.LatestUndoable is not { } command)
        {
            Status = "There is no review change to undo.";
            return;
        }

        await SubmitReviewAsync(
            StashReviewCommandProjection.Undo(command, _clock.GetUtcNow()),
            $"Undid {StashReviewCommandProjection.ActionLabel(command.Action)}.").ConfigureAwait(true);
    }

    private async Task<bool> SubmitReviewAsync(StashReviewCommand command, string savedStatus = "Correction saved.")
    {
        try
        {
            await _workflow.ReviewAsync(command, CancellationToken.None).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            Status = exception.Message;
            return false;
        }

        await ReloadSelectedReviewAsync(CancellationToken.None).ConfigureAwait(true);
        Status = savedStatus;
        return true;
    }

    /// <summary>
    /// The recognition-level snapshot id a <see cref="StashReviewCommand"/> targets, distinct from
    /// the durable <see cref="StashSnapshotRecord.SnapshotId"/> used to read, delete or compare
    /// records in <see cref="IStashSnapshotStore"/>.
    /// </summary>
    private string? SelectedRecognitionSnapshotId => _selected?.Recognition.Result.Value?.SnapshotId;

    private static IReadOnlyList<StashReviewCommandRowViewModel> ReviewCommandRows(
        IReadOnlyList<StashReviewCommand> commands) =>
        commands
            .Select(command => StashReviewCommandProjection.IsUndoRecord(command)
                ? new StashReviewCommandRowViewModel(
                    "Undo",
                    command.Reason ?? "Review change",
                    LocalTime.Moment(command.CreatedUtc),
                    null)
                : new StashReviewCommandRowViewModel(
                    command.Action.ToString(),
                    string.Join(", ", command.TargetItemKeys),
                    LocalTime.Moment(command.CreatedUtc),
                    command.Reason))
            .ToArray();

    private StashSnapshotRowViewModel? MergeCandidate() => _selected is null
        ? null
        : Snapshots.FirstOrDefault(row =>
            row.SnapshotId != _selected.SnapshotId && !_reviewState.MergedSnapshotIds.Contains(row.SnapshotId));

    private async Task ReloadSelectedReviewAsync(CancellationToken cancellationToken)
    {
        if (_selected?.Recognition.Result.Value is not { } recognized || CurrentScope() is not { } scope)
        {
            _reviewHistory = [];
            _reviewState = StashReviewCommandState.Empty;
            PendingCorrections = [];
            return;
        }

        _reviewHistory = await _reviewCommands.ListAsync(recognized.SnapshotId, cancellationToken).ConfigureAwait(true);
        _reviewState = StashReviewCommandProjection.Project(_reviewHistory);
        PendingCorrections = ReviewCommandRows(_reviewHistory);

        var additions = new List<StashReconstruction>();
        foreach (var snapshotId in _reviewState.MergedSnapshotIds.Where(id => id != _selected.SnapshotId))
        {
            var merged = await _store.ReadAsync(scope, snapshotId, cancellationToken).ConfigureAwait(true);
            if (merged?.Recognition.Result.Value is { } mergedRecognition)
            {
                additions.Add(_projector.Project(mergedRecognition));
            }
        }

        var reconstruction = _projector.Project(recognized);
        await BuildItemBreakdownAsync(
            additions.Count == 0 ? reconstruction : _merger.Merge(reconstruction, additions),
            cancellationToken).ConfigureAwait(true);
        RaiseAll();
    }

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
        var selectedItemKey = SelectedItem?.ItemKey;
        _reconstruction = reconstruction;
        var ammoByItemId = _ammoByItemId ?? new Dictionary<string, AmmoStats>(StringComparer.Ordinal);
        var keyFactsByItemId = _keyFactsByItemId ?? new Dictionary<string, KeyFacts>(StringComparer.Ordinal);
        var definitionsByItemId = new Dictionary<string, ItemDefinition?>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, LoadoutItemFacts> loadoutFacts;
        try
        {
            loadoutFacts = (await _catalog.GetLoadoutFactsAsync(cancellationToken).ConfigureAwait(true))
                .ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("stash", "read loadout facts", exception);
            loadoutFacts = new Dictionary<string, LoadoutItemFacts>(StringComparer.Ordinal);
        }
        var recognizedLoadoutIds = RecognizedLoadoutIds(reconstruction, loadoutFacts);

        var sorted = await SortAsync(reconstruction, ammoByItemId, keyFactsByItemId, cancellationToken).ConfigureAwait(true);
        var plannedByKey = sorted?.Plan.Items.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        _isSorted = sorted is not null;
        OnPropertyChanged(nameof(RecommendationNotice));

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
                var definition = await ItemDefinitionForAsync(canonicalId, definitionsByItemId, cancellationToken).ConfigureAwait(true);
                var displayName = tile.DisplayName ?? definition?.Name
                    ?? (tile.CandidateNames.Count > 0 ? $"{tile.CandidateNames[0]}?" : "Unknown item");

                var tileName = TileName(tile, definition, displayName);
                var quantity = tile.Quantity ?? 1;
                var isIgnored = _reviewState.IgnoredItemKeys.Contains(tile.ItemKey);

                var wikiUri = definition?.WikiUri;
                var bare = new StashItemRowViewModel(
                    tile.ItemKey,
                    displayName,
                    container.ContainerPath,
                    quantity == 1 ? "x1" : $"x{quantity.ToString(CultureInfo.CurrentCulture)}",
                    DescribeProvenance(tile.Provenance),
                    plannedByKey?.GetValueOrDefault(tile.ItemKey)?.Group ?? StashPlanGroup.Review)
                {
                    IsIgnored = isIgnored,
                    WikiUri = wikiUri,
                    WhyLabel = isIgnored
                        ? "Ignored in this snapshot's plan."
                        : StashSortWording.Why(
                            plannedByKey?.GetValueOrDefault(tile.ItemKey),
                            sorted?.ReasonsByItemKey.GetValueOrDefault(tile.ItemKey)),
                };
                var row = bare with
                {
                    SelectCommand = new DelegateCommand(() => SelectedItem = bare),
                    OpenWikiCommand = new DelegateCommand(() => _wikiOpener?.TryOpen(wikiUri)),
                    OpenLoadoutCommand = recognizedLoadoutIds.Count > 0 &&
                        plannedByKey?.GetValueOrDefault(tile.ItemKey)?.ReasonCodes.Contains(
                            "stash.specialist.gear-unresolved", StringComparer.Ordinal) == true &&
                        _openLoadout is not null
                            ? new AsyncDelegateCommand(() => _openLoadout(recognizedLoadoutIds))
                            : null,
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

        // Keep first, then what can go, then what nobody could sort; the scan's own order within
        // each. A list in grid order buried the four things worth selling among two hundred rows.
        Items = items
            .Select((row, index) => (Row: row, Index: index))
            .OrderBy(entry => StashSortWording.Order(entry.Row.Group))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Row)
            .ToArray();
        Regions = regions;
        if (selectedItemKey is not null)
        {
            SelectedItem = regions
                .SelectMany(region => region.Tiles)
                .Select(tile => tile.ItemRow)
                .FirstOrDefault(row => string.Equals(row?.ItemKey, selectedItemKey, StringComparison.Ordinal));
        }
        PlanTiles =
        [
            new(StashPlanGroup.Keep, "Keep", Count(items, StashPlanGroup.Keep), IsWired: _isSorted),
            new(StashPlanGroup.Sell, "Sell", Count(items, StashPlanGroup.Sell), IsWired: _isSorted),
            new(StashPlanGroup.UseSoon, "Use soon", Count(items, StashPlanGroup.UseSoon), IsWired: _isSorted),
            new(StashPlanGroup.Organize, "Organize", Count(items, StashPlanGroup.Organize), IsWired: _isSorted),
            new(StashPlanGroup.Review, "Review", Count(items, StashPlanGroup.Review), IsWired: true),
        ];
        AmmoSummary = ammoRounds
            .Select(entry => new StashAmmoSummaryRowViewModel(
                CaliberText.Describe(entry.Key),
                entry.Value.Rounds,
                entry.Value.Stacks))
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

    internal static IReadOnlyList<string> RecognizedLoadoutIds(
        StashReconstruction reconstruction,
        IReadOnlyDictionary<string, LoadoutItemFacts> facts)
    {
        var ids = new List<string>();
        var singleSlots = new HashSet<LoadoutSlot>();
        foreach (var tile in reconstruction.Containers.SelectMany(container => container.Tiles))
        {
            if (tile.ItemId is not { } itemId || facts.GetValueOrDefault(itemId) is not { } fact ||
                LoadoutPageViewModel.SlotForRecognized(fact.Category, fact.Name) is not { } slot)
            {
                continue;
            }

            if (!LoadoutPageViewModel.RecognizedSlotAllowsMany(slot) && !singleSlots.Add(slot))
            {
                continue;
            }

            ids.Add(itemId);
        }

        return ids;
    }

    /// <summary>
    /// Puts every named tile to the recommendation engine and the organization planner.
    /// </summary>
    /// <remarks>
    /// Null where there is nothing to sort with or for: no plan source composed, or no active
    /// profile. A failure to sort leaves everything under Review, which is what the workspace
    /// showed before it could sort at all, and the notice says the sort failed; the scan itself
    /// is still drawn.
    /// </remarks>
    private async Task<StashSortPlan?> SortAsync(
        StashReconstruction reconstruction,
        IReadOnlyDictionary<string, AmmoStats> ammoByItemId,
        IReadOnlyDictionary<string, KeyFacts> keyFactsByItemId,
        CancellationToken cancellationToken)
    {
        _sortFailed = false;
        if (_planSource is null || _profileContext?.Current.ActiveProfile is not { } profile || reconstruction.KnownTiles == 0)
        {
            return null;
        }

        try
        {
            return await _planSource.BuildAsync(
                    reconstruction,
                    _selected?.SnapshotId.ToString("N") ?? "scan-in-progress",
                    profile,
                    itemId => ammoByItemId.ContainsKey(itemId)
                        ? StashSpecialistIntelligenceKind.Ammo
                        : keyFactsByItemId.ContainsKey(itemId)
                            ? StashSpecialistIntelligenceKind.Key
                            : StashSpecialistIntelligenceKind.None,
                    _reviewState,
                    _clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Said as what it is. The first version of this fell through to "no profile is
            // active", which sent the reader looking for a fault that was not there.
            _sortFailed = true;
            return null;
        }
    }

    private async Task<ItemDefinition?> ItemDefinitionForAsync(
        string? canonicalId,
        Dictionary<string, ItemDefinition?> cache,
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
        cache[canonicalId] = definition;
        return definition;
    }

    /// <summary>One-square game tiles use short names; larger footprints have room for the full name.</summary>
    internal static string TileName(StashReconstructedTile tile, ItemDefinition? definition, string displayName)
    {
        if (!tile.IsKnown && tile.CandidateNames.Count == 0)
        {
            return "?";
        }

        return tile.Width == 1 && tile.Height == 1 && !string.IsNullOrWhiteSpace(definition?.ShortName)
            ? definition.ShortName
            : displayName;
    }

    private static int Count(IReadOnlyList<StashItemRowViewModel> rows, StashPlanGroup group) =>
        rows.Count(row => !row.IsIgnored && row.Group == group);

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
        // The capture handoff freezes the profile context's scope into every screenshot, and the
        // assembler refuses a scan whose scope differs. The legacy profile names the mode
        // "Regular" where the context says "Pvp", so a guided scan started from the legacy scope
        // threw on its first screenshot (#283, found rendering a real case screenshot).
        if (_profileContext?.Current.ActiveProfile is { } active)
        {
            return new InventoryProfileScope(
                active.Context.Identity.ProfileId,
                active.Context.Identity.Generation,
                active.Context.Mode.ToString());
        }

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
        OnPropertyChanged(nameof(CanMergePreviousSnapshot));
        OnPropertyChanged(nameof(CanUndoReview));
        OnPropertyChanged(nameof(UndoReviewLabel));
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
