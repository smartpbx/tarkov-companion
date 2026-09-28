using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.TestChecklist;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One filter chip: its label with a count, and whether it is the one applied.</summary>
public sealed class TestChecklistFilterViewModel : BindableViewModel
{
    private string _label = string.Empty;
    private bool _isCurrent;

    public TestChecklistFilterViewModel(TestChecklistFilter filter, Action<TestChecklistFilter> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Filter = filter;
        SelectCommand = new DelegateCommand(() => select(filter));
    }

    public TestChecklistFilter Filter { get; }

    public string AutomationId => $"v2-test-checklist-filter-{Filter.ToString().ToLowerInvariant()}";

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>One area heading, its tested count over every item in it, and the items the filter lets through.</summary>
public sealed class TestChecklistAreaViewModel : BindableViewModel
{
    private readonly IReadOnlyList<TestChecklistItemViewModel> _all;
    private string _countLine = string.Empty;

    public TestChecklistAreaViewModel(string name, IReadOnlyList<TestChecklistItemViewModel> all, IReadOnlyList<TestChecklistItemViewModel> shown)
    {
        Name = name;
        _all = all;
        Items = shown;
        Refresh();
    }

    public string Name { get; }

    public IReadOnlyList<TestChecklistItemViewModel> Items { get; }

    public string CountLine
    {
        get => _countLine;
        private set => SetProperty(ref _countLine, value);
    }

    internal void Refresh() => CountLine = TestChecklistText.AreaCount(_all.Count(item => item.Status != TestStatus.Untested), _all.Count);
}

/// <summary>
/// Setup › Test checklist (dev and rough builds): every feature to try by hand, grouped by area,
/// with a result and a note each, and the results as Markdown to paste into an issue.
/// </summary>
/// <remarks>
/// The items ship as data in <c>Assets/TestChecklist</c>; the results are this PC's, in
/// <c>Config/test-checklist-results.json</c>. A result recorded on another build keeps its status
/// and shows Retest, because a build that changed the feature may have fixed or broken it.
/// The filter is applied when it is chosen, not on every press: an item marked Works under
/// "Untested" stays on screen until the filter is chosen again, so the press is seen to land.
/// </remarks>
public sealed class TestChecklistViewModel : BindableViewModel
{
    private readonly TestChecklistCatalog _catalog;
    private readonly TestChecklistResultsStore _store;
    private readonly string _build;
    private readonly TimeProvider _clock;
    private readonly List<TestChecklistItemViewModel> _items;
    private TestChecklistFilter _filter = TestChecklistFilter.All;
    private IReadOnlyList<TestChecklistAreaViewModel> _areas = [];
    private string _progressLine = string.Empty;
    private string? _actionStatus;
    private bool _isConfirmingClear;

    public TestChecklistViewModel(
        TestChecklistCatalog catalog,
        TestChecklistResultsStore store,
        string build,
        TimeProvider? clock = null,
        Func<string, bool>? navigate = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _build = string.IsNullOrWhiteSpace(build) ? "unknown" : build;
        _clock = clock ?? TimeProvider.System;
        _items = [.. catalog.Items.Select(item => new TestChecklistItemViewModel(item, store, _build, _clock, navigate, OnItemChanged))];
        Filters = [.. Enum.GetValues<TestChecklistFilter>().Select(filter => new TestChecklistFilterViewModel(filter, Select))];
        CopyCommand = new AsyncDelegateCommand(CopyAsync);
        ExportCommand = new AsyncDelegateCommand(ExportAsync);
        ClearCommand = new DelegateCommand(() => IsConfirmingClear = true);
        ConfirmClearCommand = new DelegateCommand(ClearResults);
        CancelClearCommand = new DelegateCommand(() => IsConfirmingClear = false);
        Refresh();
        ApplyFilter();
    }

    public string Heading => TestChecklistText.Heading;

    public string Intro => TestChecklistText.Intro;

    public string Build => _build;

    public IReadOnlyList<TestChecklistItemViewModel> Items => _items;

    public IReadOnlyList<TestChecklistFilterViewModel> Filters { get; }

    public TestChecklistFilter Filter => _filter;

    public IReadOnlyList<TestChecklistAreaViewModel> Areas
    {
        get => _areas;
        private set => SetProperty(ref _areas, value);
    }

    public bool ShowsNothing => _areas.Count == 0;

    public string NothingLine => _items.Count == 0 ? TestChecklistText.NoItems : TestChecklistText.Nothing;

    /// <summary>"42 of 180 tested · 3 broken · 5 need work", with the retest count when there is one.</summary>
    public string ProgressLine
    {
        get => _progressLine;
        private set => SetProperty(ref _progressLine, value);
    }

