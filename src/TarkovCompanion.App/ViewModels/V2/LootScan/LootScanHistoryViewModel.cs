using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.ViewModels.V2.LootScan;

/// <summary>
/// The Loot page's "Last scans": every completed scan is saved (#274, #282, #291) and this raid's
/// can be reopened after the page has moved on to the next one.
/// </summary>
/// <remarks>
/// A reopened scan is shown as saved, with its time and the rules that made it, never as the live
/// result: the loot it describes may be gone and the prices have moved since.
/// </remarks>
public sealed class LootScanHistoryViewModel : BindableViewModel
{
    public const int VisibleRows = 5;

    private readonly ILootScanHistoryStore _store;
    private readonly IRuntimeStateStore? _runtime;
    private readonly ILogger _logger;
    private readonly SynchronizationContext? _context;
    private readonly CultureInfo _culture;
    private IReadOnlyList<LootScanHistoryRowViewModel> _rows = [];
    private SavedLootScanDetailViewModel? _opened;
    private Guid? _raidId;

    public LootScanHistoryViewModel(
        ILootScanHistoryStore store,
        IRuntimeStateStore? runtime = null,
        ILogger<LootScanHistoryViewModel>? logger = null,
        CultureInfo? culture = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _context = SynchronizationContext.Current;
        _raidId = runtime?.Current.Raid.RaidId;
        CloseCommand = new DelegateCommand(Close);
        if (runtime is not null)
        {
            runtime.Changed += OnRuntimeChanged;
        }

        _ = RefreshSafelyAsync();
    }

    public string Heading => "Last scans";

    public IReadOnlyList<LootScanHistoryRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => Rows.Count > 0;

    public SavedLootScanDetailViewModel? Opened
    {
        get => _opened;
        private set
        {
            _opened = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpen));
        }
    }

    public bool IsOpen => Opened is not null;

    public ICommand CloseCommand { get; }

    /// <summary>Saves a completed result the Loot page has just shown, then lists it.</summary>
    public async Task RecordAsync(LootScanViewModel shown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (shown.IsProgressive)
        {
            return;
        }

        var raid = _runtime?.Current.Raid;
        var scan = LootScanHistorySnapshot.From(shown, raid);
        await _store.SaveAsync(scan, cancellationToken).ConfigureAwait(true);
        _raidId = raid?.RaidId ?? _raidId;
        // A new live result replaces whatever saved scan was open over the page.
        Opened = null;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>This raid's scans, newest first; the newest few overall when no raid is open.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var raidId = _raidId;
        var scans = raidId is { } id
            ? (await _store.ListForRaidAsync(id, cancellationToken).ConfigureAwait(true)).Reverse().ToArray()
            : await _store.ListRecentAsync(VisibleRows, cancellationToken).ConfigureAwait(true);
        Rows = scans
            .Take(VisibleRows)
            .Select(scan => new LootScanHistoryRowViewModel(scan, _culture, Open))
            .ToArray();
    }

    public void Open(SavedLootScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        Opened = new SavedLootScanDetailViewModel(scan, _culture, CloseCommand);
    }

    public void Close() => Opened = null;

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not list saved loot scans.");
        }
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        var raidId = _runtime!.Current.Raid.RaidId;
        if (raidId is null || raidId == _raidId)
        {
            // Leaving a raid keeps its scans listed until the next one starts.
            return;
        }

        _raidId = raidId;
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context))
        {
            _ = RefreshSafelyAsync();
        }
        else
        {
            _context.Post(_ => _ = RefreshSafelyAsync(), null);
        }
    }

    internal static string Summary(SavedLootScan scan, CultureInfo culture)
    {
        var parts = new List<string>(4);
        foreach (var (verdict, label) in new[]
                 {
                     (LootScanVerdict.Take, "take"),
                     (LootScanVerdict.Swap, "swap"),
                     (LootScanVerdict.Leave, "leave"),
                     (LootScanVerdict.Review, "review"),
                 })
        {
            var count = scan.Count(verdict);
            if (count > 0)
            {
                parts.Add(string.Create(culture, $"{count:N0} {label}"));
            }
        }

        return parts.Count == 0 ? "Nothing read" : string.Join(" · ", parts);
    }

    internal static string RulesLabel(SavedLootScan scan) => "Rules " + scan.RulesetVersion;

    internal static string MapLabel(string? mapId) => string.IsNullOrWhiteSpace(mapId)
        ? string.Empty
        : char.ToUpperInvariant(mapId[0]) + mapId[1..].Replace('-', ' ');
}

