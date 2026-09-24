using TarkovCompanion.App.Localization;
using System.ComponentModel;
using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>How the rounds of one caliber are ordered; rank is the intelligence service's own.</summary>
public enum AmmoSort
{
    Rank,
    Penetration,
    Damage,
    Name,
}

/// <summary>One chip: an ordering, or an armor class to filter by (zero is "any").</summary>
public sealed class AmmoChipViewModel : BindableViewModel
{
    private bool _isSelected;

    internal AmmoChipViewModel(string key, string label, Action select)
    {
        Key = key;
        Label = label;
        SelectCommand = new DelegateCommand(select);
    }

    public string Key { get; }

    public string Label { get; }

    public string AutomationId => $"v2-ammo-{Key}";

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One caliber in the left column.</summary>
public sealed record AmmoCaliberRowViewModel(AmmoCaliberViewModel Caliber, bool IsSelected, ICommand SelectCommand)
{
    public string Name => Caliber.Name;

    public string Summary => Caliber.Summary;

    /// <summary>"240 owned" across the caliber; empty until a scan has counted any of it.</summary>
    public string Owned { get; init; } = string.Empty;

    public bool HasOwned => Owned.Length > 0;

    public string AutomationId => $"v2-ammo-caliber-{Caliber.Caliber}";
}

/// <summary>One round in the ballistics table.</summary>
public sealed record AmmoRoundRowViewModel(AmmoRoundViewModel Round, bool IsSelected, ICommand SelectCommand)
{
    public string Name => Round.Name;

    public string Rank => Round.Rank;

    public string Tier => Round.Tier;

    public string Damage => Round.Damage;

    public string Penetration => Round.Penetration;

    public string ArmorDamage => Round.ArmorDamage;

    public string Fragmentation => Round.Fragmentation;

    public IReadOnlyList<AmmoArmorRatingViewModel> ArmorClasses => Round.ArmorClasses;

    public string LearnReason => Round.LearnModeExplanation;

    /// <summary>"60 owned" for this round, loose and in packs; empty until a scan has counted it.</summary>
    public string Owned { get; init; } = string.Empty;

    public bool HasOwned => Owned.Length > 0;

    public string AutomationId => $"v2-ammo-round-{Round.ItemId}";
}

/// <summary>
/// V2 Ammo workspace: the V1 Ammo page's ranking as a ballistics table, plus the two things a
/// player asks of it that the page could not answer, "what beats class five" and "which of these
/// hits hardest".
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="AmmoPageViewModel"/>, not a second ranking. The page owns the
/// catalog read, the caliber list and the heuristic; this only orders and narrows what it
/// already produced. Filtering by armor class keeps a round whose rating for that class is
/// strong (good or excellent, the page's green): a marginal rating is a round that mostly fails
/// to get through, and a class the service did not rate is a gap, so neither answers "what beats
/// it".
/// </remarks>
public sealed class AmmoWorkspaceViewModel : BindableViewModel
{
    private readonly AmmoPageViewModel _page;
    private readonly Action<string>? _openItem;
    private int _armorClass;
    private AmmoSort _sort = AmmoSort.Rank;

    public AmmoWorkspaceViewModel(
        AmmoPageViewModel page,
        Action<string>? openItem = null,
        TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting? learnMode = null)
    {
        LearnMode = learnMode ?? new();
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _openItem = openItem;
        ArmorFilters =
        [
            .. Enumerable.Range(0, 7).Select(armorClass => new AmmoChipViewModel(
                armorClass == 0 ? "class-any" : $"class-{armorClass}",
                armorClass == 0 ? IntelText.AmmoAnyArmor : IntelText.AmmoClass(armorClass),
                () => ArmorClass = armorClass)),
        ];
        Sorts =
        [
            .. Enum.GetValues<AmmoSort>().Select(sort => new AmmoChipViewModel(
                $"sort-{sort.ToString().ToLowerInvariant()}",
                sort switch
                {
                    AmmoSort.Penetration => IntelText.AmmoSortPenetration,
                    AmmoSort.Damage => IntelText.AmmoSortDamage,
                    AmmoSort.Name => IntelText.AmmoSortName,
                    _ => IntelText.AmmoSortBestFirst,
                },
                () => Sort = sort)),
        ];
        MarkChips();
        OpenInIntelCommand = new DelegateCommand(() =>
        {
            if (_page.SelectedRound is { } round)
            {
                _openItem?.Invoke(round.ItemId);
            }
        });
        _page.PropertyChanged += PageChanged;
    }

    public TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting LearnMode { get; }

    public IReadOnlyList<AmmoChipViewModel> ArmorFilters { get; }

    public IReadOnlyList<AmmoChipViewModel> Sorts { get; }

    public ICommand RefreshCommand => _page.RefreshCommand;

    public ICommand OpenInIntelCommand { get; }

