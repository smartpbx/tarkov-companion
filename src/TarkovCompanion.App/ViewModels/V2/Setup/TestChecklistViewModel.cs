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

/// <summary>One need chip: No game needed, Squad or tablet.</summary>
public sealed class TestChecklistNeedFilterViewModel : BindableViewModel
{
    private string _label = string.Empty;
    private bool _isCurrent;

    public TestChecklistNeedFilterViewModel(TestChecklistNeedFilter filter, Action<TestChecklistNeedFilter> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Filter = filter;
        SelectCommand = new DelegateCommand(() => select(filter));
    }

    public TestChecklistNeedFilter Filter { get; }

    public string AutomationId => $"v2-test-checklist-need-{Filter.ToString().ToLowerInvariant()}";

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

/// <summary>
/// One area: its heading in the list, and its line in the rail with tested over total and what is
/// broken or needs work. The counts are over every item in the area, whatever the filters show.
/// </summary>
public sealed class TestChecklistAreaViewModel : BindableViewModel
{
    private readonly IReadOnlyList<TestChecklistItemViewModel> _all;
    private IReadOnlyList<TestChecklistItemViewModel> _items = [];
    private string _countLine = string.Empty;
    private string _railCount = string.Empty;
    private int _broken;
    private int _needsWork;
    private bool _isComplete;

    public TestChecklistAreaViewModel(string name, IReadOnlyList<TestChecklistItemViewModel> all, Action<TestChecklistAreaViewModel>? jump = null)
    {
        Name = name;
        _all = all;
        JumpCommand = new DelegateCommand(() => jump?.Invoke(this));
        Refresh();
    }

    public string Name { get; }

    public string AutomationId => $"v2-test-checklist-area-{string.Concat(Name.ToLowerInvariant().Where(char.IsLetterOrDigit))}";

    /// <summary>The area's items the filters let through.</summary>
    public IReadOnlyList<TestChecklistItemViewModel> Items
    {
        get => _items;
        internal set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsShown));
            }
        }
    }

    /// <summary>False while the filters hide every item in it; its rail line dims and does nothing.</summary>
    public bool IsShown => _items.Count > 0;

    public int Tested { get; private set; }

    public int Total => _all.Count;

    /// <summary>"12 of 40 tested", under the heading in the list.</summary>
    public string CountLine
    {
        get => _countLine;
        private set => SetProperty(ref _countLine, value);
    }

    /// <summary>"12/40", in the rail.</summary>
    public string RailCount
    {
        get => _railCount;
        private set => SetProperty(ref _railCount, value);
    }

    public int Broken
    {
        get => _broken;
        private set
        {
            if (SetProperty(ref _broken, value))
            {
                OnPropertyChanged(nameof(HasBroken));
                OnPropertyChanged(nameof(BrokenLine));
            }
        }
    }

    public int NeedsWork
    {
        get => _needsWork;
        private set
        {
            if (SetProperty(ref _needsWork, value))
            {
                OnPropertyChanged(nameof(HasNeedsWork));
                OnPropertyChanged(nameof(NeedsWorkLine));
            }
        }
    }

    public bool HasBroken => _broken > 0;

    public bool HasNeedsWork => _needsWork > 0;

    public string BrokenLine => TestChecklistText.RailBroken(_broken);

    public string NeedsWorkLine => TestChecklistText.RailNeedsWork(_needsWork);

    /// <summary>Every item has a result: the rail count turns to the success tone.</summary>
    public bool IsComplete
    {
        get => _isComplete;
        private set => SetProperty(ref _isComplete, value);
    }

    public ICommand JumpCommand { get; }

    internal void Refresh()
    {
        Tested = _all.Count(item => item.Status != TestStatus.Untested);
        CountLine = TestChecklistText.AreaCount(Tested, _all.Count);
        RailCount = TestChecklistText.RailCount(Tested, _all.Count);
        Broken = _all.Count(item => item.Status == TestStatus.Broken);
        NeedsWork = _all.Count(item => item.Status == TestStatus.NeedsWork);
        IsComplete = _all.Count > 0 && Tested == _all.Count;
    }
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
/// With 559 items the list is one flat run of area headings and items (<see cref="Rows"/>), so the
/// view can realise only the rows on screen; nested per-area lists could not be virtualised.
/// </remarks>
public sealed class TestChecklistViewModel : BindableViewModel
{
    private readonly TestChecklistCatalog _catalog;
    private readonly TestChecklistResultsStore _store;
    private readonly string _build;
    private readonly TimeProvider _clock;
    private readonly List<TestChecklistItemViewModel> _items;
    private readonly IReadOnlyList<TestChecklistAreaViewModel> _rail;
    private TestChecklistFilter _filter = TestChecklistFilter.All;
    private TestChecklistNeedFilter _needFilter = TestChecklistNeedFilter.Any;
    private string _search = string.Empty;
    private bool _expandAll;
    private IReadOnlyList<TestChecklistAreaViewModel> _areas = [];
    private IReadOnlyList<object> _rows = [];
    private IReadOnlyList<TestChecklistItemViewModel> _shown = [];
    private TestChecklistItemViewModel? _current;
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
        NeedFilters = [.. Enum.GetValues<TestChecklistNeedFilter>().Select(filter => new TestChecklistNeedFilterViewModel(filter, SelectNeed))];
        _rail =
        [
            .. _items
                .GroupBy(item => item.Item.Area, StringComparer.Ordinal)
                .Select(area => new TestChecklistAreaViewModel(area.Key, [.. area], JumpTo)),
        ];
        NextUntestedCommand = new DelegateCommand(NextUntested);
        ExpandAllCommand = new DelegateCommand(() => ExpandAll = !ExpandAll);
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