    public TestChecklistSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);

    public bool HasProblems => _catalog.Problems.Count > 0;

    /// <summary>"3 items could not be read", instead of a page that failed to open.</summary>
    public string ProblemLine => HasProblems ? TestChecklistText.Unreadable(_catalog.Problems.Count) : string.Empty;

    public string ProblemDetails => string.Join(Environment.NewLine, _catalog.Problems.Take(5));

    public string? ActionStatus
    {
        get => _actionStatus;
        private set
        {
            if (SetProperty(ref _actionStatus, value))
            {
                OnPropertyChanged(nameof(HasActionStatus));
            }
        }
    }

    public bool HasActionStatus => !string.IsNullOrEmpty(_actionStatus);

    public bool IsConfirmingClear
    {
        get => _isConfirmingClear;
        private set => SetProperty(ref _isConfirmingClear, value);
    }

    public ICommand CopyCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand ConfirmClearCommand { get; }

    public ICommand CancelClearCommand { get; }

    /// <summary>Puts text on the window's clipboard; the view hands it over.</summary>
    public Func<string, Task>? Clipboard { get; set; }

    /// <summary>Asks where to save (suggested name, contents) and writes it; false when cancelled.</summary>
    public Func<string, string, Task<bool>>? SaveFile { get; set; }

    public string Markdown() => TestChecklistReport.Markdown(_catalog.Items, _store.All, _build, _clock.GetUtcNow());

    public string SuggestedFileName => $"test-checklist-{TarkovCompanion.Core.Common.LocalTime.FileStamp(_clock.GetUtcNow())}.md";

    public void Select(TestChecklistFilter filter)
    {
        _filter = filter;
        ApplyFilter();
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>Writes every note still waiting for typing to pause.</summary>
    public void FlushNotes()
    {
        foreach (var item in _items)
        {
            item.FlushNote();
        }
    }

    internal static bool Passes(TestChecklistItemViewModel item, TestChecklistFilter filter) => filter switch
    {
        TestChecklistFilter.Untested => item.Status == TestStatus.Untested,
        TestChecklistFilter.Broken => item.Status == TestStatus.Broken,
        TestChecklistFilter.NeedsWork => item.Status == TestStatus.NeedsWork,
        TestChecklistFilter.Retest => item.NeedsRetest,
        _ => true,
    };

    private void ApplyFilter()
    {
        foreach (var chip in Filters)
        {
            chip.IsCurrent = chip.Filter == _filter;
        }

        Areas =
        [
            .. _items
                .GroupBy(item => item.Item.Area, StringComparer.Ordinal)
                .Select(area => (Area: area, Shown: area.Where(item => Passes(item, _filter)).ToList()))
                .Where(area => area.Shown.Count > 0)
                .Select(area => new TestChecklistAreaViewModel(area.Area.Key, [.. area.Area], area.Shown)),
        ];
        OnPropertyChanged(nameof(ShowsNothing));
        OnPropertyChanged(nameof(NothingLine));
    }

    private void Refresh()
    {
        Summary = TestChecklistSummary.Of(_catalog.Items, _store.All, _build);
        ProgressLine = TestChecklistText.Progress(Summary);
        OnPropertyChanged(nameof(Summary));
        foreach (var chip in Filters)
        {
            chip.Label = TestChecklistText.FilterLabel(chip.Filter, _items.Count(item => Passes(item, chip.Filter)));
        }

        foreach (var area in _areas)
        {
            area.Refresh();
        }
    }

    private void OnItemChanged(TestChecklistItemViewModel item)
    {
        ActionStatus = null;
        Refresh();
    }

    private async Task CopyAsync()
    {
        FlushNotes();
        if (Clipboard is null)
        {
            return;
        }

        await Clipboard(Markdown()).ConfigureAwait(true);
        ActionStatus = TestChecklistText.Copied;
    }

    private async Task ExportAsync()
    {
        FlushNotes();
        if (SaveFile is null)
        {
            return;
        }

        try
        {
            if (await SaveFile(SuggestedFileName, Markdown()).ConfigureAwait(true))
            {
                ActionStatus = TestChecklistText.Exported;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ActionStatus = TestChecklistText.SaveFailed(exception.Message);
        }
    }

    private void ClearResults()
    {
        IsConfirmingClear = false;
        try
        {
            _store.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ActionStatus = TestChecklistText.SaveFailed(exception.Message);
            return;
        }

        foreach (var item in _items)
        {
            item.Reset();
        }

        Refresh();
        ApplyFilter();
        ActionStatus = TestChecklistText.Cleared;
    }
}
