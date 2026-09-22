using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed class QuestScreenshotCandidateViewModel(QuestListCandidate candidate)
{
    public string TaskId => candidate.TaskId;
    public string QuestName => candidate.QuestName;
    public string Confidence => candidate.Confidence.ToString("P0", CultureInfo.CurrentCulture);
    public string Label => $"{QuestName} · {Confidence}";
}

public sealed class QuestScreenshotLineViewModel : BindableViewModel
{
    private readonly QuestScreenshotSyncViewModel _owner;
    private QuestScreenshotCandidateViewModel? _selectedCandidate;
    private bool _isConfirmed;

    internal QuestScreenshotLineViewModel(QuestListLineMatch line, QuestScreenshotSyncViewModel owner)
    {
        _owner = owner;
        OcrLine = line.OcrLine;
        Kind = line.Kind;
        Candidates = line.Candidates.Select(candidate => new QuestScreenshotCandidateViewModel(candidate)).ToArray();
        _selectedCandidate = Candidates.FirstOrDefault();
        _isConfirmed = line.Kind == QuestListLineKind.Matched;
        ConfirmCommand = new AsyncDelegateCommand(ConfirmAsync);
    }

    public string OcrLine { get; }
    public QuestListLineKind Kind { get; }
    public IReadOnlyList<QuestScreenshotCandidateViewModel> Candidates { get; }
    public bool IsMatched => Kind == QuestListLineKind.Matched;
    public bool IsAmbiguous => Kind == QuestListLineKind.Ambiguous;
    public bool IsUnmatched => Kind == QuestListLineKind.Unmatched;
    public string MatchedName => Candidates.FirstOrDefault()?.QuestName ?? OcrLine;
    public string MatchedConfidence => Candidates.FirstOrDefault()?.Confidence ?? string.Empty;

    public QuestScreenshotCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value) && IsConfirmed)
            {
                _owner.RefreshHistoryAsync().Observe("quest-sync", "refresh selected quest history");
            }
        }
    }

    public bool IsConfirmed
    {
        get => _isConfirmed;
        private set
        {
            if (SetProperty(ref _isConfirmed, value))
            {
                OnPropertyChanged(nameof(ConfirmLabel));
            }
        }
    }

    public string ConfirmLabel => IsConfirmed ? "Included" : "Use match";
    public ICommand ConfirmCommand { get; }

    internal string? ConfirmedTaskId => IsConfirmed ? SelectedCandidate?.TaskId : null;

    private async Task ConfirmAsync()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        IsConfirmed = true;
        await _owner.RefreshHistoryAsync().ConfigureAwait(true);
    }
}

/// <summary>Setup › Progress flow for onboarding from the game's own TASKS screenshots.</summary>
public sealed class QuestScreenshotSyncViewModel : BindableViewModel
{
    private readonly QuestScreenshotSyncService _sync;
    private readonly IQuestScreenshotImageSource _images;
    private readonly Func<string?> _screenshotRoot;
    private readonly TimeProvider _clock;
    private QuestHistoryInferencePreview? _history;
    private bool _hasPreview;
    private bool _showEmptyOffer;
    private string _status = "Choose TASKS screenshots to begin.";
    private int _recentMinutes = 10;

