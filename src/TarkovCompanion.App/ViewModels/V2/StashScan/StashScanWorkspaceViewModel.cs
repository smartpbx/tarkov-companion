using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
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
    private IReadOnlyDictionary<string, AmmoStats>? _ammoByItemId;
    private IReadOnlyDictionary<string, KeyFacts>? _keyFactsByItemId;
    private StashSnapshotRecord? _selected;
    private string _status = "Stash snapshots have not been loaded.";
    private string _identityCorrection = string.Empty;
    private string _quantityCorrection = string.Empty;
    private StashItemRowViewModel? _selectedItem;

    public StashScanWorkspaceViewModel(
        IStashSnapshotStore store,
        StashScanWorkflow workflow,
        InMemoryStashReviewCommandSink reviewCommands,
        IItemFactCatalog catalog,
        IRuntimeStateStore runtime,
        TimeProvider? clock = null,
        IItemRepository? itemRepository = null,
        IWikiLinkOpener? wikiOpener = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _reviewCommands = reviewCommands ?? throw new ArgumentNullException(nameof(reviewCommands));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? TimeProvider.System;
        _itemRepository = itemRepository;
        _wikiOpener = wikiOpener;

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        DeleteSelectedCommand = new AsyncDelegateCommand(DeleteSelectedAsync);
        CompareToPreviousCommand = new AsyncDelegateCommand(CompareToPreviousAsync);
        MarkSelectedUnknownCommand = new AsyncDelegateCommand(MarkSelectedUnknownAsync);
        CorrectIdentityCommand = new AsyncDelegateCommand(CorrectIdentityAsync);
        CorrectQuantityCommand = new AsyncDelegateCommand(CorrectQuantityAsync);
        StartFullScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Stash));
        StartAmmoScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Ammo));
        StartKeysScanCommand = new DelegateCommand(() => RequestScan(ScanIntent.Keys));
    }

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
            Status = Snapshots.Count == 0
                ? "No stash snapshots yet. Start a scan to build the first one."
                : $"{Snapshots.Count} snapshot(s).";

            if (_selected is null)
            {
                var current = summaries.FirstOrDefault(summary => summary.IsCurrent) ?? summaries.FirstOrDefault();
                if (current is not null)
                {
                    await SelectSnapshotAsync(current.SnapshotId, cancellationToken).ConfigureAwait(true);
                    return;
                }
            }

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
            AmmoSummary = [];
            KeySummary = [];
            RaiseAll();
            return;
        }

        _selected = record;
        await BuildItemBreakdownAsync(record, cancellationToken).ConfigureAwait(true);
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

    private async Task BuildItemBreakdownAsync(StashSnapshotRecord record, CancellationToken cancellationToken)
    {
        var ammoByItemId = _ammoByItemId ?? new Dictionary<string, AmmoStats>(StringComparer.Ordinal);
        var keyFactsByItemId = _keyFactsByItemId ?? new Dictionary<string, KeyFacts>(StringComparer.Ordinal);
        var wikiUriByItemId = new Dictionary<string, string?>(StringComparer.Ordinal);
        var stash = record.Recognition.Result.Value!;

        var items = new List<StashItemRowViewModel>();
        var ammoRounds = new Dictionary<string, (int Rounds, int Stacks)>(StringComparer.Ordinal);
        var keyOccurrences = new Dictionary<string, (string DisplayName, int Duplicates)>(StringComparer.Ordinal);

        foreach (var region in stash.CapturedRegions)
        {
            foreach (var cell in region.Grid.Cells)
            {
                var item = cell.Item.Value;
                if (item is null)
                {
                    continue;
                }

                var canonicalId = item.CanonicalId.Value;
                var displayName = item.DisplayName.Value ?? canonicalId ?? "Unresolved item";
                var quantity = item.Quantity.Value ?? 1;
                var itemKey = $"{region.ContainerPath}@{cell.Anchor.Row}:{cell.Anchor.Column}";

                if (canonicalId is not null && ammoByItemId.TryGetValue(canonicalId, out var ammo))
                {
                    var running = ammoRounds.GetValueOrDefault(ammo.Caliber);
                    ammoRounds[ammo.Caliber] = (running.Rounds + quantity, running.Stacks + 1);
                    continue;
                }

                if (canonicalId is not null && keyFactsByItemId.TryGetValue(canonicalId, out _))
                {
                    var running = keyOccurrences.GetValueOrDefault(
                        canonicalId,
                        (DisplayName: displayName, Duplicates: 0));
                    keyOccurrences[canonicalId] = (displayName, running.Duplicates + 1);
                    continue;
                }

                var wikiUri = await WikiUriForAsync(canonicalId, wikiUriByItemId, cancellationToken).ConfigureAwait(true);
                var row = new StashItemRowViewModel(
                    itemKey,
                    displayName,
                    region.ContainerPath,
                    quantity == 1 ? "x1" : $"x{quantity.ToString(CultureInfo.CurrentCulture)}",
                    DescribeProvenance(cell.Item.Provenance),
                    StashPlanGroup.Review)
                {
                    WikiUri = wikiUri,
                };
                items.Add(row with
                {
                    SelectCommand = new DelegateCommand(() => SelectedItem = row),
                    OpenWikiCommand = new DelegateCommand(() => _wikiOpener?.TryOpen(wikiUri)),
                });
            }
        }

        Items = items;
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

    private static string CoverageDescription(StashContainerCoverage coverage)
    {
        var observed = coverage.ObservedCells.Value;
        var total = coverage.TotalCells.Value;
        return observed is null || total is null
            ? $"{coverage.ContainerPath}: coverage unresolved"
            : $"{coverage.ContainerPath}: {observed}/{total} cells";
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
    }
}
