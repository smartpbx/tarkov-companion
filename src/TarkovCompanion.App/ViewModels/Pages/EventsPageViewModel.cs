using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.App.ViewModels;

/// <summary>One hand-authored event definition, as it reads in the list.</summary>
public sealed record EventSummaryViewModel(
    string EventId,
    string Name,
    string Season,
    string Window,
    string ItemSummary,
    string Provenance);

/// <summary>One item an event applies to, with the state recorded for it locally.</summary>
public sealed record EventItemViewModel(
    string ItemId,
    string ItemName,
    string StateLabel,
    bool CanRecord,
    ICommand MarkSafeCommand,
    ICommand MarkAllergicCommand,
    ICommand MarkUntestedCommand,
    ICommand MarkUnknownCommand,
    bool CanEdit,
    ICommand RemoveCommand);

/// <summary>One item the search found, offered for adding to the selected event.</summary>
public sealed record EventItemMatchViewModel(string ItemId, string ItemName, ICommand AddCommand);

/// <summary>
/// Lists the locally configured seasonal events and records, per item, what happened when the
/// player consumed it.
/// </summary>
/// <remarks>
/// <para>
/// The honesty constraint is that an empty list is the normal state, not a failure. json.tarkov.dev
/// exposes no events endpoint, so every definition is a file a person wrote; out of season there
/// are none, and the page says what an event is and where a file goes instead of reporting an
/// error it did not have.
/// </para>
/// <para>
/// Nothing here is observed from the game. Every state is one the player pressed a button to
/// record, and it is kept in the local profile.
/// </para>
/// <para>
/// <c>ProfileEventTrackerService</c> throws <see cref="KeyNotFoundException"/> for an id it was
/// not constructed with, so only ids this page received from the catalog are ever passed to it.
/// That is still not sufficient: the tracker takes its definitions as a constructor collection
/// and the container registers none, so an id straight from the catalog can be unknown to it
/// anyway. The page treats that as a reportable condition rather than letting it reach the UI
/// thread as an unhandled exception.
/// </para>
/// </remarks>
public sealed class EventsPageViewModel : PageViewModel
{
    /// <summary>Shown in place of the list when no definition is configured.</summary>
    /// <remarks>
    /// The wording follows <c>assets/events/README.md</c> and the paths <c>AppDataPaths</c>
    /// resolves, so a player who follows this line lands where <c>JsonFileEventCatalog</c> reads.
    /// </remarks>
    private int? _knownItemCount;

    private const string EmptyGuidanceText = "Name one above to create it.";

    private const string StateGuidanceText = "Untested is the default. Unknown means you cannot say.";

    /// <summary>How many search hits are worth offering at once.</summary>
    private const int MatchLimit = 25;

    private readonly IEventCatalog _catalog;
    private readonly IEventTrackerService _tracker;
    private readonly IItemRepository _itemRepository;
    private readonly IEventAuthoring? _authoring;

    private IReadOnlyDictionary<string, EventDefinition> _definitions =
        new Dictionary<string, EventDefinition>(StringComparer.Ordinal);

    private IReadOnlyList<EventSummaryViewModel> _events = [];
    private IReadOnlyList<EventItemViewModel> _items = [];
    private IReadOnlyList<EventItemMatchViewModel> _matches = [];
    private EventSummaryViewModel? _selected;
    private bool _hasEvents;
    private string _status = "Reading local event definitions…";
    private string _detail = "Select an event to see the items it applies to.";
    private string _progress = "No event selected.";
    private string _newEventName = string.Empty;
    private string _itemQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _confirmingDelete;

