using System.Windows.Input;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>One item waiting in the compare tray.</summary>
public sealed record IntelCompareEntryViewModel(string ItemId, string Name, ICommand RemoveCommand)
{
    public string AutomationId => $"v2-intel-compare-entry-{ItemId}";

    public string RemoveLabel => $"Remove {Name} from compare";
}

/// <summary>One value in the comparison table; the best in its row is marked in words as well as colour.</summary>
public sealed record IntelCompareCellViewModel(string Text, bool IsBest)
{
    public string AccessibleText => IsBest ? $"{Text}, best" : Text;
}

public sealed record IntelCompareRowViewModel(string Label, IReadOnlyList<IntelCompareCellViewModel> Cells, bool IsAlternate);

/// <summary>
/// Intel's side-by-side comparison (#287): the player adds two or three items from their detail
/// pane, then reads them in columns with the best value in each row marked.
/// </summary>
/// <remarks>
/// The tray outlives item selection on purpose: comparing is "open A, add it, open B, add it",
/// so choosing another item must not empty it. Opening another item does close the table,
/// because the detail pane is where that item is shown and a table left over it would hide the
/// choice the player just made.
/// </remarks>
public sealed class IntelCompareViewModel : BindableViewModel
{
    private readonly Func<string, CancellationToken, Task<ItemComparisonFacts>> _load;
    private readonly List<IntelCompareEntryViewModel> _entries = [];
    private CancellationTokenSource? _loadCts;
    private string? _currentItemId;
    private string _currentName = string.Empty;
    private bool _isOpen;
    private bool _isLoading;
    private ItemComparisonTable? _table;

