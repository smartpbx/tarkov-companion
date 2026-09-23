using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Events;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels;

/// <summary>A trader or map offered in an effect row's picker.</summary>
public sealed record EventTargetChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Where the pickers' traders and maps come from.</summary>
public static class EventTargetChoices
{
    public static async Task<IReadOnlyList<EventTargetChoice>> TradersAsync(
        ITraderCatalog traders,
        CancellationToken cancellationToken)
    {
        var names = await traders.GetNamesAsync(cancellationToken).ConfigureAwait(false);
        return names
            .Select(pair => new EventTargetChoice(pair.Key, pair.Value))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>The maps the map catalog lists, by the game id Plan and quests use.</summary>
    /// <remarks>
    /// Plan closes a map by the game's id ("5714dbc024597771384a510d"), not by the catalog slug
    /// ("interchange"), so a closure saved under the slug would preview as "Interchange closed"
    /// and close nothing. The slug is kept only when the map data has no game id for it yet.
    /// </remarks>
    public static async Task<IReadOnlyList<EventTargetChoice>> MapsAsync(
        IReadOnlyList<MapLocation> locations,
        IMapDataService mapData,
        CancellationToken cancellationToken)
    {
        var choices = new List<EventTargetChoice>(locations.Count);
        foreach (var location in locations)
        {
            var id = location.SourceId;
            if (string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    id = (await mapData.GetAsync(location.Id, cancellationToken).ConfigureAwait(false))?.GameId;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    id = null;
                }
            }

            choices.Add(new(string.IsNullOrWhiteSpace(id) ? location.Id : id, location.Name));
        }

        return choices
            .DistinctBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}

/// <summary>One effect in the Events editor, with the validator's complaint next to its field.</summary>
public sealed class EventEffectRowViewModel : BindableViewModel
{
    private readonly EventRuleEditorViewModel _owner;
    private readonly EventEffectDraft _loaded;
    private string _targetId;
    private string _targetName;
    private string _targetText;
    private EventTargetChoice? _selectedChoice;
    private string _multiplier;
    private bool _isOpen;
    private string _startText;
    private string _endText;
    private string _targetError = string.Empty;
    private string _valueError = string.Empty;
    private string _rowError = string.Empty;

    internal EventEffectRowViewModel(
        EventRuleEditorViewModel owner,
        EventEffectDraft draft,
        IReadOnlyList<EventTargetChoice> choices)
    {
        _owner = owner;
        _loaded = draft;
        Kind = draft.Kind;
        _targetId = draft.TargetId;
        _targetName = draft.TargetName;
        _targetText = draft.TargetName.Length > 0 ? draft.TargetName : draft.TargetId;
        _multiplier = draft.Multiplier;
        _isOpen = draft.Enabled;
        _startText = draft.StartUtc is { } start ? LocalTime.SortableDate(start) : draft.StartRaw ?? string.Empty;
        _endText = draft.EndUtc is { } end ? LocalTime.SortableDate(end) : draft.EndRaw ?? string.Empty;

        // A rule that names a trader or map the catalog does not list (yet) keeps it as a choice,
        // so opening the event does not quietly retarget the rule to nothing.
        Choices = draft.TargetId.Length > 0 &&
                  !choices.Any(choice => string.Equals(choice.Id, draft.TargetId, StringComparison.OrdinalIgnoreCase))
            ? [.. choices, new EventTargetChoice(draft.TargetId, draft.TargetName.Length > 0 ? draft.TargetName : draft.TargetId)]
            : choices;
        _selectedChoice = Choices.FirstOrDefault(choice =>
            string.Equals(choice.Id, draft.TargetId, StringComparison.OrdinalIgnoreCase));
        RemoveCommand = new DelegateCommand(() => _owner.Remove(this));
    }

    public EventEffectKind Kind { get; }

    public string KindLabel => Kind switch
    {
        EventEffectKind.TraderPriceMultiplier => "Trader prices",
        EventEffectKind.FleaAvailability => "Flea market",
        EventEffectKind.MapAvailability => "Map",
        EventEffectKind.BossSpawnMultiplier => "Boss spawns",
        EventEffectKind.QuestAvailabilityWindow => "Quest window",
        _ => "Unknown effect",
    };

    public IReadOnlyList<EventTargetChoice> Choices { get; }

    private bool NeedsTarget => Kind is not (EventEffectKind.FleaAvailability or EventEffectKind.Unknown);

    /// <summary>Traders and maps are picked when the catalog has them; typed otherwise.</summary>
    public bool UsesPicker =>
        Kind is EventEffectKind.TraderPriceMultiplier or EventEffectKind.MapAvailability && Choices.Count > 0;

    public bool UsesTargetText => NeedsTarget && !UsesPicker;

    public string TargetPlaceholder => Kind switch
    {
        EventEffectKind.TraderPriceMultiplier => "Trader id",
        EventEffectKind.MapAvailability => "Map id",
        EventEffectKind.BossSpawnMultiplier => "Boss",
        EventEffectKind.QuestAvailabilityWindow => "Quest id",
        _ => string.Empty,
    };

