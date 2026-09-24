using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels;

public sealed record AmmoCaliberViewModel(string Caliber, string Name, string Summary);

public sealed record AmmoArmorRatingViewModel(
    string ArmorClass,
    string Rating,
    bool IsStrong,
    bool IsMarginal,
    bool IsWeak)
{
    /// <summary>
    /// The class as a number. <see cref="ArmorClass"/> is the translated label ("Class 4"), so
    /// nothing may parse or compare it; this is what a filter matches on.
    /// </summary>
    public int ClassNumber { get; init; }

    /// <summary>The class on its own ("4"), for a table cell too small for the word.</summary>
    public string Number => ClassNumber > 0
        ? ClassNumber.ToString(CultureInfo.CurrentCulture)
        : ArmorClass.StartsWith("Class ", StringComparison.Ordinal) ? ArmorClass["Class ".Length..] : ArmorClass;
}

public sealed record AmmoRoundViewModel(
    string ItemId,
    string Name,
    string Rank,
    string Tier,
    string Damage,
    string Penetration,
    string ArmorDamage,
    string Fragmentation,
    string Traits,
    string PracticalAdvice,
    string LearnModeExplanation,
    string Provenance,
    IReadOnlyList<AmmoArmorRatingViewModel> ArmorClasses)
{
    /// <summary>The numbers behind <see cref="Damage"/> and <see cref="Penetration"/>, for a sort that must not compare strings.</summary>
    public int DamageValue { get; init; }

    public int PenetrationValue { get; init; }
}

/// <summary>
/// Ranks the rounds in one caliber so a player can choose ammunition before a raid.
/// </summary>
/// <remarks>
/// <para>
/// The page builds its own <see cref="AmmoIntelligenceService"/> from the catalog rather than
/// resolving <c>IAmmoIntelligenceService</c> from the container. The registered instance is
/// constructed with empty collections, so every lookup through it returns nothing; the catalog
/// is the only place the synced ballistic rows actually exist. Building the service here also
/// means a first sync that lands after startup is picked up by the next load instead of the
/// next restart.
/// </para>
/// <para>
/// Two things the page must not overstate. The tier and the armor ratings are a heuristic:
/// <see cref="AmmoIntelligenceService"/> compares penetration against the armor class number
/// and nothing else, and caps its own confidence at 0.80 to say so. And no availability data is
/// synced at all, so the caliber listing is every cached round, not the rounds a given player
/// can buy; <c>profile</c> is deliberately passed as null for that reason, because filtering on
/// a rule the data cannot express would silently hide rounds for no real reason.
/// </para>
/// </remarks>
public sealed class AmmoPageViewModel : PageViewModel
{
    private int? _knownItemCount;

    private static string NoRoundSelected => IntelText.AmmoNoRoundSelected;

    private readonly IItemFactCatalog _catalog;
    private readonly IItemRepository _itemRepository;

    // Resolving a name is a database read per round, and a caliber list re-reads the same rounds
    // every time the player switches back to it.
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private AmmoIntelligenceService? _intelligence;
    private IReadOnlyList<AmmoCaliberViewModel> _allCalibers = [];
    private IReadOnlyList<AmmoCaliberViewModel> _calibers = [];
    private IReadOnlyList<AmmoRoundViewModel> _rounds = [];
    private AmmoCaliberViewModel? _selectedCaliber;
    private AmmoRoundViewModel? _selectedRound;
    private string _searchQuery = string.Empty;
    private string _status = IntelText.AmmoLoading;
    private string _detail = IntelText.AmmoPickACaliber;
    private string _roundHeading = IntelText.AmmoNoRoundHeading;
    private string _advice = NoRoundSelected;
    private string _explanation = string.Empty;

    // #283: what the player owns, from stash and Ammo case scans. Optional: without a profile the
    // page ranks rounds exactly as before and says nothing about ownership.
    private readonly IPlayerProfileService? _profiles;
    private IReadOnlyDictionary<string, string[]> _roundsByCaliber = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AmmoPackContents> _packs = [];
    private OwnedAmmo _owned = OwnedAmmo.None;

    // [#279] Reads in flight and reads finished, for HasLoaded.
    private int _reading;
    private bool _settled;

