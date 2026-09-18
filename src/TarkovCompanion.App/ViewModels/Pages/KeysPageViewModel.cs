using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels;

public sealed record KeyLockViewModel(string LockId);

public sealed record KeyRowViewModel(
    string ItemId,
    string Name,
    string MapId,
    string Map,
    bool HasMap,
    string LockSummary,
    string MaximumUses,
    string AcquisitionCost,
    string Provenance,
    IReadOnlyList<KeyLockViewModel> Locks)
{
    /// <summary>Keep it, sell it, or neither, and the one fact that decided.</summary>
    /// <remarks>
    /// An init property rather than two more positional parameters on a record that already
    /// takes nine.
    /// </remarks>
    public KeyVerdict Verdict { get; init; } = new(KeepOrSell.NoCall, string.Empty);

    public string VerdictLabel => Verdict.Call switch
    {
        KeepOrSell.Keep => "Keep",
        KeepOrSell.KeepForLater => "Keep for later",
        KeepOrSell.Sell => "Sell",
        _ => "—",
    };

    public bool IsKeep => Verdict.Call == KeepOrSell.Keep;

    /// <summary>
    /// Drawn differently from a Keep, because it is a weaker claim.
    /// </summary>
    /// <remarks>
    /// On a fresh wipe every quest is ahead of you, so this is most of the table. Given the
    /// same colour as a Keep it would drown the handful of keys a quest actually on the board
    /// needs, which are the ones worth finding at a glance.
    /// </remarks>
    public bool IsKeepForLater => Verdict.Call == KeepOrSell.KeepForLater;

    public bool IsSell => Verdict.Call == KeepOrSell.Sell;

    public string VerdictReason => Verdict.Reason;
}

/// <summary>
/// Lists every cached key with the facts the synced data actually states about it.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no tier and no score here, and the page still does not use
/// <c>IKeyIntelligenceService</c>. Four of the six inputs that service weighs (expected loot,
/// lock utility, unique access and route risk) have no source anywhere in the synced payload
/// and are projected as zero by <c>SqliteItemFactCatalog</c>, whose remarks say so outright. A
/// tier built on them would not be a weak signal, it would be a fabricated one, and every key
/// would land in the same low band for a reason that has nothing to do with the key.
/// </para>
/// <para>
/// There <i>is</i> now a keep-or-sell call, and it is built on the opposite footing: the
/// player's own tracked quest and hideout demand, and the flea price ranked against the other
/// keys. Both are real, both are synced, and <see cref="KeyValue"/> carries the reasoning. A key
/// the data cannot rank is told it cannot be ranked rather than being given a verdict.
/// </para>
/// <para>
/// What is left is still useful, and all of it is copied rather than inferred: which map the
/// key's locks are on, how many locks it opens, how many uses it has, and what it costs. Maps
/// and locks are shown as the identifiers the source uses, because the key projection reads
/// <c>map_locks</c>, which carries ids and no names.
/// </para>
/// </remarks>
public sealed class KeysPageViewModel : PageViewModel
{
    private int? _knownItemCount;

    private const string NoKeySelected = "Select a key to see every lock it opens.";
    private const string UnknownMap = "No single map";

    private readonly IItemFactCatalog _catalog;
    private readonly IItemRepository _itemRepository;
    private readonly IQuestProgressService? _questProgress;
    private readonly IMapDataService? _maps;
    private readonly Dictionary<string, string> _mapNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<KeyRowViewModel> _allKeys = [];
    private IReadOnlyList<KeyRowViewModel> _keys = [];
    private IReadOnlyList<KeyLockViewModel> _selectedLocks = [];
    private KeyRowViewModel? _selected;
    private string _searchQuery = string.Empty;
    private string _status = "Loading the key table…";
    private string _detail = NoKeySelected;

