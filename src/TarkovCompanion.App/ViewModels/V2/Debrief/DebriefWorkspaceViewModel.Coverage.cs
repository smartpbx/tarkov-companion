using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>A pick for the extract used: one the raid offered, or the one its route was planned to.</summary>
public sealed record DebriefExtractChoiceViewModel(string Label, ICommand SelectCommand, bool IsSelected = false);

/// <summary>
/// #291: per-map coverage, charts with their tables, the extract used, and archive/restore.
/// </summary>
/// <remarks>
/// Everything here is computed from the records the load already built (see BuildRecordsAsync), so
/// switching views, toggling a table or showing the archive costs no I/O. The coverage and charts
/// follow the same filters as the list and never count an archived raid.
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private bool _isStatsView;
    private bool _showArchived;
    private string _extractUsedInput = string.Empty;
    private ICommand? _showList;
    private ICommand? _showStats;
    private ICommand? _toggleArchived;
    private ICommand? _archiveSelected;
    private ICommand? _saveExtractUsed;

    public bool IsStatsView
    {
        get => _isStatsView;
        set
        {
            if (SetProperty(ref _isStatsView, value))
            {
                OnPropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsListView => !IsStatsView;

    public ICommand ShowListCommand => _showList ??= new DelegateCommand(() => IsStatsView = false);

    public ICommand ShowStatsCommand => _showStats ??= new DelegateCommand(() => IsStatsView = true);

    public IReadOnlyList<DebriefMapCoverageRowViewModel> MapCoverage { get; private set; } = [];

    public bool HasMapCoverage => MapCoverage.Count > 0;

    /// <summary>The list's per-map strip counts raids in play, so it is left off the archive list.</summary>
    public bool ShowsMapStatsStrip => HasMapStats && !ShowArchived;

    public DebriefValueChartViewModel ValueChart { get; } = new();

    public DebriefSurvivalChartViewModel SurvivalChart { get; } = new();

    /// <summary>Whether the list shows the archive instead of the raids in play.</summary>
    public bool ShowArchived
    {
        get => _showArchived;
        set
        {
            if (SetProperty(ref _showArchived, value))
            {
                ApplyFilters();
                OnPropertyChanged(nameof(ArchiveToggleLabel));
            }
        }
    }

    public int ArchivedCount => ContextRecords.Count(record => record.IsArchived);

    public bool HasArchived => ArchivedCount > 0 || ShowArchived;

    public string ArchiveToggleLabel => ShowArchived
        ? "Back to raids"
        : $"Archived ({ArchivedCount.ToString(CultureInfo.CurrentCulture)})";

    public ICommand ToggleArchivedCommand => _toggleArchived ??= new DelegateCommand(() => ShowArchived = !ShowArchived);

    public bool SelectedIsArchived => SelectedRecord?.IsArchived ?? false;

    public string ArchiveActionLabel => SelectedIsArchived ? "Restore raid" : "Archive raid";

    public string ArchiveHint => SelectedIsArchived
        ? "Archived: left out of the list, stats and charts."
        : "Hides it from the list, stats and charts. Nothing is deleted.";

    public ICommand ArchiveSelectedCommand => _archiveSelected ??= new AsyncDelegateCommand(ToggleArchiveSelectedAsync);

    /// <summary>What the selected raid's photographed extract lists offered.</summary>
    public string SelectedOfferedLabel => SelectedRecord switch
    {
        null => string.Empty,
        { OfferedExtracts: { Count: > 0 } offered } => "Offered: " + string.Join(", ", offered),
        _ => "Offered extracts not recorded for this raid.",
    };

    public string SelectedExtractUsedLabel => SelectedRecord?.UsedExtract ?? "Not recorded";

    public bool HasSelectedExtractUsed => SelectedRecord?.UsedExtract is not null;

    public IReadOnlyList<DebriefExtractChoiceViewModel> SelectedExtractChoices { get; private set; } = [];

    public bool HasSelectedExtractChoices => SelectedExtractChoices.Count > 0;

    public string ExtractUsedInput
    {
        get => _extractUsedInput;
        set => SetProperty(ref _extractUsedInput, value ?? string.Empty);
    }

    public ICommand SaveExtractUsedCommand => _saveExtractUsed ??= new AsyncDelegateCommand(() => SaveExtractUsedAsync(ExtractUsedInput));

    private DebriefRaidRecord? SelectedRecord =>
        _selected is null ? null : _allRecords.FirstOrDefault(record => record.Raid.Id == _selected.Id);

    private static RaidCoverageInput ToCoverageInput(DebriefRaidRecord record) => new(
        record.Raid.Id,
        record.Raid.MapId,
        record.Raid.StartedUtc,
        record.Raid.Outcome,
        record.OfferedExtracts,
        record.UsedExtract,
        record.Manual?.ValueRoubles);

    /// <summary>Rebuilds the coverage table and both charts from the filtered raids in play.</summary>
    private void RebuildCoverage(IReadOnlyList<DebriefRaidRecord> active)
    {
        var inputs = active.Select(ToCoverageInput).ToArray();
        var maps = RaidCoverage.ByMap(inputs);
        MapCoverage = [.. maps.Select(CoverageRow)];
        ValueChart.Update(RaidCoverage.ValueSeries(inputs), CoverageMapLabel);
        SurvivalChart.Update(maps, CoverageMapLabel);
        OnPropertyChanged(nameof(MapCoverage));
        OnPropertyChanged(nameof(HasMapCoverage));
        OnPropertyChanged(nameof(ShowsMapStatsStrip));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasArchived));
        OnPropertyChanged(nameof(ArchiveToggleLabel));
        RaiseContext();
    }

    private string CoverageMapLabel(string? mapId) => mapId is { Length: > 0 } id ? MapLabel(id) : "Unknown map";

    private DebriefMapCoverageRowViewModel CoverageRow(RaidMapCoverage map)
    {
        var culture = CultureInfo.CurrentCulture;
        var extracts = map.Offered.Count == 0
            ? "Offered not recorded"
            : string.Create(culture, $"{map.UsedOfOffered.Count} of {map.Offered.Count} used");
        var note = map.RaidsWithOffered == map.Raids
            ? string.Empty
            : string.Create(culture, $"{map.RaidsWithOffered} of {CountLabel(map.Raids, "raid")}");
        if (map.UsedUnchecked > 0)
        {
            note = (note.Length == 0 ? string.Empty : note + " · ")
                + string.Create(culture, $"{map.UsedUnchecked} used, no list");
        }

        return new(
            MapLabel(map.MapId),
            CountLabel(map.Raids, "raid"),
            map.Extracted.ToString(culture),
            map.Died.ToString(culture),
            map.SurvivalRate is { } rate ? DebriefSurvivalChartViewModel.Percent(rate) : "—",
            extracts,
            note);
    }

    /// <summary>The picks shown under "Extract used": what the raid offered, else the planned route's extract.</summary>
    private void RebuildExtractChoices()
    {
        var record = SelectedRecord;
        var names = new List<string>(record?.OfferedExtracts ?? []);
        if (_selectedPlannedRoute?.Extract is { } planned && RaidExtractUsed.Normalize(planned) is { } name
            && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name);
        }

        SelectedExtractChoices = record is null
            ? []
            : [.. names.Select(choice => new DebriefExtractChoiceViewModel(
                choice,
                new AsyncDelegateCommand(() => SaveExtractUsedAsync(choice)),
                string.Equals(choice, record.UsedExtract, StringComparison.OrdinalIgnoreCase)))];
        ExtractUsedInput = string.Empty;
        OnPropertyChanged(nameof(SelectedExtractChoices));
        OnPropertyChanged(nameof(HasSelectedExtractChoices));
        OnPropertyChanged(nameof(SelectedOfferedLabel));
        OnPropertyChanged(nameof(SelectedExtractUsedLabel));
        OnPropertyChanged(nameof(HasSelectedExtractUsed));
        OnPropertyChanged(nameof(SelectedIsArchived));
        OnPropertyChanged(nameof(ArchiveActionLabel));
        OnPropertyChanged(nameof(ArchiveHint));
    }

    /// <summary>Records the extract used on the selected raid; an empty name clears it.</summary>
    internal async Task SaveExtractUsedAsync(string? extract)
    {
        if (_selected is null)
        {
            return;
        }

        var name = RaidExtractUsed.Normalize(extract);
        if (name is null && !string.IsNullOrWhiteSpace(extract))
        {
            Status = $"Enter an extract name up to {RaidExtractUsed.MaximumLength} characters.";
            return;
        }

        var raidId = _selected.Id;
        var now = _clock.GetUtcNow().ToUniversalTime();
        await _raidHistoryService.RecordEventAsync(
            raidId,
            RaidExtractUsed.EventType,
            now,
            new RaidExtractUsed(name, now).ToPayload(),
            CancellationToken.None).ConfigureAwait(true);
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        await SelectRaidAsync(raidId, CancellationToken.None).ConfigureAwait(true);
        Status = name is null ? "Cleared the extract used." : $"Extract used: {name}.";
    }

    /// <summary>Archives the selected raid, or restores it when it is archived.</summary>
    internal async Task ToggleArchiveSelectedAsync()
    {
        if (SelectedRecord is not { } record)
        {
            return;
        }

        var archive = !record.IsArchived;
        var raidId = record.Raid.Id;
        var now = _clock.GetUtcNow().ToUniversalTime();
        await _raidHistoryService.RecordEventAsync(
            raidId,
            RaidArchive.EventType,
            now,
            new RaidArchive(archive, now).ToPayload(),
            CancellationToken.None).ConfigureAwait(true);
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        // The raid has just left the list being shown; keep it in the panel so the action can be
        // reversed from where it was taken.
        await SelectRaidAsync(raidId, CancellationToken.None).ConfigureAwait(true);
        Status = archive ? "Raid archived. Restore it from Archived." : "Raid restored.";
    }
}
