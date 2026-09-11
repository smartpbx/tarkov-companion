using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.App.ViewModels;

/// <summary>One hand-authored event definition, as it reads in the list.</summary>
public sealed record EventSummaryViewModel(
    string EventId,
    string Name,
    string Season,
    string Window,
    string Items,
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
    ICommand MarkUnknownCommand);

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
    private const string EmptyGuidanceText =
        "An event is a seasonal rule set in which some consumables affect each player " +
        "differently, so this page lets you mark every applicable item Safe or Allergic and " +
        "keeps the result in your local profile. Definitions are written by hand because " +
        "json.tarkov.dev has no events endpoint: put a JSON file in " +
        "%LOCALAPPDATA%\\TarkovCompanion\\Config\\Events (Data\\Config\\Events for a portable " +
        "install) and restart the app, and see assets/events/README.md for the file shape.";

    private const string StateGuidanceText =
        "Untested is the default for every applicable item. Unknown records that you cannot say; " +
        "it is also what the tracker reports for an item outside the event. Nothing on this page " +
        "is read from the game.";

    private readonly IEventCatalog _catalog;
    private readonly IEventTrackerService _tracker;
    private readonly IItemRepository _itemRepository;

    private IReadOnlyDictionary<string, EventDefinition> _definitions =
        new Dictionary<string, EventDefinition>(StringComparer.Ordinal);

    private IReadOnlyList<EventSummaryViewModel> _events = [];
    private IReadOnlyList<EventItemViewModel> _items = [];
    private EventSummaryViewModel? _selected;
    private bool _hasEvents;
    private string _status = "Reading local event definitions…";
    private string _detail = "Select an event to see the items it applies to.";
    private string _progress = "No event selected.";

    public EventsPageViewModel(
        IEventCatalog catalog,
        IEventTrackerService tracker,
        IItemRepository itemRepository)
        : base("Events", "Locally configured seasonal events and the results you record by hand", "Runtime state not loaded")
    {
        _catalog = catalog;
        _tracker = tracker;
        _itemRepository = itemRepository;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

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
            if (SetProperty(ref _selected, value) && value is not null)
            {
                _ = ShowEventAsync(value, CancellationToken.None);
            }
        }
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        // Recorded states live in the active profile, so which profile is active is the piece of
        // runtime state that actually changes what this page shows.
        var profile = snapshot.Profile is null
            ? "no active profile"
            : $"{snapshot.Profile.Name} · {snapshot.Profile.GameMode}";
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items · results stored against {profile}";
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
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
                ? $"{Events.Count} event definition(s) loaded from your local events folder."
                : "No event definitions are configured. That is the ordinary state out of season, not an error.";
            Detail = HasEvents
                ? "Select an event to see the items it applies to."
                : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _definitions = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
            Events = [];
            Items = [];
            HasEvents = false;
            Progress = "No event selected.";
            Status = $"The local event definitions could not be read: {exception.Message}";
        }
    }

    private async Task ShowEventAsync(EventSummaryViewModel summary, CancellationToken cancellationToken)
    {
        // Only ids the catalog supplied ever reach the tracker, which throws for anything else.
        if (!_definitions.TryGetValue(summary.EventId, out var definition))
        {
            Items = [];
            Progress = "No event selected.";
            Detail = $"{summary.Name} is no longer in the loaded catalog. Reload to pick it up again.";
            return;
        }

        try
        {
            Detail = $"Reading recorded results for {definition.Name}…";
            if (definition.ApplicableItemIds.Count == 0)
            {
                Items = [];
                Progress = "This definition lists no applicable items.";
                Detail = $"{definition.Name} lists no applicable items, so there is nothing to record against it yet.";
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
                    new AsyncDelegateCommand(() => SetStateAsync(definition.Id, itemId, EventItemState.Unknown, CancellationToken.None))));
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
                    ? $"{definition.Name}: {Items.Count} applicable item(s)."
                    : $"{definition.Name}: {Items.Count} applicable item(s); {unnamed} are shown by id because no synced item matches them.";
            }
            else
            {
                Progress = "No progress: this event is not registered with the tracker.";
                Detail =
                    $"{definition.Name} loaded from its file, but the progress tracker was built without it, " +
                    "so no result can be read or recorded for it in this session.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Progress = "No progress could be read.";
            Detail = $"{summary.Name} could not be read: {exception.Message}";
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
            Detail = "That item is no longer part of the loaded event definition. Reload the page.";
            return;
        }

        try
        {
            await _tracker.SetItemStateAsync(eventId, itemId, state, cancellationToken).ConfigureAwait(true);
            if (Selected is { } selected)
            {
                await ShowEventAsync(selected, cancellationToken).ConfigureAwait(true);
            }

            Detail = $"Recorded {DescribeState(state)} for that item in {definition.Name}.";
        }
        catch (KeyNotFoundException)
        {
            Detail =
                $"{definition.Name} is loaded from its file but the progress tracker was built " +
                "without it, so nothing was recorded.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = $"That result could not be recorded: {exception.Message}";
        }
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
