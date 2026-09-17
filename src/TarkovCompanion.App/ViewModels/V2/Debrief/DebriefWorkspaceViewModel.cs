using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

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
    private RaidHistoryEntry? _selected;
    private IReadOnlyList<ScreenshotPosition> _selectedPositions = [];
    private string _status = "Raid history has not been loaded.";
    private string _correctedOutcome = string.Empty;
    private string _correctedNotes = string.Empty;
    private Func<string, string?> _mapName = _ => null;

    public DebriefWorkspaceViewModel(
        IRaidHistoryService raidHistoryService,
        AppDataPaths paths,
        TimeProvider? clock = null)
    {
        _raidHistoryService = raidHistoryService ?? throw new ArgumentNullException(nameof(raidHistoryService));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? TimeProvider.System;

        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        SaveCorrectionCommand = new AsyncDelegateCommand(SaveCorrectionAsync);
        ExportCsvCommand = new AsyncDelegateCommand(ExportCsvAsync);
        ExportJsonCommand = new AsyncDelegateCommand(ExportJsonAsync);
    }

    public IReadOnlyList<DebriefRaidRowViewModel> Raids { get; private set; } = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool HasRaids => Raids.Count > 0;

    public bool HasNoRaids => !HasRaids;

    public bool HasSelection => _selected is not null;

    public bool HasNoSelection => !HasSelection;

    public string SelectedMapLabel => _selected?.MapId is { } mapId ? MapLabel(mapId) : string.Empty;

    public string SelectedStartedLabel => _selected?.StartedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown";

    /// <summary>Package 17 (home): said once, beside the field it explains, instead of in the status line.</summary>
    public string OutcomeHint { get; } = "The game doesn't record outcomes; enter one by hand.";

    public string SelectedModeLabel => _selected?.Mode ?? string.Empty;

    public string SelectedDurationLabel => Duration(_selected);

    public string SelectedOutcomeLabel => _selected?.Outcome ?? "Not recorded";

    public string SelectedNotesLabel => _selected?.Notes ?? string.Empty;

    public string SelectedPathLabel => _selectedPositions.Count switch
    {
        0 => "No screenshots recorded for this raid.",
        1 => "1 screenshot recorded.",
        var count => $"{count.ToString(CultureInfo.CurrentCulture)} screenshots recorded.",
    };

    public string ScanBreakdownNotice { get; } = "Per-event scan/loot detail isn't available yet.";

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
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            Raids = raids
                .Select(raid => new DebriefRaidRowViewModel(
                    raid.Id,
                    raid.MapId is { } mapId ? MapLabel(mapId) : "Unknown map",
                    raid.Mode,
                    raid.StartedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown",
                    raid.EndedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "In progress",
                    Duration(raid),
                    raid.Outcome ?? "Not recorded")
                {
                    SelectCommand = new AsyncDelegateCommand(() => SelectRaidAsync(raid.Id, CancellationToken.None)),
                    IsSelected = _selected?.Id == raid.Id,
                })
                .ToArray();
            if (_selected is null && Raids.Count > 0)
            {
                // Master-detail: the newest raid is what a debrief is almost always about.
                await SelectRaidAsync(Raids[0].RaidId, cancellationToken).ConfigureAwait(true);
            }

            Status = Raids.Count == 0
                ? "No raids recorded yet."
                : Raids.Count == 1 ? "1 raid" : $"{Raids.Count.ToString(CultureInfo.CurrentCulture)} raids";
            RaiseAll();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Raids = [];
            Status = $"Raid history unavailable: {exception.Message}";
            RaiseAll();
        }
    }

    public async Task SelectRaidAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
        _selected = raids.FirstOrDefault(raid => raid.Id == raidId);
        _selectedPositions = _selected is null
            ? []
            : await _raidHistoryService.ListPositionsAsync(raidId, cancellationToken).ConfigureAwait(true);
        CorrectedOutcome = _selected?.Outcome ?? string.Empty;
        CorrectedNotes = _selected?.Notes ?? string.Empty;
        Raids = Raids.Select(row => row with { IsSelected = row.RaidId == raidId }).ToArray();
        RaiseAll();
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
                $"{_clock.GetUtcNow():yyyyMMdd-HHmmss}-{fileName}");
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
        return elapsed <= TimeSpan.Zero
            ? "Unknown"
            : elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\h\ mm\m" : @"mm\m\ ss\s", CultureInfo.InvariantCulture);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Raids));
        OnPropertyChanged(nameof(HasRaids));
        OnPropertyChanged(nameof(HasNoRaids));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(SelectedStartedLabel));
        OnPropertyChanged(nameof(SelectedMapLabel));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(SelectedDurationLabel));
        OnPropertyChanged(nameof(SelectedOutcomeLabel));
        OnPropertyChanged(nameof(SelectedNotesLabel));
        OnPropertyChanged(nameof(SelectedPathLabel));
    }
}
