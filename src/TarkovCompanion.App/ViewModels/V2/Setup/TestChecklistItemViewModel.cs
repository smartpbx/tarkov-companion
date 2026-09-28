using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.TestChecklist;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One numbered step, drawn as its number beside its text.</summary>
public sealed record TestChecklistStepRow(string Number, string Text);

/// <summary>One checklist item: what to do, what to see, the four result buttons and the note.</summary>
/// <remarks>
/// A status press is saved at once, stamped with the running build and the time. The note is saved
/// a moment after typing stops, and only the note changes: editing a note on an item recorded on an
/// older build must not make it look retested.
/// </remarks>
public sealed class TestChecklistItemViewModel : BindableViewModel
{
    /// <summary>How long typing has to pause before the note is written.</summary>
    public static readonly TimeSpan NoteDelay = TimeSpan.FromMilliseconds(700);

    private readonly TestChecklistResultsStore _store;
    private readonly string _build;
    private readonly TimeProvider _clock;
    private readonly Action<TestChecklistItemViewModel> _changed;
    private readonly Lock _noteGate = new();
    private TestChecklistResult? _result;
    private string _note;
    private CancellationTokenSource? _pendingNote;
    private string? _saveError;

    public TestChecklistItemViewModel(
        TestChecklistItem item,
        TestChecklistResultsStore store,
        string build,
        TimeProvider clock,
        Func<string, bool>? navigate,
        Action<TestChecklistItemViewModel> changed)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _result = store.Get(item.Id);
        _note = _result?.Note ?? string.Empty;
        Steps = [.. item.Steps.Select((step, index) => new TestChecklistStepRow($"{index + 1}.", step))];
        Needs = [.. item.Needs.Select(TestChecklistText.Need)];
        GoThereCommand = new DelegateCommand(() =>
        {
            if (item.GoTo is { } address)
            {
                navigate?.Invoke(address);
            }
        });
        CanGoThere = item.GoTo is not null && navigate is not null;
        WorksCommand = new DelegateCommand(() => Mark(TestStatus.Works));
        BrokenCommand = new DelegateCommand(() => Mark(TestStatus.Broken));
        NeedsWorkCommand = new DelegateCommand(() => Mark(TestStatus.NeedsWork));
        SkipCommand = new DelegateCommand(() => Mark(TestStatus.Skipped));
    }

    public TestChecklistItem Item { get; }

    public string Id => Item.Id;

    public string Feature => Item.Feature;

    public string Expect => Item.Expect;

    public IReadOnlyList<TestChecklistStepRow> Steps { get; }

    public IReadOnlyList<string> Needs { get; }

    public bool HasNeeds => Needs.Count > 0;

    public string? FlagLine => Item.Flag is { } flag ? TestChecklistText.Flag(flag) : null;

    public bool HasFlag => Item.Flag is not null;

    public string RefsLine => string.Join(" ", Item.Refs);

    public bool HasRefs => Item.Refs.Count > 0;

    public bool CanGoThere { get; }

    public ICommand GoThereCommand { get; }

    public ICommand WorksCommand { get; }

    public ICommand BrokenCommand { get; }

    public ICommand NeedsWorkCommand { get; }

    public ICommand SkipCommand { get; }

    public TestStatus Status => _result?.Status ?? TestStatus.Untested;

    public TestChecklistResult? Result => _result;

    public bool IsWorks => Status == TestStatus.Works;

    public bool IsBroken => Status == TestStatus.Broken;

    public bool IsNeedsWork => Status == TestStatus.NeedsWork;

    public bool IsSkipped => Status == TestStatus.Skipped;

    public bool NeedsRetest => _result?.NeedsRetest(_build) == true;

    /// <summary>"Broken on 2.0.1400 · 28/09/2026 17:40", or empty while untested.</summary>
    public string RecordedLine => _result is { Status: not TestStatus.Untested, Build: { } build, TestedUtc: { } tested }
        ? TestChecklistText.Recorded(_result.Status, build, tested)
        : string.Empty;

    public bool HasRecordedLine => RecordedLine.Length > 0;

    public string AutomationId => $"v2-test-checklist-item-{Item.Id}";

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value ?? string.Empty))
            {
                ScheduleNote();
            }
        }
    }

    /// <summary>The note write that is waiting for typing to pause, for a test to await.</summary>
    internal Task PendingNote { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Sets the status, stamped with this build and now. Pressing the lit one again puts the item
    /// back to untested, keeping the note.
    /// </summary>
    public void Mark(TestStatus status)
    {
        var next = Status == status && !NeedsRetest ? TestStatus.Untested : status;
        CancelPendingNote();
        var result = next == TestStatus.Untested
            ? new TestChecklistResult(TestStatus.Untested, NoteOrNull(), null, null)
            : new TestChecklistResult(next, NoteOrNull(), _build, _clock.GetUtcNow());
        Save(result);
    }

    /// <summary>Writes a waiting note now (the page is closing, or a test wants it on disk).</summary>
    public void FlushNote()
    {
        if (CancelPendingNote())
        {
            SaveNote();
        }
    }

    /// <summary>After Clear results: back to untested with no note, without writing anything.</summary>
    internal void Reset()
    {
        CancelPendingNote();
        _result = null;
        _note = string.Empty;
        OnPropertyChanged(nameof(Note));
        RaiseStatus();
    }

    private void ScheduleNote()
    {
        CancellationTokenSource next;
        lock (_noteGate)
        {
            _pendingNote?.Cancel();
            _pendingNote = next = new CancellationTokenSource();
        }

        PendingNote = WriteNoteLaterAsync(next);
    }

    private async Task WriteNoteLaterAsync(CancellationTokenSource pending)
    {
        try
        {
            // Back on the caller's context (the UI thread in the app), so the save and its error line are too.
            await Task.Delay(NoteDelay, _clock, pending.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_noteGate)
        {
            if (!ReferenceEquals(_pendingNote, pending))
            {
                return;
            }

            _pendingNote = null;
        }

        SaveNote();
    }

    /// <summary>True when a note write was waiting.</summary>
    private bool CancelPendingNote()
    {
        lock (_noteGate)
        {
            if (_pendingNote is null)
            {
                return false;
            }

            _pendingNote.Cancel();
            _pendingNote = null;
            return true;
        }
    }

    private void SaveNote()
    {
        var current = _result ?? new TestChecklistResult(TestStatus.Untested, null, null, null);
        _result = current with { Note = NoteOrNull() };
        try
        {
            _store.Set(Item.Id, _result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveError = TestChecklistText.SaveFailed(exception.Message);
        }
    }

    private void Save(TestChecklistResult result)
    {
        _result = result;
        try
        {
            _store.Set(Item.Id, result);
            SaveError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveError = TestChecklistText.SaveFailed(exception.Message);
        }

        RaiseStatus();
        _changed(this);
    }

    /// <summary>Why the last write failed, or null.</summary>
    public string? SaveError
    {
        get => _saveError;
        private set => SetProperty(ref _saveError, value);
    }

    private string? NoteOrNull() => string.IsNullOrWhiteSpace(_note) ? null : _note;

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsWorks));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(IsNeedsWork));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(NeedsRetest));
        OnPropertyChanged(nameof(RecordedLine));
        OnPropertyChanged(nameof(HasRecordedLine));
    }
}