    public EventsPageViewModel(
        IEventCatalog catalog,
        IEventTrackerService tracker,
        IItemRepository itemRepository,
        IEventAuthoring? authoring = null)
        : base("Events", "Seasonal events, and what you record against them", "Runtime state not loaded")
    {
        _catalog = catalog;
        _tracker = tracker;
        _itemRepository = itemRepository;
        _authoring = authoring;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
        CreateCommand = new AsyncDelegateCommand(() => CreateAsync(CancellationToken.None));
        SearchCommand = new AsyncDelegateCommand(() => SearchAsync(CancellationToken.None));
        DeleteCommand = new AsyncDelegateCommand(() => DeleteAsync(CancellationToken.None));
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public AsyncDelegateCommand CreateCommand { get; }

    public AsyncDelegateCommand SearchCommand { get; }

    public AsyncDelegateCommand DeleteCommand { get; }

    /// <summary>Whether this page may write definitions, which is what shows the editing controls.</summary>
    /// <remarks>
    /// Nothing is registered for tests that only read, and a page that offered a Create button
    /// which then did nothing would be worse than one that offers none.
    /// </remarks>
    public bool CanEdit => _authoring is not null;

    /// <summary>Where definitions are kept, shown so a person can find the files.</summary>
    public string DefinitionsDirectory => _authoring?.DefinitionsDirectory ?? string.Empty;

    /// <summary>The name typed for a new event.</summary>
    public string NewEventName
    {
        get => _newEventName;
        set => SetProperty(ref _newEventName, value);
    }

    /// <summary>What to search the item catalog for.</summary>
    public string ItemQuery
    {
        get => _itemQuery;
        set => SetProperty(ref _itemQuery, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public IReadOnlyList<EventItemMatchViewModel> Matches
    {
        get => _matches;
        private set => SetProperty(ref _matches, value);
    }

    /// <summary>
    /// Deleting takes two presses, and this is the label that says which one is next.
    /// </summary>
    /// <remarks>
    /// A definition is a list somebody built by hand, and the button sits beside the ones that
    /// record a result. Two presses rather than a dialog: the second press is the confirmation,
    /// and changing the selection or reloading puts it back.
    /// </remarks>
    public string DeleteLabel => _confirmingDelete ? "Confirm delete" : "Delete event";

    private bool ConfirmingDelete
    {
        get => _confirmingDelete;
        set
        {
            if (SetProperty(ref _confirmingDelete, value))
            {
                OnPropertyChanged(nameof(DeleteLabel));
            }
        }
    }

    public string EmptyGuidance => EmptyGuidanceText;

    public string StateGuidance => StateGuidanceText;

    public IReadOnlyList<EventSummaryViewModel> Events
    {
        get => _events;
        private set => SetProperty(ref _events, value);
    }

    public IReadOnlyList<EventItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>True once at least one definition loaded; drives which panel the view shows.</summary>
    public bool HasEvents
    {
        get => _hasEvents;
        private set
        {
            if (SetProperty(ref _hasEvents, value))
            {
                OnPropertyChanged(nameof(HasNoEvents));
            }
        }
    }

    public bool HasNoEvents => !_hasEvents;

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

    public string Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public EventSummaryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            ConfirmingDelete = false;
            Matches = [];
            SearchStatus = string.Empty;
            if (value is not null)
            {
                _ = ShowEventAsync(value, CancellationToken.None);
            }
        }
    }

    /// <summary>Takes what the sync produced, rebuilding when the catalog actually changed.</summary>
    /// <remarks>
    /// The event definitions are local files and owe the sync nothing, but the items they name
    /// are looked up in the catalog, so before the first sync every applicable item on this
    /// page reads as its raw id. The count is the change signal; 0 -> N used to do nothing and
    /// the ids stayed until somebody pressed Reload.
    ///
    /// Fire and forget, because Apply is called from the shell's state pass and must not block
    /// it on a database read.
    /// </remarks>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        // Recorded states live in the active profile, so which profile is active is the piece of
        // runtime state that actually changes what this page shows.
        var profile = snapshot.Profile is null
            ? "no active profile"
            : $"{snapshot.Profile.Name} · {snapshot.Profile.GameMode}";
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items · results stored against {profile}";
        if (_knownItemCount == snapshot.Data.ItemCount)
        {
            return;
        }

        _knownItemCount = snapshot.Data.ItemCount;
        if (snapshot.Data.ItemCount > 0)
        {
            _ = LoadAsync();
        }
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public Task LoadAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken, null);