    public QuestScreenshotSyncViewModel(
        QuestScreenshotSyncService sync,
        IQuestScreenshotImageSource images,
        Func<string?> screenshotRoot,
        TimeProvider clock)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _screenshotRoot = screenshotRoot ?? throw new ArgumentNullException(nameof(screenshotRoot));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        PickCommand = new AsyncDelegateCommand(PickAsync);
        RecentCommand = new AsyncDelegateCommand(RecentAsync);
        ApplyCommand = new AsyncDelegateCommand(ApplyAsync);
        CancelCommand = new DelegateCommand(Cancel);
        DismissOfferCommand = new DelegateCommand(() => ShowEmptyOffer = false);
    }

    public ObservableCollection<QuestScreenshotLineViewModel> Matched { get; } = [];
    public ObservableCollection<QuestScreenshotLineViewModel> Ambiguous { get; } = [];
    public ObservableCollection<QuestScreenshotLineViewModel> Unmatched { get; } = [];
    public IReadOnlyList<int> RecentMinuteChoices { get; } = [5, 10, 15, 30];

    /// <summary>Assigned by the attached view because the file picker belongs to its window.</summary>
    public Func<Task<IReadOnlyList<string>>> ChooseFiles { get; set; } =
        () => Task.FromResult<IReadOnlyList<string>>([]);

    public ICommand PickCommand { get; }
    public ICommand RecentCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DismissOfferCommand { get; }

    public bool HasPreview
    {
        get => _hasPreview;
        private set => SetProperty(ref _hasPreview, value);
    }

    public bool ShowEmptyOffer
    {
        get => _showEmptyOffer;
        private set => SetProperty(ref _showEmptyOffer, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public int RecentMinutes
    {
        get => _recentMinutes;
        set => SetProperty(ref _recentMinutes, value);
    }

    public bool HasMatched => Matched.Count > 0;
    public bool HasAmbiguous => Ambiguous.Count > 0;
    public bool HasUnmatched => Unmatched.Count > 0;
    public string MatchedHeading => $"Matched · {Matched.Count}";
    public string AmbiguousHeading => $"Confirm these · {Ambiguous.Count}";
    public string UnmatchedHeading => $"Not found · {Unmatched.Count}";
    public string HistorySummary => _history is null
        ? "No progress changes previewed."
        : $"{_history.ActiveChanges} active · {_history.EarlierQuestChanges} earlier completed";

    public async Task RefreshOfferAsync()
    {
        try
        {
            ShowEmptyOffer = await _sync.HasNoRecordedProgressAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Quest progress is not ready: {exception.Message}";
        }
    }

    public Task LoadFixtureAsync(IEnumerable<string> ocrLines) =>
        LoadPreviewAsync(_sync.AnalyzeLinesAsync(
            ocrLines,
            imageCount: 1,
            ocrEngine: "fixture OCR",
            CancellationToken.None));

    internal async Task RefreshHistoryAsync()
    {
        _history = await _sync.PreviewSelectionAsync(ConfirmedTaskIds(), CancellationToken.None).ConfigureAwait(true);
        OnPropertyChanged(nameof(HistorySummary));
    }

    private async Task PickAsync()
    {
        ShowEmptyOffer = false;
        var paths = await ChooseFiles().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            Status = "No screenshots selected.";
            return;
        }

        Status = $"Reading {paths.Count} screenshot{(paths.Count == 1 ? string.Empty : "s")}…";
        var loaded = await _images.LoadFilesAsync(paths, CancellationToken.None).ConfigureAwait(true);
        await AnalyzeLoadedAsync(loaded).ConfigureAwait(true);
    }

    private async Task RecentAsync()
    {
        ShowEmptyOffer = false;
        Status = "Looking for recent screenshots…";
        var loaded = await _images.LoadRecentAsync(
            _screenshotRoot(),
            _clock.GetUtcNow().AddMinutes(-RecentMinutes),
            CancellationToken.None).ConfigureAwait(true);
        await AnalyzeLoadedAsync(loaded).ConfigureAwait(true);
    }

    private async Task AnalyzeLoadedAsync(QuestScreenshotImageLoad loaded)
    {
        if (loaded.Error is { } error)
        {
            Status = error;
            return;
        }

        if (loaded.Images.Count == 0)
        {
            Status = loaded.Skipped > 0 ? "No readable screenshots found." : "No recent screenshots found.";
            return;
        }

        try
        {
            await LoadPreviewAsync(_sync.AnalyzeAsync(loaded.Images, CancellationToken.None)).ConfigureAwait(true);
            if (loaded.Skipped > 0)
            {
                Status += $" · {loaded.Skipped} skipped";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Could not read quests: {exception.Message}";
        }
    }

    private async Task LoadPreviewAsync(Task<QuestScreenshotSyncPreview> pending)
    {
        var preview = await pending.ConfigureAwait(true);
        Matched.Clear();
        Ambiguous.Clear();
        Unmatched.Clear();
        foreach (var line in preview.Lines)
        {
            var row = new QuestScreenshotLineViewModel(line, this);
            CollectionFor(line.Kind).Add(row);
        }

        _history = preview.History;
        HasPreview = true;
        Status = $"Read {preview.ImageCount} screenshot{(preview.ImageCount == 1 ? string.Empty : "s")} · {preview.OcrEngine}";
        NotifyPreview();
    }

    private async Task ApplyAsync()
    {
        var ids = ConfirmedTaskIds();
        if (ids.Count == 0)
        {
            Status = "Confirm at least one quest first.";
            return;
        }

        try
        {
            var applied = await _sync.ApplyAsync(ids, CancellationToken.None).ConfigureAwait(true);
            Status = $"Quest progress synced · {applied.Changed} changes";
            ClearPreview();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Quest progress was not synced: {exception.Message}";
        }
    }

    private void Cancel()
    {
        ClearPreview();
        Status = "Sync cancelled. Nothing changed.";
    }

    private void ClearPreview()
    {
        Matched.Clear();
        Ambiguous.Clear();
        Unmatched.Clear();
        _history = null;
        HasPreview = false;
        NotifyPreview();
    }

    private List<string> ConfirmedTaskIds() =>
        Matched.Concat(Ambiguous)
            .Select(row => row.ConfirmedTaskId)
            .Where(taskId => taskId is not null)
            .Select(taskId => taskId!)
            .ToList();

    private ObservableCollection<QuestScreenshotLineViewModel> CollectionFor(QuestListLineKind kind) => kind switch
    {
        QuestListLineKind.Matched => Matched,
        QuestListLineKind.Ambiguous => Ambiguous,
        _ => Unmatched,
    };

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(HasMatched));
        OnPropertyChanged(nameof(HasAmbiguous));
        OnPropertyChanged(nameof(HasUnmatched));
        OnPropertyChanged(nameof(MatchedHeading));
        OnPropertyChanged(nameof(AmbiguousHeading));
        OnPropertyChanged(nameof(UnmatchedHeading));
        OnPropertyChanged(nameof(HistorySummary));
    }
}