    public IntelCompareViewModel(Func<string, CancellationToken, Task<ItemComparisonFacts>> load)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        ToggleCurrentCommand = new DelegateCommand(ToggleCurrent);
        OpenCommand = new AsyncDelegateCommand(OpenAsync);
        CloseCommand = new DelegateCommand(Close);
        ClearCommand = new DelegateCommand(Clear);
    }

    public ICommand ToggleCurrentCommand { get; }

    public AsyncDelegateCommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand ClearCommand { get; }

    public IReadOnlyList<IntelCompareEntryViewModel> Entries => [.. _entries];

    public bool HasEntries => _entries.Count > 0;

    public bool CanOpen => _entries.Count >= 2;

    public string OpenLabel => $"Compare {_entries.Count}";

    public string TrayLabel => _entries.Count >= 2
        ? "Compare"
        : "Add one more to compare";

    public string ClearLabel => "Clear";

    public string CloseLabel => "Back to item";

    public bool CurrentIsInTray => _currentItemId is not null && Contains(_currentItemId);

    /// <summary>False when there is no item open, or the tray is full and the open item is not in it.</summary>
    public bool CanToggleCurrent => _currentItemId is not null &&
        (CurrentIsInTray || _entries.Count < ItemComparisonBuilder.MaximumItems);

    public string ToggleCurrentLabel => CurrentIsInTray
        ? "Comparing"
        : _entries.Count >= ItemComparisonBuilder.MaximumItems ? "Compare full" : "Compare";

    public string ToggleCurrentTip => CurrentIsInTray
        ? $"Remove {_currentName} from compare"
        : _entries.Count >= ItemComparisonBuilder.MaximumItems
            ? $"Compare holds {ItemComparisonBuilder.MaximumItems} items"
            : $"Add {_currentName} to compare";

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(ShowsTable));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowsTable));
            }
        }
    }

    public bool ShowsTable => IsOpen && !IsLoading && _table is not null;

    public string LoadingLabel => "Comparing…";

    public string Heading => _table?.Kind switch
    {
        ItemComparisonKind.Ammo => "Ammo side by side",
        ItemComparisonKind.Armor => "Armor side by side",
        ItemComparisonKind.Key => "Keys side by side",
        _ => "Items side by side",
    };

    /// <summary>Said when the chosen items are of different kinds and so compare on value alone.</summary>
    public string KindNote => _table is { Kind: ItemComparisonKind.Item } && MixedKinds ? "Different kinds, compared as items." : string.Empty;

    public bool HasKindNote => KindNote.Length > 0;

    public int ColumnCount => Math.Max(1, _table?.Columns.Count ?? 1);

    public IReadOnlyList<string> ColumnNames => _table?.Columns.Select(column => column.Name).ToArray() ?? [];

    public IReadOnlyList<IntelCompareRowViewModel> Rows => _table?.Rows
        .Select((row, index) => new IntelCompareRowViewModel(
            row.Label,
            [.. row.Cells.Select(cell => new IntelCompareCellViewModel(cell.Text, cell.IsBest))],
            index % 2 == 1))
        .ToArray() ?? [];

    private bool MixedKinds { get; set; }

    /// <summary>The shell calls this whenever the item in the detail pane changes (null when none).</summary>
    public void SetCurrent(string? itemId, string name)
    {
        var changed = !string.Equals(_currentItemId, itemId, StringComparison.Ordinal);
        _currentItemId = itemId;
        _currentName = name;
        if (changed && itemId is not null && IsOpen)
        {
            Close();
        }

        RaiseToggle();
    }

    public bool Contains(string itemId) =>
        _entries.Any(entry => string.Equals(entry.ItemId, itemId, StringComparison.Ordinal));

    /// <summary>Adds an item; refuses a duplicate or a fourth, and says which by returning false.</summary>
    public bool Add(string itemId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (Contains(itemId) || _entries.Count >= ItemComparisonBuilder.MaximumItems)
        {
            return false;
        }

        _entries.Add(new IntelCompareEntryViewModel(itemId, name, new DelegateCommand(() => Remove(itemId))));
        RaiseTray();
        return true;
    }

    public void Remove(string itemId)
    {
        if (_entries.RemoveAll(entry => string.Equals(entry.ItemId, itemId, StringComparison.Ordinal)) == 0)
        {
            return;
        }

        if (IsOpen)
        {
            if (_entries.Count < 2)
            {
                Close();
            }
            else
            {
                OpenAsync().Observe("intel", "compare items");
            }
        }

        RaiseTray();
    }

    public async Task OpenAsync()
    {
        if (!CanOpen)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ids = _entries.Select(entry => entry.ItemId).ToArray();
        IsOpen = true;
        IsLoading = true;
        try
        {
            var facts = new List<ItemComparisonFacts>(ids.Length);
            foreach (var id in ids)
            {
                facts.Add(await _load(id, cts.Token).ConfigureAwait(true));
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            MixedKinds = facts.Select(fact => fact.Intel.Kind).Distinct().Count() > 1;
            _table = ItemComparisonBuilder.Build(facts);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }

        RaiseTable();
    }

    private void ToggleCurrent()
    {
        if (_currentItemId is not { } itemId)
        {
            return;
        }

        if (Contains(itemId))
        {
            Remove(itemId);
        }
        else
        {
            Add(itemId, _currentName);
        }
    }

    private void Close()
    {
        _loadCts?.Cancel();
        IsLoading = false;
        IsOpen = false;
    }

    private void Clear()
    {
        _entries.Clear();
        Close();
        RaiseTray();
    }

    private void RaiseTray()
    {
        foreach (var name in new[]
        {
            nameof(Entries), nameof(HasEntries), nameof(CanOpen), nameof(OpenLabel), nameof(TrayLabel),
        })
        {
            OnPropertyChanged(name);
        }

        RaiseToggle();
    }

    private void RaiseToggle()
    {
        OnPropertyChanged(nameof(CurrentIsInTray));
        OnPropertyChanged(nameof(CanToggleCurrent));
        OnPropertyChanged(nameof(ToggleCurrentLabel));
        OnPropertyChanged(nameof(ToggleCurrentTip));
    }

    private void RaiseTable()
    {
        foreach (var name in new[]
        {
            nameof(Heading), nameof(KindNote), nameof(HasKindNote), nameof(ColumnCount), nameof(ColumnNames),
            nameof(Rows), nameof(ShowsTable),
        })
        {
            OnPropertyChanged(name);
        }
    }
}