    /// <summary>The caliber filter box; typing narrows the left column, as on V1.</summary>
    public string SearchQuery
    {
        get => _page.SearchQuery;
        set => _page.SearchQuery = value;
    }

    public string Status => _page.Status;

    /// <summary>[#279] The table has been read and no read is running; the gallery waits for it.</summary>
    public bool HasLoaded => _page.HasLoaded;

    /// <summary>The caliber column's second line: how many rounds, or what to do next.</summary>
    public string Detail => _page.Detail;

    /// <summary>0 for any armor, otherwise the class (1 to 6) a round must get through.</summary>
    public int ArmorClass
    {
        get => _armorClass;
        private set
        {
            if (SetProperty(ref _armorClass, value))
            {
                MarkChips();
                KeepSelectionVisible();
                EnsureRound();
                RaiseRounds();
            }
        }
    }

    public AmmoSort Sort
    {
        get => _sort;
        private set
        {
            if (SetProperty(ref _sort, value))
            {
                MarkChips();
                RaiseRounds();
            }
        }
    }

    public IReadOnlyList<AmmoCaliberRowViewModel> Calibers
    {
        get
        {
            var selected = _page.SelectedCaliber;
            return
            [
                .. _page.Calibers.Select(caliber => new AmmoCaliberRowViewModel(
                    caliber,
                    ReferenceEquals(caliber, selected),
                    new DelegateCommand(() => _page.SelectedCaliber = caliber))
                {
                    Owned = OwnedAmmo.Short(_page.Owned.RoundsOf(_page.RoundIdsOf(caliber.Caliber))),
                }),
            ];
        }
    }

    public bool HasCalibers => _page.Calibers.Count > 0;

    public IReadOnlyList<AmmoRoundRowViewModel> Rounds
    {
        get
        {
            var selected = _page.SelectedRound;
            return
            [
                .. Arrange(_page.Rounds, ArmorClass, Sort).Select(round => new AmmoRoundRowViewModel(
                    round,
                    ReferenceEquals(round, selected),
                    new DelegateCommand(() => _page.SelectedRound = round))
                {
                    Owned = OwnedAmmo.Short(_page.Owned.RoundsOf(round.ItemId)),
                }),
            ];
        }
    }

    public bool HasRounds => Rounds.Count > 0;

    public bool ShowsNoRounds => !HasRounds;

    /// <summary>Why the table is empty: no caliber yet, none cached, or nothing gets through the chosen class.</summary>
    public string NoRoundsLabel => _page.Rounds.Count > 0 && ArmorClass > 0
        ? IntelText.AmmoNoneBeatsClass(ArmorClass)
        : _page.SelectedCaliber is null
            ? IntelText.AmmoPickCaliber
            : IntelText.AmmoNoRoundsCached;

    /// <summary>"4 of 12 rounds" while a class filter hides some; "12 rounds" otherwise.</summary>
    public string RoundCountLabel
    {
        get
        {
            var shown = Rounds.Count;
            var total = _page.Rounds.Count;
            return shown == total
                ? IntelText.AmmoRoundCount(total)
                : IntelText.AmmoRoundsShownOf(shown, total);
        }
    }

    public bool HasSelectedRound => _page.SelectedRound is not null;

    public bool ShowsNoSelectedRound => !HasSelectedRound;

    public string SelectedName => _page.SelectedRound?.Name ?? string.Empty;

    public string SelectedRank => _page.SelectedRound?.Rank ?? string.Empty;

    public string SelectedTier => _page.SelectedRound?.Tier ?? string.Empty;

    public string SelectedTraits => _page.SelectedRound?.Traits ?? string.Empty;

    public string SelectedProvenance => _page.SelectedRound?.Provenance ?? string.Empty;

    public string SelectedAdvice => _page.Advice;

    public string SelectedExplanation => _page.Explanation;

    /// <summary>How many of the chosen round the player owns, or how to find out.</summary>
    public string SelectedOwned => _page.SelectedRound is { } round && _page.TracksOwnership
        ? OwnedAmmo.Long(_page.Owned.RoundsOf(round.ItemId))
        : string.Empty;

    public bool HasSelectedOwned => SelectedOwned.Length > 0;

    /// <summary>Re-reads what the player owns; the shell calls it each time the page is shown.</summary>
    public Task LoadOwnedAsync() => _page.RefreshOwnedAsync(CancellationToken.None);

    public IReadOnlyList<AmmoArmorRatingViewModel> SelectedArmorClasses => _page.SelectedRound?.ArmorClasses ?? [];

