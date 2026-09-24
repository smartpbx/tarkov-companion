using TarkovCompanion.App.Localization;
using System.ComponentModel;
using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>Which of the page's keep-or-sell calls a verdict chip keeps.</summary>
public enum KeyVerdictFilter
{
    All,
    Keep,
    KeepForLater,
    Sell,

    /// <summary>Keys a scan or a hand count says the player has (#283).</summary>
    Owned,
}

/// <summary>One verdict chip above the key list.</summary>
public sealed class KeyVerdictChipViewModel : BindableViewModel
{
    private bool _isSelected;

    internal KeyVerdictChipViewModel(KeyVerdictFilter filter, string label, Action<KeyVerdictFilter> select)
    {
        Filter = filter;
        Label = label;
        SelectCommand = new DelegateCommand(() => select(filter));
    }

    public KeyVerdictFilter Filter { get; }

    public string Label { get; }

    public string AutomationId => $"v2-keys-filter-{Filter.ToString().ToLowerInvariant()}";

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One key row: the verdict, then the four facts that decided it.</summary>
/// <remarks>
/// [#453] Selection changes in place. The list used to be built again, every row of it, whenever
/// it was read and whenever a key was picked, and the view drew all of those rows again.
/// </remarks>
public sealed class KeyListRowViewModel : BindableViewModel
{
    private bool _isSelected;

    public KeyListRowViewModel(KeyRowViewModel key, bool isSelected, ICommand selectCommand)
    {
        Key = key;
        _isSelected = isSelected;
        SelectCommand = selectCommand;
    }

    public KeyRowViewModel Key { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public string Name => Key.Name;

    public string VerdictLabel => Key.VerdictLabel;

    public bool IsKeep => Key.IsKeep;

    public bool IsKeepForLater => Key.IsKeepForLater;

    public bool IsSell => Key.IsSell;

    public string VerdictReason => Key.VerdictReason;

    public string LearnReason => VerdictReason;

    public string Map => Key.Map;

    public string LockSummary => Key.LockSummary;

    public string Uses => Key.MaximumUses;

    public string Cost => Key.AcquisitionCost;

    /// <summary>"Owned", "Owned ×2", or empty when none is recorded.</summary>
    public string Owned { get; init; } = string.Empty;

    public bool HasOwned => Owned.Length > 0;

    public string AutomationId => $"v2-keys-row-{Key.ItemId}";
}

/// <summary>
/// V2 Keys workspace: every cached key with its keep-or-sell call, the map it belongs to, how many
/// locks it opens, its uses and its price, from the V1 Keys page's own rows.
/// </summary>
/// <remarks>
/// An adapter over <see cref="KeysPageViewModel"/>, which owns the catalog read, the map names and
/// the verdict (the player's tracked quest and hideout demand against the flea price). The only
/// logic added here is the verdict filter, and it reads the verdicts the page already produced.
/// </remarks>
public sealed class KeysWorkspaceViewModel : BindableViewModel
{
    private readonly KeysPageViewModel _page;
    private readonly Action<string>? _openItem;
    private KeyVerdictFilter _filter;
    private IReadOnlyList<KeyListRowViewModel>? _rows;

    public KeysWorkspaceViewModel(
        KeysPageViewModel page,
        Action<string>? openItem = null,
        TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting? learnMode = null)
    {
        LearnMode = learnMode ?? new();
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _openItem = openItem;
        VerdictFilters =
        [
            new(KeyVerdictFilter.All, IntelText.KeysFilterAll, SelectFilter),
            new(KeyVerdictFilter.Keep, IntelText.KeysFilterKeep, SelectFilter),
            new(KeyVerdictFilter.KeepForLater, IntelText.KeysFilterKeepForLater, SelectFilter),
            new(KeyVerdictFilter.Sell, IntelText.KeysFilterSell, SelectFilter),
            new(KeyVerdictFilter.Owned, IntelText.KeysFilterOwned, SelectFilter),
        ];
        MarkChips();
        OpenInIntelCommand = new DelegateCommand(() =>
        {
            if (_page.Selected is { } key)
            {
                _openItem?.Invoke(key.ItemId);
            }
        });
        _page.PropertyChanged += PageChanged;
    }