    public bool HasMultiplier => Kind is EventEffectKind.TraderPriceMultiplier or EventEffectKind.BossSpawnMultiplier;

    public bool HasOpenToggle => Kind is EventEffectKind.FleaAvailability or EventEffectKind.MapAvailability;

    public bool HasWindow => Kind == EventEffectKind.QuestAvailabilityWindow;

    public bool IsUnknown => Kind == EventEffectKind.Unknown;

    public string UnknownText => _loaded.UnknownJson ?? string.Empty;

    public string RowName => $"{KindLabel} {_owner.Rows.IndexOf(this) + 1}";

    public EventTargetChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (SetProperty(ref _selectedChoice, value) && value is not null)
            {
                _targetId = value.Id;
                _targetName = value.Name;
                _owner.Revalidate();
            }
        }
    }

    public string TargetText
    {
        get => _targetText;
        set
        {
            if (!SetProperty(ref _targetText, value))
            {
                return;
            }

            // A typed boss or quest is its own name; a typed trader or map id is only an id.
            _targetId = value.Trim();
            _targetName = Kind is EventEffectKind.BossSpawnMultiplier or EventEffectKind.QuestAvailabilityWindow
                ? value.Trim()
                : string.Empty;
            _owner.Revalidate();
        }
    }

    public string Multiplier
    {
        get => _multiplier;
        set
        {
            if (SetProperty(ref _multiplier, value))
            {
                _owner.Revalidate();
            }
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                _owner.Revalidate();
            }
        }
    }

    public string StartText
    {
        get => _startText;
        set
        {
            if (SetProperty(ref _startText, value))
            {
                _owner.Revalidate();
            }
        }
    }

    public string EndText
    {
        get => _endText;
        set
        {
            if (SetProperty(ref _endText, value))
            {
                _owner.Revalidate();
            }
        }
    }

    public string TargetError
    {
        get => _targetError;
        private set
        {
            if (SetProperty(ref _targetError, value))
            {
                OnPropertyChanged(nameof(HasTargetError));
            }
        }
    }

    public bool HasTargetError => TargetError.Length > 0;

    public string ValueError
    {
        get => _valueError;
        private set
        {
            if (SetProperty(ref _valueError, value))
            {
                OnPropertyChanged(nameof(HasValueError));
            }
        }
    }

    public bool HasValueError => ValueError.Length > 0;

    public string RowError
    {
        get => _rowError;
        private set
        {
            if (SetProperty(ref _rowError, value))
            {
                OnPropertyChanged(nameof(HasRowError));
            }
        }
    }

    public bool HasRowError => RowError.Length > 0;

    public ICommand RemoveCommand { get; }

    internal EventEffectDraft ToDraft()
    {
        var hasStart = EventsPageViewModel.TryReadDate(StartText, out var start);
        var hasEnd = EventsPageViewModel.TryReadDate(EndText, out var end);
        return _loaded with
        {
            TargetId = _targetId,
            TargetName = _targetName,
            Multiplier = Multiplier,
            Enabled = IsOpen,
            StartUtc = hasStart ? start : null,
            StartRaw = hasStart ? null : StartText,
            EndUtc = hasEnd ? end : null,
            EndRaw = hasEnd ? null : EndText,
        };
    }

    /// <summary>Puts the parser's issues for this row next to the field they name.</summary>
    /// <remarks>
    /// The parser speaks in JSON paths and property names, which is right for a file and wrong
    /// under a text box. Whether there is an issue is still the parser's call; only the words
    /// are this row's.
    /// </remarks>
    internal void ShowIssues(IReadOnlyList<(string Field, string Message)> issues)
    {
        string target = string.Empty, value = string.Empty, row = string.Empty;
        foreach (var (field, message) in issues)
        {
            switch (field)
            {
                case "traderId" or "mapId" or "bossId" or "questId":
                    target = message.Contains("exceed", StringComparison.Ordinal) ? "Too long" : Kind switch
                    {
                        EventEffectKind.TraderPriceMultiplier => "Pick a trader",
                        EventEffectKind.MapAvailability => "Pick a map",
                        EventEffectKind.BossSpawnMultiplier => "Name the boss",
                        _ => "Name the quest",
                    };
                    break;
                case "multiplier":
                    value = "Above 0, at most 100";
                    break;
                case "startUtc":
                    value = "First day is not a date";
                    break;
                case "endUtc":
                    value = "Last day is not a date";
                    break;
                case "type":
                    row = "Unknown type · remove it to save";
                    break;
                case "":
                    row = message.Contains("before", StringComparison.Ordinal)
                        ? "Last day is before the first"
                        : message.Contains("requires", StringComparison.Ordinal)
                            ? "Give a first or last day"
                            : message;
                    break;
                default:
                    row = message.Contains("exceed", StringComparison.Ordinal) ? "A name is too long" : message;
                    break;
            }
        }

        TargetError = target;
        ValueError = value;
        RowError = row;
    }
}

