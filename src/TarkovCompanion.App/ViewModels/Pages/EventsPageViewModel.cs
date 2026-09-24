using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Localization;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Events;
using TarkovCompanion.Application.Services.Profile;
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

    private static string EmptyGuidanceText => PlanText.EventsEmptyGuidance;

    private static string StateGuidanceText => PlanText.EventsStateGuidance;

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
    private string _status = PlanText.EventsReadingDefinitions;
    private string _detail = PlanText.EventsSelectAnEvent;
    private string _progress = PlanText.EventsNoEventSelected;
    private string _newEventName = string.Empty;
    private string _itemQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _confirmingDelete;
    // [V2 rough package 60 — Plan] #288: the schedule the file format has always carried and the
    // page never let anybody set.
    private string _scheduleStart = string.Empty;
    private string _scheduleEnd = string.Empty;
    private string _renameTo = string.Empty;
    private string _scheduleStatus = string.Empty;
    private string _schedulePreview = string.Empty;
    private bool _isArchived;
    private string _rulePreview = string.Empty;
    private string _ruleStatus = string.Empty;
    private bool _hasRulePreview;
    private bool _hasRuleIssues;

    public EventsPageViewModel(
        IEventCatalog catalog,
        IEventTrackerService tracker,
        IItemRepository itemRepository,
        IEventAuthoring? authoring = null,
        Func<CancellationToken, Task<IReadOnlyList<EventTargetChoice>>>? traderChoices = null,
        Func<CancellationToken, Task<IReadOnlyList<EventTargetChoice>>>? mapChoices = null)
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
        SaveScheduleCommand = new AsyncDelegateCommand(() => SaveScheduleAsync(CancellationToken.None));
        ToggleArchivedCommand = new AsyncDelegateCommand(() => SetArchivedAsync(!IsArchived, CancellationToken.None));
        DuplicateCommand = new AsyncDelegateCommand(() => DuplicateAsync(CancellationToken.None));
        _traderChoices = traderChoices;
        _mapChoices = mapChoices;
        RuleEditor.Changed += (_, _) => ShowRuleState();
    }

    private readonly Func<CancellationToken, Task<IReadOnlyList<EventTargetChoice>>>? _traderChoices;
    private readonly Func<CancellationToken, Task<IReadOnlyList<EventTargetChoice>>>? _mapChoices;
    private bool _choicesLoaded;
    private string _history = string.Empty;

    /// <summary>The selected event's typed effects, as editable rows (#288).</summary>
    public EventRuleEditorViewModel RuleEditor { get; } = new();

    /// <summary>Copies the selected event, rules and items included, under a new name.</summary>
    public AsyncDelegateCommand DuplicateCommand { get; }

    /// <summary>When the selected definition was last written.</summary>
    /// <remarks>
    /// The definition file records when it was last saved and nothing about who saved it, so this
    /// says only when. A file written by hand without a date reads as the file's own modified time.
    /// </remarks>
    public string History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public AsyncDelegateCommand CreateCommand { get; }

    public AsyncDelegateCommand SearchCommand { get; }

    public AsyncDelegateCommand DeleteCommand { get; }

    /// <summary>Writes the window, the name and the archived flag back to the definition file.</summary>
    public AsyncDelegateCommand SaveScheduleCommand { get; }

    /// <summary>Switches the selected event off, or back on. Keeps it; never deletes.</summary>
    public AsyncDelegateCommand ToggleArchivedCommand { get; }

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
    public string DeleteLabel => _confirmingDelete ? PlanText.EventsConfirmDelete : PlanText.EventsDeleteEvent;

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

    /// <summary>The window's first day, as typed. Empty means the author did not say.</summary>
    /// <remarks>
    /// A date typed as text rather than a picker: both ends are optional, and every date control
    /// this application has makes "no date" harder to express than a date. Parsed against the
    /// current culture first, then the invariant one, so both 12/10/2026 and 2026-10-12 read.
    /// </remarks>
    public string ScheduleStart
    {
        get => _scheduleStart;
        set
        {
            if (SetProperty(ref _scheduleStart, value))
            {
                RefreshSchedulePreview();
            }
        }
    }

    public string ScheduleEnd
    {
        get => _scheduleEnd;
        set
        {
            if (SetProperty(ref _scheduleEnd, value))
            {
                RefreshSchedulePreview();
            }
        }
    }

    /// <summary>A new name for the selected event, or its current one.</summary>
    public string RenameTo
    {
        get => _renameTo;
        set => SetProperty(ref _renameTo, value);
    }

    /// <summary>What the last save said, or why it was refused.</summary>
    public string ScheduleStatus
    {
        get => _scheduleStatus;
        private set => SetProperty(ref _scheduleStatus, value);
    }

    /// <summary>What the typed window would mean, before it is saved.</summary>
    public string SchedulePreview
    {
        get => _schedulePreview;
        private set => SetProperty(ref _schedulePreview, value);
    }

    /// <summary>Whether the selected event is switched off.</summary>
    public bool IsArchived
    {
        get => _isArchived;
        private set
        {
            if (SetProperty(ref _isArchived, value))
            {
                OnPropertyChanged(nameof(ArchiveLabel));
            }
        }
    }

    /// <summary>What the archive button will do, said on the button.</summary>
    public string ArchiveLabel => IsArchived ? PlanText.EventsBringBack : PlanText.EventsArchiveEvent;

    /// <summary>The effects this definition would apply while its schedule is active.</summary>
    public string RulePreview
    {
        get => _rulePreview;
        private set => SetProperty(ref _rulePreview, value);
    }

    public string RuleStatus
    {
        get => _ruleStatus;
        private set => SetProperty(ref _ruleStatus, value);
    }

    public bool HasRulePreview
    {
        get => _hasRulePreview;
        private set => SetProperty(ref _hasRulePreview, value);
    }

    public bool HasRuleIssues
    {
        get => _hasRuleIssues;
        private set => SetProperty(ref _hasRuleIssues, value);
    }

    public bool HasRuleInformation => Selected is not null;

    /// <summary>Whether a schedule can be edited at all: only with a selection and a writer.</summary>
    public bool CanEditSchedule => CanEdit && Selected is not null;

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
            ScheduleStatus = string.Empty;
            OnPropertyChanged(nameof(CanEditSchedule));
            OnPropertyChanged(nameof(HasRuleInformation));
            LoadScheduleFields(value);
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
            LoadAsync().Observe("events", "reload");
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
            Status = PlanText.EventsReadingDefinitions;
            await LoadChoicesAsync(cancellationToken).ConfigureAwait(true);
            var definitions = await _catalog.GetAsync(cancellationToken).ConfigureAwait(true);
            _definitions = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

            var now = DateTimeOffset.UtcNow;
            Events = definitions.Select(definition => Describe(definition, now)).ToArray();
            HasEvents = Events.Count > 0;
            Selected = null;
            Items = [];
            Progress = PlanText.EventsNoEventSelected;

            // An empty catalog is the ordinary out-of-season state. Saying so in the same tone as
            // a successful read is the whole point; the view shows the guidance panel instead.
            Status = HasEvents
                ? PlanText.EventsDefinitionsLoaded(Events.Count)
                : PlanText.EventsNoDefinitions;
            Detail = HasEvents
                ? PlanText.EventsSelectAnEvent
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
            Progress = PlanText.EventsNoEventSelected;
            Status = PlanText.EventsUnreadable(exception.Message);
        }
    }

    private async Task ShowEventAsync(EventSummaryViewModel summary, CancellationToken cancellationToken)
    {
        // Only ids the catalog supplied ever reach the tracker, which throws for anything else.
        if (!_definitions.TryGetValue(summary.EventId, out var definition))
        {
            Items = [];
            Progress = PlanText.EventsNoEventSelected;
            Detail = PlanText.EventsNoLongerLoaded(summary.Name);
            return;
        }

        try
        {
            Detail = PlanText.EventsReadingResults(definition.Name);
            if (definition.ApplicableItemIds.Count == 0)
            {
                Items = [];
                Progress = PlanText.EventsNoApplicableItemsProgress;
                Detail = PlanText.EventsListsNoItems(definition.Name);
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
                    canRecord ? DescribeState(state) : PlanText.EventsNotTracked,
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
                    PlanText.EventsProgress(counts.Safe, counts.Allergic, counts.Tested, counts.Total, counts.Unknown);
                var unnamed = Items.Count(row => string.Equals(row.ItemName, row.ItemId, StringComparison.Ordinal));
                Detail = unnamed == 0
                    ? PlanText.EventsItems(definition.Name, Items.Count)
                    : PlanText.EventsItemsShownById(definition.Name, Items.Count, unnamed);
            }
            else
            {
                Progress = PlanText.EventsNotRegistered;
                Detail = PlanText.EventsLoadedNotRegistered(definition.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Progress = PlanText.EventsNoProgress;
            Detail = PlanText.EventsUnreadable(exception.Message);
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

    private readonly EventStateUndo _undo = new();
    private AsyncDelegateCommand? _undoCommand;

    /// <summary>Takes back the last state recorded on this page (#285). One step.</summary>
    public AsyncDelegateCommand UndoCommand => _undoCommand ??= new(() => UndoAsync(CancellationToken.None));

    public bool CanUndo => _undo.CanUndo;

    public string UndoLabel => _undo.Last is { } last
        ? PlanText.EventsUndoLabel(last.ItemName, DescribeState(last.Previous))
        : string.Empty;

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
    }

    internal async Task UndoAsync(CancellationToken cancellationToken)
    {
        if (_undo.Take() is not { } change)
        {
            return;
        }

        try
        {
            await _tracker.SetItemStateAsync(change.EventId, change.ItemId, change.Previous, cancellationToken).ConfigureAwait(true);
            if (Selected is { } selected)
            {
                await ShowEventAsync(selected, cancellationToken).ConfigureAwait(true);
            }

            Detail = PlanText.EventsUndone(change.ItemName, DescribeState(change.Previous));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = PlanText.EventsNotUndone(exception.Message);
        }
        finally
        {
            NotifyUndo();
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
            Detail = PlanText.EventsItemNoLongerInDefinition;
            return;
        }

        try
        {
            var previous = await _tracker.GetItemStateAsync(eventId, itemId, cancellationToken).ConfigureAwait(true);
            await _tracker.SetItemStateAsync(eventId, itemId, state, cancellationToken).ConfigureAwait(true);
            var itemName = Items.FirstOrDefault(row => string.Equals(row.ItemId, itemId, StringComparison.Ordinal))?.ItemName ?? itemId;
            _undo.Record(new(eventId, itemId, itemName, previous, state));
            if (Selected is { } selected)
            {
                await ShowEventAsync(selected, cancellationToken).ConfigureAwait(true);
            }

            Detail = PlanText.EventsRecordedIn(DescribeState(state), definition.Name);
            NotifyUndo();
        }
        catch (KeyNotFoundException)
        {
            Detail = PlanText.EventsNotRecordedUnregistered(definition.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = PlanText.EventsNotRecorded(exception.Message);
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
            Status = PlanText.EventsTypeANameFirst;
            return;
        }

        var id = Slug(name);
        if (id.Length == 0)
        {
            Status = PlanText.EventsNameHasNoLetters;
            return;
        }

        if (_definitions.ContainsKey(id))
        {
            Status = PlanText.EventsAlreadyExists(name);
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
            Status = PlanText.EventsCreated(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = PlanText.EventsNotCreated(exception.Message);
        }
    }

    /// <summary>
    /// Fills the schedule fields from whichever event is selected.
    /// </summary>
    /// <remarks>
    /// Read from the definition rather than kept as edits across a selection change: leaving one
    /// event's dates in the boxes while another is selected is how somebody saves a window onto
    /// the wrong event.
    /// </remarks>
    private void LoadScheduleFields(EventSummaryViewModel? summary)
    {
        if (summary is null || !_definitions.TryGetValue(summary.EventId, out var definition))
        {
            ScheduleStart = string.Empty;
            ScheduleEnd = string.Empty;
            RenameTo = string.Empty;
            IsArchived = false;
            SchedulePreview = string.Empty;
            History = string.Empty;
            RuleEditor.Clear();
            RulePreview = string.Empty;
            RuleStatus = string.Empty;
            HasRulePreview = false;
            HasRuleIssues = false;
            return;
        }

        // The player's own calendar day, written the same way in every culture so it reads back.
        ScheduleStart = definition.StartUtc is { } start ? LocalTime.SortableDate(start) : string.Empty;
        ScheduleEnd = definition.EndUtc is { } end ? LocalTime.SortableDate(end) : string.Empty;
        RenameTo = definition.Name;
        IsArchived = !definition.Active;
        History = PlanText.EventsLastChanged(Local(definition.Provenance.ObservedUtc));
        RefreshSchedulePreview();
        RuleEditor.Load(definition.RulesJson);
    }

    /// <summary>Reads the trader and map pickers once; a failure leaves typed ids, not an error.</summary>
    private async Task LoadChoicesAsync(CancellationToken cancellationToken)
    {
        if (_choicesLoaded || (_traderChoices is null && _mapChoices is null))
        {
            return;
        }

        IReadOnlyList<EventTargetChoice> traders = [], maps = [];
        try
        {
            traders = _traderChoices is null ? [] : await _traderChoices(cancellationToken).ConfigureAwait(true);
            maps = _mapChoices is null ? [] : await _mapChoices(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("events", "read trader and map choices", exception.Message);
        }

        // Only a read that found something is kept: before the first sync both lists are empty,
        // and the next reload should ask again.
        _choicesLoaded = traders.Count > 0 && maps.Count > 0;
        RuleEditor.UseChoices(traders, maps);
    }

    /// <summary>The preview and status lines, from the rows as they stand.</summary>
    private void ShowRuleState()
    {
        var parsed = RuleEditor.Result.Parsed;
        HasRuleIssues = !parsed.IsValid;
        if (!parsed.IsValid)
        {
            HasRulePreview = false;
            RulePreview = string.Empty;
            RuleStatus = RuleEditor.Status + " · " + string.Join(" · ", parsed.Issues.Take(3));
            return;
        }

        HasRulePreview = parsed.Rules.Effects.Count > 0;
        RulePreview = HasRulePreview
            ? PlanText.EventsWhileActive(EventRuleText.Preview(parsed.Rules))
            : PlanText.EventsNoTypedEffects;
        RuleStatus = PlanText.EventsEffectsValidated(parsed.Rules.Effects.Count);
    }

    /// <summary>Says what the typed window would mean, before anything is written.</summary>
    private void RefreshSchedulePreview()
    {
        if (Selected is null)
        {
            SchedulePreview = string.Empty;
            return;
        }

        if (!TryReadDate(ScheduleStart, out var start))
        {
            SchedulePreview = PlanText.EventsFirstDateNotDate;
            return;
        }

        if (!TryReadDate(ScheduleEnd, out var end))
        {
            SchedulePreview = PlanText.EventsLastDateNotDate;
            return;
        }

        if (start is { } from && end is { } until && until < from)
        {
            SchedulePreview = PlanText.EventsLastBeforeFirst;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SchedulePreview = IsArchived
            ? PlanText.EventsArchivedPreview
            : start is { } begins && begins > now
                ? PlanText.EventsStartsIn(DescribeWindow(start, end), Days(begins - now))
                : end is { } closes && closes < now
                    ? PlanText.EventsEndedAgo(DescribeWindow(start, end), Days(now - closes))
                    : PlanText.EventsInSeasonNow(DescribeWindow(start, end));
    }

    private static string Days(TimeSpan span) => span.TotalDays >= 1
        ? PlanText.EventsDays((int)span.TotalDays)
        : PlanText.EventsHours(Math.Max(1, (int)span.TotalHours));

    /// <summary>
    /// Reads a typed date, treating empty as "the author did not say".
    /// </summary>
    /// <remarks>
    /// Returns true for an empty box with a null date, because an absent end is a valid window
    /// and refusing it would make an open-ended event impossible to save. The named day is the
    /// player's local calendar day; storage remains the corresponding UTC instant.
    /// </remarks>
    internal static bool TryReadDate(string? input, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (!DateTime.TryParse(input.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            && !DateTime.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return false;
        }

        var localMidnight = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified);
        if (LocalTime.Zone.IsInvalidTime(localMidnight))
        {
            return false;
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, LocalTime.Zone);
        value = new DateTimeOffset(utc, TimeSpan.Zero);
        return true;
    }

    /// <summary>Writes the window, the name and the archived flag onto the selected event.</summary>
    private async Task SaveScheduleAsync(CancellationToken cancellationToken)
    {
        if (_authoring is null
            || Selected is not { } summary
            || !_definitions.TryGetValue(summary.EventId, out var definition))
        {
            return;
        }

        if (!TryReadDate(ScheduleStart, out var start) || !TryReadDate(ScheduleEnd, out var end))
        {
            ScheduleStatus = PlanText.EventsNotSavedBadDate;
            return;
        }

        if (start is { } from && end is { } until && until < from)
        {
            ScheduleStatus = PlanText.EventsNotSavedLastBeforeFirst;
            return;
        }

        var name = RenameTo.Trim();
        if (name.Length == 0)
        {
            ScheduleStatus = PlanText.EventsNotSavedNeedsName;
            return;
        }

        // Untouched rules keep the text they were stored as, so archiving a hand-written event
        // with a rule this build cannot read is not refused over a rule nobody changed.
        var rulesJson = definition.RulesJson;
        if (RuleEditor.IsDirty)
        {
            if (!RuleEditor.IsValid)
            {
                ScheduleStatus = PlanText.EventsNotSavedFixEffects;
                return;
            }

            rulesJson = RuleEditor.Result.RulesJson;
        }

        try
        {
            // The id is kept even when the name changes: it is what the recorded results are
            // stored against, and renaming an event must not orphan what the player recorded.
            await _authoring.SaveAsync(
                definition with
                {
                    Name = name,
                    StartUtc = start,
                    EndUtc = end,
                    Active = !IsArchived,
                    RulesJson = rulesJson,
                    Provenance = definition.Provenance with { ObservedUtc = DateTimeOffset.UtcNow },
                },
                cancellationToken).ConfigureAwait(true);
            await LoadAsync(cancellationToken, definition.Id).ConfigureAwait(true);
            ScheduleStatus = PlanText.EventsSaved(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduleStatus = PlanText.EventsNotSaved(exception.Message);
        }
    }

    /// <summary>Copies the selected event as saved, under "name copy", and selects the copy.</summary>
    /// <remarks>
    /// The copy is how next year's event starts from this year's rules and items. What was recorded
    /// against the original stays with the original: it is keyed by the original's id, and a
    /// result recorded in one season is not evidence about the next.
    /// </remarks>
    private async Task DuplicateAsync(CancellationToken cancellationToken)
    {
        if (_authoring is null
            || Selected is not { } summary
            || !_definitions.TryGetValue(summary.EventId, out var definition))
        {
            return;
        }

        var name = $"{definition.Name} copy";
        var id = Slug(name);
        for (var suffix = 2; _definitions.ContainsKey(id); suffix++)
        {
            name = $"{definition.Name} copy {suffix}";
            id = Slug(name);
        }

        try
        {
            await _authoring.SaveAsync(
                definition with
                {
                    Id = id,
                    Name = name,
                    ApplicableItemIds = new HashSet<string>(definition.ApplicableItemIds, StringComparer.Ordinal),
                    Provenance = definition.Provenance with { ObservedUtc = DateTimeOffset.UtcNow },
                },
                cancellationToken).ConfigureAwait(true);
            await LoadAsync(cancellationToken, id).ConfigureAwait(true);
            Status = PlanText.EventsCreated(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = PlanText.EventsNotCopied(exception.Message);
        }
    }

    /// <summary>Switches an event off or back on, keeping it and everything recorded against it.</summary>
    private async Task SetArchivedAsync(bool archived, CancellationToken cancellationToken)
    {
        IsArchived = archived;
        RefreshSchedulePreview();
        await SaveScheduleAsync(cancellationToken).ConfigureAwait(true);
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
            Detail = PlanText.EventsPressAgainToDelete(summary.Name);
            return;
        }

        try
        {
            await _authoring.DeleteAsync(summary.EventId, cancellationToken).ConfigureAwait(true);
            _undo.Forget(summary.EventId);
            NotifyUndo();
            ConfirmingDelete = false;
            await LoadAsync(cancellationToken, null).ConfigureAwait(true);
            Status = PlanText.EventsDeleted(summary.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConfirmingDelete = false;
            Status = PlanText.EventsNotDeleted(exception.Message);
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
            SearchStatus = PlanText.EventsSelectAnEventFirst;
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
                ? PlanText.EventsNothingToAdd(query)
                : PlanText.EventsToAdd(rows.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Matches = [];
            SearchStatus = PlanText.EventsSearchFailed(exception.Message);
        }
    }

    private Task AddItemAsync(string eventId, string itemId, CancellationToken cancellationToken) =>
        ChangeItemsAsync(eventId, items => items.Add(itemId), PlanText.EventsAddedAction, cancellationToken);

    private Task RemoveItemAsync(string eventId, string itemId, CancellationToken cancellationToken) =>
        ChangeItemsAsync(eventId, items => items.Remove(itemId), PlanText.EventsRemovedAction, cancellationToken);

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
            if (_undo.Last is { } remembered && !items.Contains(remembered.ItemId))
            {
                _undo.Forget(eventId, remembered.ItemId);
                NotifyUndo();
            }

            var query = ItemQuery;
            await LoadAsync(cancellationToken, eventId).ConfigureAwait(true);
            ItemQuery = query;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            Detail = PlanText.EventsItemsChanged(what, items.Count, definition.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = PlanText.EventsNotSaved(exception.Message);
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
            ? PlanText.EventsSwitchedOff
            : definition.StartUtc is { } start && start > now
                ? PlanText.EventsNotStarted(Local(start))
                : definition.EndUtc is { } end && end < now
                    ? PlanText.EventsOutOfSeason(Local(end))
                    : PlanText.EventsInSeason;

        var window = DescribeWindow(definition.StartUtc, definition.EndUtc);

        var confidence = definition.Provenance.Confidence is { } value
            ? value.Value.ToString("0.00", CultureInfo.CurrentCulture)
            : PlanText.EventsUnstated;

        return new(
            definition.Id,
            definition.Name,
            season,
            window,
            definition.ApplicableItemIds.Count == 0
                ? PlanText.EventsNoApplicableItemsListed
                : PlanText.EventsApplicableItems(definition.ApplicableItemIds.Count),
            PlanText.EventsProvenance(definition.Provenance.Source, confidence, definition.Provenance.Reference ?? PlanText.EventsNoReference));
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
            return endUtc is { } until ? PlanText.EventsWindow(Local(from), Local(until)) : PlanText.EventsFrom(Local(from));
        }

        return endUtc is { } close ? PlanText.EventsUntil(Local(close)) : PlanText.EventsNoDates;
    }

    private static string DescribeState(EventItemState state) => state switch
    {
        EventItemState.Safe => PlanText.EventsSafe,
        EventItemState.Allergic => PlanText.EventsAllergic,
        EventItemState.Untested => PlanText.EventsUntested,
        _ => PlanText.EventsUnknown,
    };

    private static string Local(DateTimeOffset timestamp) =>
        LocalTime.Moment(timestamp);
}