    public IReadOnlyList<TestChecklistNeedFilterViewModel> NeedFilters { get; }

    public TestChecklistNeedFilter NeedFilter => _needFilter;

    /// <summary>The areas with at least one item the filters let through, in file order.</summary>
    public IReadOnlyList<TestChecklistAreaViewModel> Areas
    {
        get => _areas;
        private set => SetProperty(ref _areas, value);
    }

    /// <summary>Every area, for the rail: it keeps its place while a filter hides the area's items.</summary>
    public IReadOnlyList<TestChecklistAreaViewModel> Rail => _rail;

    /// <summary>What the list draws, in order: each shown area's heading followed by its shown items.</summary>
    public IReadOnlyList<object> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>Text typed into Search; every word must appear in the feature, a step, the expectation or the id.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Opens every Works and Skipped row as a full card.</summary>
    public bool ExpandAll
    {
        get => _expandAll;
        set
        {
            if (SetProperty(ref _expandAll, value))
            {
                foreach (var item in _items)
                {
                    item.ExpandAll = value;
                }
            }
        }
    }

    public ICommand ExpandAllCommand { get; }

    public ICommand NextUntestedCommand { get; }

    /// <summary>The item Next untested or an area jump last landed on, or the last one marked.</summary>
    public TestChecklistItemViewModel? Current => _current;

    /// <summary>Asks the view to bring the row at this index in <see cref="Rows"/> to the top of the list.</summary>
    public event Action<int>? ScrollRequested;

    public bool ShowsNothing => _rows.Count == 0;

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

    public void SelectNeed(TestChecklistNeedFilter filter)
    {
        _needFilter = filter;
        ApplyFilter();
        OnPropertyChanged(nameof(NeedFilter));
    }

    /// <summary>
    /// Moves to the first untested item after the current one among those shown, wrapping at the
    /// end, so testing runs item to item without scrolling. Says so when none is left.
    /// </summary>
    public void NextUntested()
    {
        var start = _current is null ? 0 : IndexOf(_shown, _current) + 1;
        for (var step = 0; step < _shown.Count; step++)
        {
            var candidate = _shown[(start + step) % _shown.Count];
            if (candidate.Status == TestStatus.Untested)
            {
                ActionStatus = null;
                Land(candidate, candidate);
                return;
            }
        }

        ActionStatus = TestChecklistText.NoneUntested;
    }