    public KeysPageViewModel(
        IItemFactCatalog catalog,
        IItemRepository itemRepository,
        IQuestProgressService? questProgress = null,
        // Names the map a key belongs to. Without it the rows print the raw identifier, which
        // is what they did: every key on the page read "Map id 56f40101d2720b2a4d8b45d6".
        //
        // The maps table rather than the map catalog, and that distinction is the whole of why
        // the first attempt at this changed nothing. A key's map id is the game's own —
        // 56f40101d2720b2a4d8b45d6 — and the map catalog is the-hideout's maps.json, whose
        // locations are keyed by normalised name and carry no such id at all: MapLocation's
        // SourceId reads an "id" property that file does not have, so it is null for every
        // location and no lookup through it can ever match. The maps table is synced from
        // tarkov.dev and is keyed by exactly that id.
        IMapDataService? maps = null)
        : base("Keys", "Keep or sell, what each key opens, its uses and its price", "Not loaded")
    {
        _catalog = catalog;
        _itemRepository = itemRepository;
        _questProgress = questProgress;
        _maps = maps;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    /// <summary>
    /// What a map is called, asked once per map rather than once per key.
    /// </summary>
    /// <remarks>
    /// Two hundred and fifty-seven keys across a dozen maps, so the cache is the difference
    /// between a dozen reads and two hundred and fifty-seven. The id is kept as the answer when
    /// the table has nothing, which is the rule the rest of the application follows: a name
    /// nobody has yet is better shown as the id than as "Unknown".
    /// </remarks>
    private async Task<string> NameOfMapAsync(string mapId, CancellationToken cancellationToken)
    {
        if (_maps is null)
        {
            return mapId;
        }

        if (_mapNames.TryGetValue(mapId, out var cached))
        {
            return cached;
        }

        try
        {
            var name = (await _maps.GetAsync(mapId, cancellationToken).ConfigureAwait(true))?.Name ?? mapId;
            _mapNames[mapId] = name;
            return name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _mapNames[mapId] = mapId;
            return mapId;
        }
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    /// <summary>
    /// Why locks read as identifiers and why nothing is ranked.
    /// </summary>
    /// <remarks>
    /// Two paragraphs, one of which explained in six clauses why a ranking would be invented.
    /// The absence of a ranking is visible; the reason for it belongs in the repository rather
    /// than on a page somebody reads between raids.
    /// </remarks>
    public string ScoringNotice { get; } = "Not ranked. Locks are named by the source's identifiers.";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<KeyRowViewModel> Keys
    {
        get => _keys;
        private set => SetProperty(ref _keys, value);
    }

    public IReadOnlyList<KeyLockViewModel> SelectedLocks
    {
        get => _selectedLocks;
        private set => SetProperty(ref _selectedLocks, value);
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

    public KeyRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            // Everything shown for a selection is already on the row, so there is nothing to await
            // and nothing that can fail here.
            SelectedLocks = value?.Locks ?? [];
            Detail = value is null
                ? NoKeySelected
                : value.Locks.Count == 0
                    ? $"{value.Name} · no locks synced for it"
                    : $"{value.Name} opens {Count(value.Locks.Count)} locks";
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
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
        if (_knownItemCount == snapshot.Data.ItemCount)
        {
            return;
        }

        _knownItemCount = snapshot.Data.ItemCount;
        if (snapshot.Data.ItemCount == 0)
        {
            // The rows came out of the item cache, so an empty cache means they are gone
            // rather than merely stale, and leaving them on screen would present them as
            // current.
            Reset();
            Status = snapshot.Data.Detail;
            return;
        }

        _ = LoadAsync();
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = "Reading the key table…";
            var facts = await _catalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(true);
            if (facts.Count == 0)
            {
                Reset();
                Status = "No keys cached yet";
                return;
            }

            // Ranked before any row is built, because a key's verdict is a statement about
            // where it sits among the others and there is no such thing as the first one's
            // rank on its own.
            var ranks = KeyValue.Rank(facts
                .Where(fact => fact.AcquisitionCostRoubles is > 0)
                .Select(fact => (fact.ItemId, fact.AcquisitionCostRoubles!.Value)));
            var rows = new List<KeyRowViewModel>(facts.Count);
            foreach (var fact in facts)
            {
                rows.Add(await DescribeAsync(
                    fact,
                    ranks.TryGetValue(fact.ItemId, out var rank) ? rank : null,
                    cancellationToken).ConfigureAwait(true));
            }

            // Keys whose map the projection could not settle on sort last rather than being mixed
            // in under a name that would read as a real map.
            _allKeys = rows
                .OrderByDescending(row => row.HasMap)
                .ThenBy(row => row.Map, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            ApplyFilter();

            var withoutMap = _allKeys.Count(row => !row.HasMap);
            var keep = _allKeys.Count(row => row.IsKeep);
            var sell = _allKeys.Count(row => row.IsSell);
            Status = withoutMap == 0
                ? $"{Count(_allKeys.Count)} cached keys · {Count(keep)} to keep · {Count(sell)} to sell"
                : $"{Count(_allKeys.Count)} keys · {Count(keep)} to keep · {Count(sell)} to sell · {Count(withoutMap)} without a single cached map";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Reset();
            Status = $"Unreadable · {exception.Message}";
        }
    }

    private async Task<KeyRowViewModel> DescribeAsync(
        KeyFacts facts,
        double? dearerThan,
        CancellationToken cancellationToken)
    {
        // Falling back to the id keeps a key whose item row is missing visible with its real locks
        // and cost, rather than hiding it behind a name lookup that failed.
        var item = await _itemRepository.GetAsync(facts.ItemId, cancellationToken).ConfigureAwait(true);
        return new(
            facts.ItemId,
            item?.Name ?? facts.ItemId,
            facts.MapId ?? string.Empty,
            // Named where the maps table has it, and the identifier until it does. A key
            // belongs to a place, and "56f40101d2720b2a4d8b45d6" is not one.
            facts.MapId is { } mapId
                ? await NameOfMapAsync(mapId, cancellationToken).ConfigureAwait(true)
                : UnknownMap,
            facts.MapId is not null,
            facts.Locks.Count switch
            {
                0 => "No cached lock lists this key",
                1 => "Opens 1 lock",
                _ => $"Opens {Count(facts.Locks.Count)} locks",
            },
            // A key with no stated use count is not the same as a key with unlimited uses; the
            // source simply does not say, so neither does the page.
            facts.MaximumUses is { } uses ? $"{Count(uses)} use(s)" : "No use limit is stated",
            facts.AcquisitionCostRoubles is { } acquisitionCost && acquisitionCost > 0
                ? Roubles(acquisitionCost)
                : "No price is cached",
            $"json.tarkov.dev · {Describe(facts.Provenance.SourceUpdatedUtc)}",
            facts.Locks.Select(lockId => new KeyLockViewModel(lockId)).ToArray())
        {
            Verdict = KeyValue.Judge(
                facts.AcquisitionCostRoubles,
                dearerThan,
                facts.Locks.Count,
                facts.MaximumUses,
                await NeedsAsync(facts.ItemId, cancellationToken).ConfigureAwait(true)),
        };
    }

    /// <summary>
    /// What the player's own tracked progress asks for, where progress is being tracked.
    /// </summary>
    /// <remarks>
    /// One query per key, matching the item-name lookup beside it, which is the same shape and
    /// the same cost. Null rather than an empty summary where nothing is wired up, so the
    /// verdict falls through to the market rather than concluding that no quest wants it.
    ///
    /// A failure here is not a failure of the page. The verdict simply loses its strongest
    /// input and says what the market says, which is what it did before this existed.
    /// </remarks>
    private async Task<ItemNeedSummary?> NeedsAsync(string itemId, CancellationToken cancellationToken)
    {
        if (_questProgress is null)
        {
            return null;
        }

        try
        {
            return await _questProgress.GetItemNeedsAsync(itemId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        Keys = query.Length == 0
            ? _allKeys
            : _allKeys
                .Where(row =>
                    row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    row.HasMap && row.Map.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
    }

    private void Reset()
    {
        _allKeys = [];
        Keys = [];
        Selected = null;
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Roubles(long value) => value.ToString("N0", CultureInfo.CurrentCulture) + " ₽";

    private static string Describe(DateTimeOffset? timestamp) =>
        timestamp is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "no timestamp";
}