    public TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting LearnMode { get; }

    public IReadOnlyList<KeyVerdictChipViewModel> VerdictFilters { get; }

    public ICommand RefreshCommand => _page.RefreshCommand;

    public ICommand OpenInIntelCommand { get; }

    /// <summary>The filter box: a key's name, or the map it belongs to.</summary>
    public string SearchQuery
    {
        get => _page.SearchQuery;
        set => _page.SearchQuery = value;
    }

    public string Status => _page.Status;

    public KeyVerdictFilter Filter
    {
        get => _filter;
        private set
        {
            if (SetProperty(ref _filter, value))
            {
                MarkChips();
                RaiseKeys();
                // A chip that hides the open key opens the first key it shows instead, so the
                // panel never describes a key the list no longer has.
                if (_page.Selected is { } open && !Narrow(_page.Keys, Filter, _page.Owned).Contains(open))
                {
                    _page.Selected = Narrow(_page.Keys, Filter, _page.Owned).FirstOrDefault();
                }
            }
        }
    }

    /// <summary>The rows under the verdict chip, built once per key list and chip.</summary>
    public IReadOnlyList<KeyListRowViewModel> Keys
    {
        get
        {
            if (_rows is null)
            {
                var selected = _page.Selected;
                _rows =
                [
                    .. Narrow(_page.Keys, Filter, _page.Owned).Select(key => new KeyListRowViewModel(
                        key,
                        ReferenceEquals(key, selected),
                        new DelegateCommand(() => _page.Selected = key))
                    {
                        Owned = OwnedLabel(_page.Owned.GetValueOrDefault(key.ItemId)),
                    }),
                ];
            }

            return _rows;
        }
    }

    public bool HasKeys => Keys.Count > 0;

    public bool ShowsNoKeys => !HasKeys;

    /// <summary>Why the list is empty, in the terms of whichever of the page and the chip emptied it.</summary>
    public string NoKeysLabel => _page.Keys.Count > 0
        ? IntelText.KeysNoMatch
        : _page.Status;

    /// <summary>"12 keys", or "3 of 12 keys" while a verdict chip hides some.</summary>
    public string KeyCountLabel
    {
        get
        {
            var shown = Keys.Count;
            var total = _page.Keys.Count;
            return shown == total
                ? IntelText.KeysCount(total)
                : IntelText.KeysShownOf(shown, total);
        }
    }

    /// <summary>The page's own status line already counts the keys; the count is only news while a chip is hiding some.</summary>
    public bool ShowsKeyCount => Keys.Count != _page.Keys.Count;

    public bool HasSelectedKey => _page.Selected is not null;

    public bool ShowsNoSelectedKey => !HasSelectedKey;

    public string SelectedName => _page.Selected?.Name ?? string.Empty;

    /// <summary>The verdict as a heading; the row's dash for "no call" reads as a missing value there.</summary>
    public string SelectedVerdict => _page.Selected switch
    {
        null => string.Empty,
        { IsKeep: false, IsKeepForLater: false, IsSell: false } => IntelText.KeysNoCall,
        var key => key.VerdictLabel,
    };

    public bool SelectedIsKeep => _page.Selected?.IsKeep == true;

    public bool SelectedIsKeepForLater => _page.Selected?.IsKeepForLater == true;

    public bool SelectedIsSell => _page.Selected?.IsSell == true;

    public string SelectedReason => _page.Selected?.VerdictReason ?? string.Empty;

    public bool HasSelectedReason => SelectedReason.Length > 0;

    public string SelectedMap => _page.Selected?.Map ?? string.Empty;

    public string SelectedLocks => _page.Selected?.LockSummary ?? string.Empty;

    public string SelectedUses => _page.Selected?.MaximumUses ?? string.Empty;

    public string SelectedCost => _page.Selected?.AcquisitionCost ?? string.Empty;

    public string SelectedProvenance => _page.Selected?.Provenance ?? string.Empty;

    public IReadOnlyList<KeyLockViewModel> SelectedLockIds => _page.SelectedLocks;

    public bool HasSelectedLockIds => _page.SelectedLocks.Count > 0;