/// <summary>One saved scan in "Last scans".</summary>
public sealed class LootScanHistoryRowViewModel
{
    public LootScanHistoryRowViewModel(SavedLootScan scan, CultureInfo culture, Action<SavedLootScan> open)
    {
        Scan = scan;
        TimeLabel = LocalTime.ShortTime(scan.EvaluatedUtc, culture);
        SummaryLabel = LootScanHistoryViewModel.Summary(scan, culture);
        OpenCommand = new DelegateCommand(() => open(scan));
    }

    public SavedLootScan Scan { get; }

    public string TimeLabel { get; }

    public string SummaryLabel { get; }

    public string AutomationName => $"Open scan from {TimeLabel}: {SummaryLabel}";

    public ICommand OpenCommand { get; }
}

/// <summary>A saved scan reopened over the Loot page.</summary>
public sealed class SavedLootScanDetailViewModel
{
    public SavedLootScanDetailViewModel(SavedLootScan scan, CultureInfo culture, ICommand? close = null)
    {
        Scan = scan;
        CloseCommand = close;
        Heading = "Saved scan · " + LocalTime.ShortTime(scan.EvaluatedUtc, culture);
        var context = new List<string>(4);
        if (LootScanHistoryViewModel.MapLabel(scan.MapId) is { Length: > 0 } map)
        {
            context.Add(map);
        }

        context.Add(LootScanHistoryViewModel.Summary(scan, culture));
        context.Add(scan.IsComplete ? "Complete" : "Needed review");
        ContextLabel = string.Join(" · ", context);
        RulesLabel = LootScanHistoryViewModel.RulesLabel(scan);
        Items = scan.Items.Select(item => new SavedLootScanItemViewModel(item, culture)).ToArray();
    }

    public SavedLootScan Scan { get; }

    public string Heading { get; }

    public string ContextLabel { get; }

    public string RulesLabel { get; }

    public string Notice => "Saved result. Prices and loot may have changed.";

    public ICommand? CloseCommand { get; }

    public IReadOnlyList<SavedLootScanItemViewModel> Items { get; }
}

public sealed class SavedLootScanItemViewModel
{
    public SavedLootScanItemViewModel(SavedLootScanItem item, CultureInfo culture)
    {
        Item = item;
        VerdictLabel = item.Verdict.ToString().ToUpperInvariant();
        ValueLabel = item.ValueRoubles is { } value ? LootScanDecisionViewModel.CompactRoubles(value, culture) : string.Empty;
        var detail = new List<string>(3);
        if (item.Placement.Length > 0)
        {
            detail.Add(item.Placement);
        }

        if (item.Reason.Length > 0)
        {
            detail.Add(item.Reason);
        }

        if (item.Confidence is { } confidence)
        {
            detail.Add(string.Create(culture, $"{confidence:P0} sure"));
        }

        DetailLabel = string.Join(" · ", detail);
    }

    public SavedLootScanItem Item { get; }

    public string Name => Item.Name;

    public string VerdictLabel { get; }

    public string ValueLabel { get; }

    public string DetailLabel { get; }

    public bool IsTake => Item.Verdict == LootScanVerdict.Take;

    public bool IsSwap => Item.Verdict == LootScanVerdict.Swap;

    public bool IsLeave => Item.Verdict == LootScanVerdict.Leave;

    public bool IsReview => Item.Verdict == LootScanVerdict.Review;
}

/// <summary>Turns the result the Loot page showed into the record that is saved.</summary>
public static class LootScanHistorySnapshot
{
    public static SavedLootScan From(LootScanViewModel shown, RaidSnapshot? raid)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var result = shown.Result;
        // Every call the engine made carries the version of the rules it used; a scan with none
        // (nothing named, or every call a refusal) was still decided by the current rules.
        var ruleset = result.Decisions
            .Select(decision => decision.Recommendation?.RulesetVersion)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
            ?? ExplainableRecommendationPolicy.CurrentRulesetVersion;
        var items = shown.Decisions
            .Take(LootScanHistoryRetention.MaximumItemsPerScan)
            .Select(row => new SavedLootScanItem(
                row.Decision.Item.Value?.CanonicalId.Value,
                row.Name,
                row.Verdict,
                row.PlacementLabel,
                row.Decision.Economics?.BestNetValueRoubles ?? row.CatalogValueRoubles,
                row.Decision.Item.Provenance.Confidence.Score,
                row.HeadlineReason))
            .ToArray();
        return new SavedLootScan(
            result.ScanId,
            raid?.RaidId,
            raid?.MapId ?? result.Context.ActiveMap,
            result.EvaluatedUtc,
            ruleset,
            shown.IsComplete,
            items);
    }
}