    /// <summary>Reads the catalog, optionally staying on the event that was being edited.</summary>
    /// <remarks>
    /// Every write reloads, because the catalog is the only place the definitions live and a
    /// page holding its own copy would be the second answer that could disagree. Staying on the
    /// event is what makes adding three items feel like adding three items rather than like
    /// three separate visits to the page.
    /// </remarks>
    private async Task LoadAsync(CancellationToken cancellationToken, string? selectEventId)
    {
        try
        {
            Status = "Reading local event definitions…";
            var definitions = await _catalog.GetAsync(cancellationToken).ConfigureAwait(true);
            _definitions = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

            var now = DateTimeOffset.UtcNow;
            Events = definitions.Select(definition => Describe(definition, now)).ToArray();
            HasEvents = Events.Count > 0;
            Selected = null;
            Items = [];
            Progress = "No event selected.";

            // An empty catalog is the ordinary out-of-season state. Saying so in the same tone as
            // a successful read is the whole point; the view shows the guidance panel instead.
            Status = HasEvents
                ? $"{Events.Count} definitions loaded"
                : "No definitions configured";
            Detail = HasEvents
                ? "Select an event to see the items it applies to."
                : string.Empty;

            // Last, so the detail line the selection produces is not overwritten by the one
            // that describes having no selection.
            if (selectEventId is not null)
            {
                Selected = Events.FirstOrDefault(
                    summary => string.Equals(summary.EventId, selectEventId, StringComparison.Ordinal));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _definitions = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
            Events = [];
            Items = [];
            HasEvents = false;
            Progress = "No event selected.";
            Status = $"Unreadable · {exception.Message}";
        }
    }

    private async Task ShowEventAsync(EventSummaryViewModel summary, CancellationToken cancellationToken)
    {
        // Only ids the catalog supplied ever reach the tracker, which throws for anything else.
        if (!_definitions.TryGetValue(summary.EventId, out var definition))
        {
            Items = [];
            Progress = "No event selected.";
            Detail = $"{summary.Name} is no longer loaded · reload";
            return;
        }

        try
        {
            Detail = $"Reading recorded results for {definition.Name}…";
            if (definition.ApplicableItemIds.Count == 0)
            {
                Items = [];
                Progress = "This definition lists no applicable items.";
                Detail = $"{definition.Name} lists no applicable items";
                return;
            }

            // One probe decides whether the tracker knows this event at all, so a tracker built
            // without definitions costs one caught exception rather than one per item.
            var progress = await TryReadProgressAsync(definition.Id, cancellationToken).ConfigureAwait(true);
            var canRecord = progress is not null;
            var rows = new List<EventItemViewModel>(definition.ApplicableItemIds.Count);
            foreach (var itemId in definition.ApplicableItemIds)
            {
                var item = await _itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
                var state = canRecord
                    ? await _tracker.GetItemStateAsync(definition.Id, itemId, cancellationToken).ConfigureAwait(true)
                    : EventItemState.Unknown;
                rows.Add(new(
                    itemId,
                    item?.Name ?? itemId,
                    canRecord ? DescribeState(state) : "Not tracked",
                    canRecord,
                    new AsyncDelegateCommand(() => SetStateAsync(definition.Id, itemId, EventItemState.Safe, CancellationToken.None)),
                    new AsyncDelegateCommand(() => SetStateAsync(definition.Id, itemId, EventItemState.Allergic, CancellationToken.None)),
                    new AsyncDelegateCommand(() => SetStateAsync(definition.Id, itemId, EventItemState.Untested, CancellationToken.None)),
                    new AsyncDelegateCommand(() => SetStateAsync(definition.Id, itemId, EventItemState.Unknown, CancellationToken.None)),
                    CanEdit,
                    new AsyncDelegateCommand(() => RemoveItemAsync(definition.Id, itemId, CancellationToken.None))));
            }

            Items = rows
                .OrderBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (progress is { } counts)
            {
                Progress =
                    $"{counts.Safe} safe · {counts.Allergic} allergic · {counts.Tested} of {counts.Total} tested · " +
                    $"{counts.Unknown} not yet recorded";
                var unnamed = Items.Count(row => string.Equals(row.ItemName, row.ItemId, StringComparison.Ordinal));
                Detail = unnamed == 0
                    ? $"{definition.Name} · {Items.Count} items"
                    : $"{definition.Name} · {Items.Count} items · {unnamed} shown by id";
            }
            else
            {
                Progress = "Not registered with the tracker";
                Detail = $"{definition.Name} is loaded but not registered with the tracker";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Progress = "No progress could be read.";
            Detail = $"Unreadable · {exception.Message}";
        }
    }

    /// <summary>
    /// Reads the event's progress, or returns <see langword="null"/> when the tracker does not
    /// know the event.
    /// </summary>
    /// <remarks>
    /// The catalog and the tracker are two independent lists of definitions. This page can only
    /// guarantee the ids it uses came from the catalog; whether the tracker was constructed with
    /// the same set is outside its reach, and a <see cref="KeyNotFoundException"/> is how that
    /// disagreement surfaces.
    /// </remarks>
    private async Task<EventProgress?> TryReadProgressAsync(string eventId, CancellationToken cancellationToken)
    {
        try
        {
            return await _tracker.GetProgressAsync(eventId, cancellationToken).ConfigureAwait(true);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task SetStateAsync(
        string eventId,
        string itemId,
        EventItemState state,
        CancellationToken cancellationToken)
    {
        // Re-check against the currently loaded definition: the tracker throws for an item that
        // is not part of the event just as it throws for an unknown event.
        if (!_definitions.TryGetValue(eventId, out var definition) ||
            !definition.ApplicableItemIds.Contains(itemId))
        {
            Detail = "That item is no longer in the definition · reload";
            return;
        }

        try
        {
            await _tracker.SetItemStateAsync(eventId, itemId, state, cancellationToken).ConfigureAwait(true);
            if (Selected is { } selected)
            {
                await ShowEventAsync(selected, cancellationToken).ConfigureAwait(true);
            }

            Detail = $"Recorded {DescribeState(state)} in {definition.Name}";
        }
        catch (KeyNotFoundException)
        {
            Detail = $"Not recorded · {definition.Name} is not registered with the tracker";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = $"Not recorded · {exception.Message}";
        }
    }

    /// <summary>Creates an empty event under the typed name and selects it.</summary>
    /// <remarks>
    /// Empty, because the items are chosen next and choosing them one at a time against a real
    /// list is the part a text editor was bad at. The id is made from the name so the file on
    /// disk is findable by somebody who later wants to edit it by hand, which the file format
    /// still allows.
    ///
    /// An id already in use is refused rather than replaced. Two events called the same thing is
    /// a state somebody chose; one silently overwriting the other is not.
    /// </remarks>
    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        if (_authoring is null)
        {
            return;
        }

        var name = NewEventName.Trim();
        if (name.Length == 0)
        {
            Status = "Type a name first";
            return;
        }

        var id = Slug(name);
        if (id.Length == 0)
        {
            Status = "That name has no letters or digits in it";
            return;
        }

        if (_definitions.ContainsKey(id))
        {
            Status = $"{name} already exists";
            return;
        }

        try
        {
            await _authoring.SaveAsync(
                new EventDefinition(
                    id,
                    name,
                    null,
                    null,
                    true,
                    new HashSet<string>(StringComparer.Ordinal),
                    "{}",
                    new DataProvenance("local event definition", DateTimeOffset.UtcNow)),
                cancellationToken).ConfigureAwait(true);
            NewEventName = string.Empty;
            await LoadAsync(cancellationToken, id).ConfigureAwait(true);
            Status = $"Created {name}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Not created · {exception.Message}";
        }
    }

    /// <summary>Deletes the selected event, on the second press.</summary>
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (_authoring is null || Selected is not { } summary)
        {
            return;
        }

        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            Detail = $"Press again to delete {summary.Name}";
            return;
        }

        try
        {
            await _authoring.DeleteAsync(summary.EventId, cancellationToken).ConfigureAwait(true);
            ConfirmingDelete = false;
            await LoadAsync(cancellationToken, null).ConfigureAwait(true);
            Status = $"Deleted {summary.Name}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConfirmingDelete = false;
            Status = $"Not deleted · {exception.Message}";
        }
    }

    /// <summary>Searches the item catalog for things to add to the selected event.</summary>
    /// <remarks>
    /// The items already on the event are left out of the results, so the list is what can be
    /// added rather than what matched.
    /// </remarks>
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (Selected is not { } summary || !_definitions.TryGetValue(summary.EventId, out var definition))
        {
            SearchStatus = "Select an event first";
            return;
        }

        var query = ItemQuery.Trim();
        if (query.Length == 0)
        {
            Matches = [];
            SearchStatus = string.Empty;
            return;
        }

        try
        {
            var hits = await _itemRepository.SearchAsync(query, MatchLimit, cancellationToken).ConfigureAwait(true);
            var rows = hits
                .Where(hit => !definition.ApplicableItemIds.Contains(hit.Item.Id))
                .Select(hit => new EventItemMatchViewModel(
                    hit.Item.Id,
                    hit.Item.Name,
                    new AsyncDelegateCommand(() => AddItemAsync(definition.Id, hit.Item.Id, CancellationToken.None))))
                .ToArray();
            Matches = rows;
            SearchStatus = rows.Length == 0
                ? $"Nothing to add for \"{query}\""
                : $"{rows.Length} to add";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Matches = [];
            SearchStatus = $"Search failed · {exception.Message}";
        }
    }