    /// <summary>Whether the player has the chosen key, or how to find out.</summary>
    public string SelectedOwned => _page.Selected is { } key && _page.TracksOwnership
        ? _page.Owned.TryGetValue(key.ItemId, out var count)
            ? count > 0 ? count == 1 ? IntelText.KeysYouOwnIt : IntelText.KeysYouOwnCount(count) : IntelText.KeysYouDontOwnIt
            : IntelText.KeysOwnedNotScanned
        : string.Empty;

    public bool HasSelectedOwned => SelectedOwned.Length > 0;

    /// <summary>Re-reads what the player owns; the shell calls it each time the page is shown.</summary>
    public Task LoadOwnedAsync() => _page.RefreshOwnedAsync(CancellationToken.None);

    internal static string OwnedLabel(int count) => count switch
    {
        <= 0 => string.Empty,
        1 => IntelText.KeysOwned,
        _ => IntelText.KeysOwnedCount(count),
    };

    /// <summary>Keeps the rows a verdict chip asks for. A key the page could not judge belongs to no chip but All.</summary>
    internal static IReadOnlyList<KeyRowViewModel> Narrow(
        IReadOnlyList<KeyRowViewModel> keys,
        KeyVerdictFilter filter,
        IReadOnlyDictionary<string, int>? owned = null) => filter switch
    {
        KeyVerdictFilter.Owned => [.. keys.Where(key => owned?.GetValueOrDefault(key.ItemId) > 0)],
        KeyVerdictFilter.Keep => [.. keys.Where(key => key.IsKeep)],
        KeyVerdictFilter.KeepForLater => [.. keys.Where(key => key.IsKeepForLater)],
        KeyVerdictFilter.Sell => [.. keys.Where(key => key.IsSell)],
        _ => keys,
    };

    private void SelectFilter(KeyVerdictFilter filter) => Filter = filter;

    /// <summary>Opens the first key when none is chosen, so the context panel is never an empty column beside a full list.</summary>
    private void EnsureSelection()
    {
        if (_page.Selected is null && Narrow(_page.Keys, Filter, _page.Owned).FirstOrDefault() is { } first)
        {
            _page.Selected = first;
        }
    }

    private void MarkChips()
    {
        foreach (var chip in VerdictFilters)
        {
            chip.IsSelected = chip.Filter == Filter;
        }
    }

    private void RaiseKeys()
    {
        _rows = null;
        OnPropertyChanged(nameof(Keys));
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(ShowsNoKeys));
        OnPropertyChanged(nameof(NoKeysLabel));
        OnPropertyChanged(nameof(KeyCountLabel));
        OnPropertyChanged(nameof(ShowsKeyCount));
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(KeysPageViewModel.Keys):
                RaiseKeys();
                EnsureSelection();
                break;
            case nameof(KeysPageViewModel.Selected):
                if (_rows is not null)
                {
                    var selected = _page.Selected;
                    foreach (var row in _rows)
                    {
                        row.IsSelected = ReferenceEquals(row.Key, selected);
                    }
                }

                foreach (var name in new[]
                {
                    nameof(HasSelectedKey), nameof(ShowsNoSelectedKey), nameof(SelectedName), nameof(SelectedVerdict),
                    nameof(SelectedIsKeep), nameof(SelectedIsKeepForLater), nameof(SelectedIsSell), nameof(SelectedReason),
                    nameof(HasSelectedReason), nameof(SelectedMap), nameof(SelectedLocks), nameof(SelectedUses),
                    nameof(SelectedCost), nameof(SelectedProvenance), nameof(SelectedOwned), nameof(HasSelectedOwned),
                })
                {
                    OnPropertyChanged(name);
                }

                break;
            case nameof(KeysPageViewModel.SelectedLocks):
                OnPropertyChanged(nameof(SelectedLockIds));
                OnPropertyChanged(nameof(HasSelectedLockIds));
                break;
            case nameof(KeysPageViewModel.Status):
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(NoKeysLabel));
                break;
            case nameof(KeysPageViewModel.Owned):
                RaiseKeys();
                OnPropertyChanged(nameof(SelectedOwned));
                OnPropertyChanged(nameof(HasSelectedOwned));
                break;
            case nameof(KeysPageViewModel.SearchQuery):
                OnPropertyChanged(nameof(SearchQuery));
                break;
        }
    }
}