/// <summary>
/// The typed effects of one event, edited as rows and checked by <see cref="EventRuleParser"/>
/// on every keystroke (#288).
/// </summary>
/// <remarks>
/// #765 made rules typed, validated and previewed, and left writing them to a text editor: the
/// rule half of this issue was a JSON string only somebody who had read the README could change.
/// The rows write the same file format, so a hand-edited file still opens here and an event saved
/// here can still be edited by hand.
/// </remarks>
public sealed class EventRuleEditorViewModel : BindableViewModel
{
    private IReadOnlyList<EventTargetChoice> _traders = [];
    private IReadOnlyList<EventTargetChoice> _maps = [];
    private string? _original;
    private EventRuleDraftResult _result = EventRuleDrafts.Validate(null, []);
    private string _preview = string.Empty;
    private string _status = string.Empty;
    private string _generalError = string.Empty;
    private bool _isDirty;
    private bool _loading;

    public EventRuleEditorViewModel()
    {
        AddTraderCommand = new DelegateCommand(() => Add(new(EventEffectKind.TraderPriceMultiplier, Multiplier: "1")));
        AddFleaCommand = new DelegateCommand(() => Add(new(EventEffectKind.FleaAvailability)));
        AddMapCommand = new DelegateCommand(() => Add(new(EventEffectKind.MapAvailability)));
        AddBossCommand = new DelegateCommand(() => Add(new(EventEffectKind.BossSpawnMultiplier, Multiplier: "2")));
        AddQuestCommand = new DelegateCommand(() => Add(new(EventEffectKind.QuestAvailabilityWindow)));
    }

    /// <summary>Raised after every change, so the page can refresh what depends on the rules.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<EventEffectRowViewModel> Rows { get; } = [];

    public ICommand AddTraderCommand { get; }

    public ICommand AddFleaCommand { get; }

    public ICommand AddMapCommand { get; }

    public ICommand AddBossCommand { get; }

    public ICommand AddQuestCommand { get; }

    public EventRuleDraftResult Result => _result;

    public bool IsValid => _result.IsValid;

    /// <summary>Whether the rows differ from what was loaded; an untouched event keeps its text.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => Rows.Count == 0;

    /// <summary>"While active: ..." for the rules as typed, or empty while they do not validate.</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>An issue that belongs to no row, such as stored text that was never JSON.</summary>
    public string GeneralError
    {
        get => _generalError;
        private set
        {
            if (SetProperty(ref _generalError, value))
            {
                OnPropertyChanged(nameof(HasGeneralError));
            }
        }
    }

    public bool HasGeneralError => GeneralError.Length > 0;

    public void UseChoices(IReadOnlyList<EventTargetChoice> traders, IReadOnlyList<EventTargetChoice> maps)
    {
        _traders = traders;
        _maps = maps;
        if (!IsDirty)
        {
            Load(_original);
        }
    }

    /// <summary>Replaces the rows with the rules an event has stored.</summary>
    public void Load(string? rulesJson)
    {
        _loading = true;
        try
        {
            _original = rulesJson;
            Rows.Clear();
            var drafts = EventRuleDrafts.Read(rulesJson);
            foreach (var draft in drafts ?? [])
            {
                Rows.Add(new(this, draft, ChoicesFor(draft.Kind)));
            }

            IsDirty = false;
            Revalidate();
            if (drafts is null)
            {
                // The rows start empty and the parser's own words say what was wrong with the file.
                GeneralError = "Stored rules did not read · " +
                    string.Join(" · ", EventRuleParser.Parse(rulesJson).Issues.Take(2)) +
                    " · saving replaces them";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public void Clear() => Load(null);

    internal void Remove(EventEffectRowViewModel row)
    {
        if (Rows.Remove(row))
        {
            IsDirty = true;
            Revalidate();
        }
    }

    private void Add(EventEffectDraft draft)
    {
        Rows.Add(new(this, draft, ChoicesFor(draft.Kind)));
        IsDirty = true;
        Revalidate();
    }

    private IReadOnlyList<EventTargetChoice> ChoicesFor(EventEffectKind kind) => kind switch
    {
        EventEffectKind.TraderPriceMultiplier => _traders,
        EventEffectKind.MapAvailability => _maps,
        _ => [],
    };

    internal void Revalidate()
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        _result = EventRuleDrafts.Validate(_original, [.. Rows.Select(row => row.ToDraft())]);
        for (var index = 0; index < Rows.Count; index++)
        {
            Rows[index].ShowIssues(_result.IssuesFor(index));
        }

        GeneralError = string.Join(" · ", _result.GeneralIssues.Select(issue => issue.Message));
        var effects = _result.Parsed.Rules.Effects.Count;
        if (!_result.IsValid)
        {
            Preview = string.Empty;
            var issues = _result.Parsed.Issues.Count;
            Status = issues == 1 ? "1 issue · fix the marked field" : $"{issues} issues · fix the marked fields";
        }
        else
        {
            Preview = effects > 0
                ? $"While active: {EventRuleText.Preview(_result.Parsed.Rules)}."
                : "While active: no typed effects.";
            Status = effects == 1 ? "1 effect validated" : $"{effects} effects validated";
        }

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasNoRows));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