    /// <summary>Applies the class filter, then the ordering. Rank keeps the service's order, which is already best first.</summary>
    internal static IReadOnlyList<AmmoRoundViewModel> Arrange(
        IReadOnlyList<AmmoRoundViewModel> rounds,
        int armorClass,
        AmmoSort sort)
    {
        IEnumerable<AmmoRoundViewModel> kept = armorClass is >= 1 and <= 6
            ? rounds.Where(round => GetsThrough(round, armorClass))
            : rounds;
        return
        [
            .. sort switch
            {
                AmmoSort.Penetration => kept.OrderByDescending(round => round.PenetrationValue),
                AmmoSort.Damage => kept.OrderByDescending(round => round.DamageValue),
                AmmoSort.Name => kept.OrderBy(round => round.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => kept,
            },
        ];
    }

    private static bool GetsThrough(AmmoRoundViewModel round, int armorClass) =>
        round.ArmorClasses.FirstOrDefault(rating => rating.ClassNumber == armorClass) is { IsStrong: true };

    private void MarkChips()
    {
        foreach (var chip in ArmorFilters)
        {
            chip.IsSelected = chip.Key == (ArmorClass == 0 ? "class-any" : $"class-{ArmorClass}");
        }

        foreach (var chip in Sorts)
        {
            chip.IsSelected = chip.Key == $"sort-{Sort.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>
    /// Nothing is chosen until the player chooses, and an empty context panel beside a full table
    /// reads as a page that has not loaded. So the first caliber is opened as soon as the list
    /// exists, the way Intel opens its first result.
    /// </summary>
    private void EnsureCaliber()
    {
        if (_page.SelectedCaliber is null && _page.Calibers.Count > 0)
        {
            _page.SelectedCaliber = _page.Calibers[0];
        }
    }

    /// <summary>
    /// Opens the first round once a caliber has finished ranking.
    /// </summary>
    /// <remarks>
    /// Driven by the page's Detail line, which it writes last, after the rounds are in and the old
    /// choice is cleared. Choosing from the change to SelectedRound instead nests one assignment
    /// inside the page's own setter for it, and that setter finishes by writing the heading and
    /// advice from the value it was given, so the nested choice's advice was overwritten by the
    /// "select a round" hint.
    /// </remarks>
    private void EnsureRound()
    {
        if (_page.SelectedRound is null && Arrange(_page.Rounds, ArmorClass, Sort).FirstOrDefault() is { } first)
        {
            _page.SelectedRound = first;
        }
    }

    /// <summary>A class filter that hides the chosen round moves the choice to the first round still shown.</summary>
    private void KeepSelectionVisible()
    {
        if (_page.SelectedRound is { } current)
        {
            var shown = Arrange(_page.Rounds, ArmorClass, Sort);
            if (!shown.Contains(current))
            {
                _page.SelectedRound = shown.FirstOrDefault();
            }
        }
    }

    private void RaiseRounds()
    {
        OnPropertyChanged(nameof(Rounds));
        OnPropertyChanged(nameof(HasRounds));
        OnPropertyChanged(nameof(ShowsNoRounds));
        OnPropertyChanged(nameof(NoRoundsLabel));
        OnPropertyChanged(nameof(RoundCountLabel));
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(AmmoPageViewModel.Calibers):
            case nameof(AmmoPageViewModel.SelectedCaliber):
                OnPropertyChanged(nameof(Calibers));
                OnPropertyChanged(nameof(HasCalibers));
                RaiseRounds();
                EnsureCaliber();
                break;
            case nameof(AmmoPageViewModel.Rounds):
                RaiseRounds();
                break;
            case nameof(AmmoPageViewModel.SelectedRound):
                RaiseRounds();
                foreach (var name in new[]
                {
                    nameof(HasSelectedRound), nameof(ShowsNoSelectedRound), nameof(SelectedName), nameof(SelectedRank),
                    nameof(SelectedTier), nameof(SelectedTraits), nameof(SelectedProvenance), nameof(SelectedArmorClasses),
                    nameof(SelectedOwned), nameof(HasSelectedOwned),
                })
                {
                    OnPropertyChanged(name);
                }

                break;
            case nameof(AmmoPageViewModel.Advice):
                OnPropertyChanged(nameof(SelectedAdvice));
                break;
            case nameof(AmmoPageViewModel.Explanation):
                OnPropertyChanged(nameof(SelectedExplanation));
                break;
            case nameof(AmmoPageViewModel.Status):
                OnPropertyChanged(nameof(Status));
                break;
            case nameof(AmmoPageViewModel.Detail):
                OnPropertyChanged(nameof(Detail));
                EnsureRound();
                break;
            case nameof(AmmoPageViewModel.Owned):
                OnPropertyChanged(nameof(Calibers));
                RaiseRounds();
                OnPropertyChanged(nameof(SelectedOwned));
                OnPropertyChanged(nameof(HasSelectedOwned));
                break;
            case nameof(AmmoPageViewModel.SearchQuery):
                OnPropertyChanged(nameof(SearchQuery));
                break;
        }
    }
}