    private Task AddItemAsync(string eventId, string itemId, CancellationToken cancellationToken) =>
        ChangeItemsAsync(eventId, items => items.Add(itemId), "Added", cancellationToken);

    private Task RemoveItemAsync(string eventId, string itemId, CancellationToken cancellationToken) =>
        ChangeItemsAsync(eventId, items => items.Remove(itemId), "Removed", cancellationToken);

    /// <summary>Rewrites one definition's item list and reloads onto it.</summary>
    /// <remarks>
    /// The recorded results are keyed by event and item together and are not touched here, so an
    /// item removed by accident and added back still has what was recorded against it.
    /// </remarks>
    private async Task ChangeItemsAsync(
        string eventId,
        Action<HashSet<string>> change,
        string what,
        CancellationToken cancellationToken)
    {
        if (_authoring is null || !_definitions.TryGetValue(eventId, out var definition))
        {
            return;
        }

        var items = new HashSet<string>(definition.ApplicableItemIds, StringComparer.Ordinal);
        change(items);
        try
        {
            await _authoring.SaveAsync(definition with { ApplicableItemIds = items }, cancellationToken)
                .ConfigureAwait(true);
            var query = ItemQuery;
            await LoadAsync(cancellationToken, eventId).ConfigureAwait(true);
            ItemQuery = query;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            Detail = $"{what} · {items.Count} item(s) in {definition.Name}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = $"Not saved · {exception.Message}";
        }
    }

