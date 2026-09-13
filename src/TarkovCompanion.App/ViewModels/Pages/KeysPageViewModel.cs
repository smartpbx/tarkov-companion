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
    string Map,
    bool HasMap,
    string LockSummary,
    string MaximumUses,
    string AcquisitionCost,
    string Provenance,
    IReadOnlyList<KeyLockViewModel> Locks);

/// <summary>
/// Lists every cached key with the facts the synced data actually states about it.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no tier, no score and no "worth carrying" verdict here, and the page
/// does not use <c>IKeyIntelligenceService</c> at all. Four of the six inputs that service
/// weighs (expected loot, lock utility, unique access and route risk) have no source anywhere in
/// the synced payload and are projected as zero by <c>SqliteItemFactCatalog</c>, whose remarks
/// say so outright. A tier built on them would not be a weak signal, it would be a fabricated
/// one, and every key would land in the same low band for a reason that has nothing to do with
/// the key.
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
    private IReadOnlyList<KeyRowViewModel> _allKeys = [];
    private IReadOnlyList<KeyRowViewModel> _keys = [];
    private IReadOnlyList<KeyLockViewModel> _selectedLocks = [];
    private KeyRowViewModel? _selected;
    private string _searchQuery = string.Empty;
    private string _status = "Loading the key table…";
    private string _detail = NoKeySelected;

    public KeysPageViewModel(IItemFactCatalog catalog, IItemRepository itemRepository)
        : base("Keys", "What each key opens, its uses, and its price", "Not loaded")
    {
        _catalog = catalog;
        _itemRepository = itemRepository;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
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

            var rows = new List<KeyRowViewModel>(facts.Count);
            foreach (var fact in facts)
            {
                rows.Add(await DescribeAsync(fact, cancellationToken).ConfigureAwait(true));
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
            Status = withoutMap == 0
                ? $"{Count(_allKeys.Count)} cached key(s)."
                : $"{Count(_allKeys.Count)} keys · {Count(withoutMap)} without a single cached map";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Reset();
            Status = $"Unreadable · {exception.Message}";
        }
    }

    private async Task<KeyRowViewModel> DescribeAsync(KeyFacts facts, CancellationToken cancellationToken)
    {
        // Falling back to the id keeps a key whose item row is missing visible with its real locks
        // and cost, rather than hiding it behind a name lookup that failed.
        var item = await _itemRepository.GetAsync(facts.ItemId, cancellationToken).ConfigureAwait(true);
        return new(
            facts.ItemId,
            item?.Name ?? facts.ItemId,
            facts.MapId ?? UnknownMap,
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
            facts.AcquisitionCostRoubles > 0
                ? Roubles(facts.AcquisitionCostRoubles)
                : "No price is cached",
            $"json.tarkov.dev · {Describe(facts.Provenance.SourceUpdatedUtc)}",
            facts.Locks.Select(lockId => new KeyLockViewModel(lockId)).ToArray());
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        Keys = query.Length == 0
            ? _allKeys
            : _allKeys
                .Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
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