    /// <summary>Brings an area's heading to the top of the list; its first shown item becomes current.</summary>
    public void JumpTo(TestChecklistAreaViewModel area)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (!area.IsShown)
        {
            return;
        }

        Land(area, area.Items[0]);
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

    /// <summary>Whether the need filter and the search let an item through, whatever its status.</summary>
    private bool InScope(TestChecklistItemViewModel item) =>
        TestChecklistNarrowing.Passes(item.Item, _needFilter) && TestChecklistNarrowing.Matches(item.Item, _search);

    private void ApplyFilter()
    {
        foreach (var chip in Filters)
        {
            chip.IsCurrent = chip.Filter == _filter;
        }

        foreach (var chip in NeedFilters)
        {
            chip.IsCurrent = chip.Filter == _needFilter;
        }

        var rows = new List<object>();
        var shown = new List<TestChecklistItemViewModel>();
        foreach (var area in _rail)
        {
            area.Items = [.. _items.Where(item => ReferenceEquals(AreaOf(item), area) && Passes(item, _filter) && InScope(item))];
            if (area.Items.Count > 0)
            {
                rows.Add(area);
                rows.AddRange(area.Items);
                shown.AddRange(area.Items);
            }
        }

        _shown = shown;
        Areas = [.. _rail.Where(area => area.IsShown)];
        Rows = rows;
        if (_current is not null && IndexOf(_shown, _current) < 0)
        {
            SetCurrent(null);
        }

        RefreshCounts();
        OnPropertyChanged(nameof(ShowsNothing));
        OnPropertyChanged(nameof(NothingLine));
    }

    private TestChecklistAreaViewModel? AreaOf(TestChecklistItemViewModel item) =>
        _rail.FirstOrDefault(area => string.Equals(area.Name, item.Item.Area, StringComparison.Ordinal));

    private void Land(object row, TestChecklistItemViewModel item)
    {
        SetCurrent(item);
        var index = IndexOf(_rows, row);
        if (index >= 0)
        {
            ScrollRequested?.Invoke(index);
        }
    }

    private void SetCurrent(TestChecklistItemViewModel? item)
    {
        if (ReferenceEquals(_current, item))
        {
            return;
        }

        if (_current is not null)
        {
            _current.IsCurrent = false;
        }

        _current = item;
        if (item is not null)
        {
            item.IsCurrent = true;
        }

        OnPropertyChanged(nameof(Current));
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, object value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (ReferenceEquals(list[index], value))
            {
                return index;
            }
        }

        return -1;
    }

    private void Refresh()
    {
        Summary = TestChecklistSummary.Of(_catalog.Items, _store.All, _build);
        ProgressLine = TestChecklistText.Progress(Summary);
        OnPropertyChanged(nameof(Summary));
        RefreshCounts();
        foreach (var area in _rail)
        {
            area.Refresh();
        }
    }

    /// <summary>The status chips count within the need filter and search, so a chip says what pressing it shows.</summary>
    private void RefreshCounts()
    {
        var scope = _items.Where(InScope).ToList();
        foreach (var chip in Filters)
        {
            chip.Label = TestChecklistText.FilterLabel(chip.Filter, scope.Count(item => Passes(item, chip.Filter)));
        }

        foreach (var chip in NeedFilters)
        {
            chip.Label = TestChecklistText.NeedFilterLabel(chip.Filter, _items.Count(item => TestChecklistNarrowing.Passes(item.Item, chip.Filter)));
        }
    }

    private void OnItemChanged(TestChecklistItemViewModel item)
    {
        ActionStatus = null;
        SetCurrent(item);
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