    /// <summary>An id made from a name, using only the characters a file name can hold.</summary>
    private static string Slug(string name)
    {
        var slug = new System.Text.StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-');
    }

    /// <summary>Shapes one definition for the list, including whether it is in force.</summary>
    /// <remarks>
    /// A switched-off or out-of-season definition is listed exactly like any other and labelled,
    /// because hiding it would make "no event is configured" and "an event is configured but not
    /// running" look identical, which is the distinction the whole page turns on.
    /// </remarks>
    private static EventSummaryViewModel Describe(EventDefinition definition, DateTimeOffset now)
    {
        var season = !definition.Active
            ? "Switched off in its definition file"
            : definition.StartUtc is { } start && start > now
                ? $"Not started; begins {Local(start)}"
                : definition.EndUtc is { } end && end < now
                    ? $"Out of season; ended {Local(end)}"
                    : "In season";

        var window = DescribeWindow(definition.StartUtc, definition.EndUtc);

        var confidence = definition.Provenance.Confidence is { } value
            ? value.Value.ToString("0.00", CultureInfo.CurrentCulture)
            : "unstated";

        return new(
            definition.Id,
            definition.Name,
            season,
            window,
            definition.ApplicableItemIds.Count == 0
                ? "No applicable items listed"
                : $"{definition.ApplicableItemIds.Count:N0} applicable item(s)",
            $"{definition.Provenance.Source} · confidence {confidence} · {definition.Provenance.Reference ?? "no reference given"}");
    }

    /// <summary>States the dates a definition carries, with an open end left open.</summary>
    /// <remarks>
    /// Both dates are optional in the file format, and an absent one means the author did not
    /// say, not that the window is unbounded in some asserted sense.
    /// </remarks>
    private static string DescribeWindow(DateTimeOffset? startUtc, DateTimeOffset? endUtc)
    {
        if (startUtc is { } from)
        {
            return endUtc is { } until ? $"{Local(from)} to {Local(until)}" : $"From {Local(from)}";
        }

        return endUtc is { } close ? $"Until {Local(close)}" : "No dates recorded";
    }

    private static string DescribeState(EventItemState state) => state switch
    {
        EventItemState.Safe => "Safe",
        EventItemState.Allergic => "Allergic",
        EventItemState.Untested => "Untested",
        _ => "Unknown",
    };

    private static string Local(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