    public AmmoPageViewModel(
        IItemFactCatalog catalog,
        IItemRepository itemRepository,
        IPlayerProfileService? profiles = null)
        : base(IntelText.AmmoTitle, IntelText.AmmoSubtitle, IntelText.AmmoNotLoaded)
    {
        _catalog = catalog;
        _itemRepository = itemRepository;
        _profiles = profiles;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    /// <summary>
    /// What the ranking is, in the fewest words that keep it honest.
    /// </summary>
    /// <remarks>
    /// This was four sentences about what is not modelled and one about what the list does not
    /// know of your traders. A player reading a penetration ranking mid-raid needs to know it
    /// compares two numbers; the rest was hedging.
    /// </remarks>
    public string HeuristicNotice { get; } = IntelText.AmmoHeuristicNotice;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyCaliberFilter();
            }
        }
    }

    public IReadOnlyList<AmmoCaliberViewModel> Calibers
    {
        get => _calibers;
        private set => SetProperty(ref _calibers, value);
    }

    public IReadOnlyList<AmmoRoundViewModel> Rounds
    {
        get => _rounds;
        private set => SetProperty(ref _rounds, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string RoundHeading
    {
        get => _roundHeading;
        private set => SetProperty(ref _roundHeading, value);
    }

    public string Advice
    {
        get => _advice;
        private set => SetProperty(ref _advice, value);
    }

    public string Explanation
    {
        get => _explanation;
        private set => SetProperty(ref _explanation, value);
    }

    public AmmoCaliberViewModel? SelectedCaliber
    {
        get => _selectedCaliber;
        set
        {
            if (SetProperty(ref _selectedCaliber, value) && value is not null)
            {
                _ = ShowCaliberAsync(value, CancellationToken.None);
            }
        }
    }

    public AmmoRoundViewModel? SelectedRound
    {
        get => _selectedRound;
        set
        {
            if (!SetProperty(ref _selectedRound, value))
            {
                return;
            }

            // Both strings are written by the intelligence service and carried through verbatim:
            // they are the only place the ranking explains itself, and paraphrasing them here
            // would let the page drift away from what the service actually computed.
            RoundHeading = value?.Name ?? IntelText.AmmoNoRoundHeading;
            Advice = value?.PracticalAdvice ?? NoRoundSelected;
            Explanation = value?.LearnModeExplanation ?? string.Empty;
        }
    }

    /// <summary>Takes what the sync produced, rebuilding when the catalog actually changed.</summary>
    /// <remarks>
    /// The count is the change signal, not merely a zero check. On a fresh install this page
    /// loads from an empty cache, the sync then fills it, and nothing here noticed: the old
    /// code acted only on ItemCount == 0, so 0 -> N did nothing and the page went on saying it
    /// had nothing cached until somebody pressed Reload. Copied from LoadoutPageViewModel,
    /// which is the one page that had it right.
    ///
    /// Fire and forget, because Apply is called from the shell's state pass and must not block
    /// it on a database read.
    /// </remarks>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        Evidence = IntelText.AmmoEvidence(snapshot.Data.Availability, snapshot.Data.ItemCount);
        if (_knownItemCount == snapshot.Data.ItemCount)
        {
            return;
        }

        _knownItemCount = snapshot.Data.ItemCount;
        if (snapshot.Data.ItemCount == 0)
        {
            // An empty cache means the rows this page was built from are gone, so the
            // ranking is dropped with them rather than left on screen looking current.
            Reset();
            Status = SetupText.DataDetail(snapshot.Data);
            _settled = true;
            return;
        }

        LoadAsync().Observe("ammo", "reload");
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    /// <summary>
    /// [#279] True once a read of the table has finished (with rows, none cached, or an error) and
    /// no other read is running. The Windows gallery photographed "Reading the ammunition table…"
    /// three times on one run; it now waits for this instead.
    /// </summary>
    public bool HasLoaded => _settled && _reading == 0;

    /// <summary>Rounds the player owns; <see cref="OwnedAmmo.None"/> until a profile is read.</summary>
    public OwnedAmmo Owned
    {
        get => _owned;
        private set => SetProperty(ref _owned, value);
    }

    public bool TracksOwnership => _profiles is not null;

    /// <summary>The rounds of one caliber, for its owned total.</summary>
    public IReadOnlyList<string> RoundIdsOf(string caliber) =>
        _roundsByCaliber.TryGetValue(caliber, out var ids) ? ids : [];

    /// <summary>Re-reads the owned counts: a scan finished since the table was loaded.</summary>
    public async Task RefreshOwnedAsync(CancellationToken cancellationToken)
    {
        if (_profiles is null)
        {
            return;
        }

        var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(true);
        Owned = new OwnedAmmo(profile.OwnedItemCounts, _packs);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _reading++;
        try
        {
            await ReadAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _reading--;
            _settled = true;
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = IntelText.AmmoReading;
            var stats = await _catalog.GetAmmoAsync(cancellationToken).ConfigureAwait(true);
            if (stats.Count == 0)
            {
                Reset();
                Status = IntelText.AmmoNoneCached;
                return;
            }

            // Packs are handed over too so that an ammunition box resolves to the round inside it
            // rather than reading as an unknown item.
            var packs = await _catalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(true);
            _intelligence = new AmmoIntelligenceService(stats, packs);
            _packs = packs;
            _roundsByCaliber = stats
                .GroupBy(stat => stat.Caliber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(stat => stat.ItemId).ToArray(), StringComparer.OrdinalIgnoreCase);
            await RefreshOwnedAsync(cancellationToken).ConfigureAwait(true);

            var calibers = new List<AmmoCaliberViewModel>();
            foreach (var group in stats.GroupBy(stat => stat.Caliber, StringComparer.OrdinalIgnoreCase))
            {
                // One catalog round is enough to recover a future caliber's player-facing prefix.
                // Known conventional names stay centralized in CaliberText.
                var sample = group.First();
                var sampleName = await ResolveNameAsync(sample.ItemId, cancellationToken).ConfigureAwait(true);
                calibers.Add(new(
                    group.Key,
                    CaliberText.Describe(group.Key, sampleName),
                    IntelText.AmmoCaliberSummary(group.Count(), group.Max(stat => stat.Penetration))));
            }

            _allCalibers = calibers
                .OrderBy(caliber => caliber.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            ApplyCaliberFilter();
            Status =
                IntelText.AmmoStatus(_allCalibers.Count, stats.Count);

            // A reload has to re-rank whatever is open, or the rounds on screen would still be the
            // ones the previous service instance produced while the status line claimed a refresh.
            if (SelectedCaliber is { } current)
            {
                await ShowCaliberAsync(current, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Reset();
            Status = IntelText.AmmoUnreadable(exception.Message);
        }
    }

    private async Task ShowCaliberAsync(AmmoCaliberViewModel caliber, CancellationToken cancellationToken)
    {
        var intelligence = _intelligence;
        if (intelligence is null)
        {
            Rounds = [];
            Detail = IntelText.AmmoTableNotLoaded;
            return;
        }

        try
        {
            Detail = IntelText.AmmoRanking(caliber.Name);

            // profile is null on purpose. GetCaliberAsync drops rounds the profile cannot obtain,
            // and no availability rule is ever loaded, so passing a profile would only risk the
            // list quietly shrinking on a rule the data cannot actually state.
            var ranked = await intelligence
                .GetCaliberAsync(caliber.Caliber, profile: null, cancellationToken)
                .ConfigureAwait(true);

            var rows = new List<AmmoRoundViewModel>(ranked.Count);
            for (var index = 0; index < ranked.Count; index++)
            {
                rows.Add(await DescribeRoundAsync(ranked[index], index + 1, ranked.Count, cancellationToken)
                    .ConfigureAwait(true));
            }

            Rounds = rows;
            SelectedRound = null;
            Detail = rows.Count == 0
                ? IntelText.AmmoNoRoundsFor(caliber.Name)
                : IntelText.AmmoRoundsIn(rows.Count, caliber.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Rounds = [];
            Detail = IntelText.AmmoUnreadable(exception.Message);
        }
    }

    private async Task<AmmoRoundViewModel> DescribeRoundAsync(
        AmmoIntelligence round,
        int rank,
        int total,
        CancellationToken cancellationToken)
    {
        var stats = round.Stats;
        return new(
            stats.ItemId,
            await ResolveNameAsync(stats.ItemId, cancellationToken).ConfigureAwait(true),
            IntelText.AmmoRank(rank, total),
            round.Tier,
            Count(stats.Damage),
            Count(stats.Penetration),
            stats.ArmorDamagePercent is { } armorDamage ? IntelText.AmmoPercent(armorDamage) : IntelText.AmmoNotStated,
            stats.FragmentationChance is { } fragmentation
                ? fragmentation.ToString("P0", CultureInfo.CurrentCulture)
                : IntelText.AmmoNotStated,
            DescribeTraits(stats),
            round.PracticalAdvice,
            round.LearnModeExplanation,
            IntelText.AmmoProvenance(Describe(stats.Provenance.SourceUpdatedUtc), round.Confidence.Value),
            DescribeArmor(round.ArmorClassRatings))
        {
            DamageValue = stats.Damage,
            PenetrationValue = stats.Penetration,
        };
    }

    private async Task<string> ResolveNameAsync(string itemId, CancellationToken cancellationToken)
    {
        if (_names.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        // Falling back to the id keeps a round whose item row is missing visible with its real
        // ballistics, instead of dropping it out of a ranking it genuinely belongs in.
        var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
        var name = item?.Name ?? itemId;
        _names[itemId] = name;
        return name;
    }

    private void ApplyCaliberFilter()
    {
        var query = SearchQuery.Trim();
        Calibers = query.Length == 0
            ? _allCalibers
            : _allCalibers
                .Where(caliber =>
                    caliber.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    caliber.Caliber.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    private void Reset()
    {
        _intelligence = null;
        _allCalibers = [];
        _names.Clear();
        Calibers = [];
        Rounds = [];

        // Clearing the caliber matters as well as the round: the setter only re-ranks on a
        // non-null value, so a stale selection left behind would sit highlighted over an empty
        // list and would not re-rank if the same caliber came back.
        SelectedCaliber = null;
        SelectedRound = null;
    }

    private static IReadOnlyList<AmmoArmorRatingViewModel> DescribeArmor(
        IReadOnlyDictionary<int, ArmorEffectiveness> ratings)
    {
        var strip = new List<AmmoArmorRatingViewModel>(6);
        for (var armorClass = 1; armorClass <= 6; armorClass++)
        {
            // A class the service did not rate reads as a gap, not as a bad rating.
            if (!ratings.TryGetValue(armorClass, out var rating))
            {
                strip.Add(new(IntelText.AmmoClass(armorClass), IntelText.AmmoNoRating, false, false, false) { ClassNumber = armorClass });
                continue;
            }

            strip.Add(new(
                IntelText.AmmoClass(armorClass),
                IntelText.AmmoRating(rating),
                rating is ArmorEffectiveness.Excellent or ArmorEffectiveness.Good,
                rating is ArmorEffectiveness.Fair or ArmorEffectiveness.Limited,
                rating is ArmorEffectiveness.Poor)
            {
                ClassNumber = armorClass,
            });
        }

        return strip;
    }

    private static string DescribeTraits(AmmoStats stats)
    {
        var traits = new List<string>(5);
        if (stats.ProjectileCount > 1)
        {
            traits.Add(IntelText.AmmoProjectiles(stats.ProjectileCount));
        }

        if (stats.VelocityMetresPerSecond is { } velocity)
        {
            traits.Add(IntelText.AmmoVelocity(velocity));
        }

        // Recoil has been in the synced stats since the first sync and shown nowhere. A round
        // that adds a fifth to a weapon's recoil is a different choice from one that does not,
        // which is the whole question this page exists to answer.
        if (stats.RecoilModifier is { } recoil && Math.Abs(recoil) > 0.001)
        {
            traits.Add(IntelText.AmmoRecoil(recoil));
        }

        if (stats.IsSubsonic)
        {
            traits.Add(IntelText.AmmoSubsonic);
        }

        if (stats.IsTracer)
        {
            traits.Add(IntelText.AmmoTracer);
        }

        return traits.Count == 0 ? IntelText.AmmoNoTraits : string.Join(" · ", traits);
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Describe(DateTimeOffset? timestamp) =>
        timestamp is { } value ? LocalTime.Moment(value) : IntelText.AmmoNoTimestamp;
}
