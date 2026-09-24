using System.Globalization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One map in the "For …" picker.</summary>
public sealed record LoadoutSuggestionMapOption(string MapId, string Name, int ObjectiveCount)
{
    public string Label => string.Create(CultureInfo.CurrentCulture, $"{Name} · {ObjectiveCount}");
}

/// <summary>One loadout change, why, and for which quest.</summary>
public sealed class LoadoutSuggestionRowViewModel(LoadoutSuggestionRow row)
{
    public string Kind => row.Suggestion.Kind switch
    {
        LoadoutSuggestionKind.Key => "Key",
        LoadoutSuggestionKind.Weapon => "Weapon",
        LoadoutSuggestionKind.WeaponMod => "Mod",
        LoadoutSuggestionKind.Wear => "Wear",
        LoadoutSuggestionKind.LeaveBehind => "Leave",
        LoadoutSuggestionKind.Range => "Range",
        LoadoutSuggestionKind.Carry => "Carry",
        _ => "Room",
    };

    public string Title => row.Suggestion.Title;

    /// <summary>The quest it serves first, then the objective's own words.</summary>
    public string Quest => row.Suggestion.Quests.Count switch
    {
        1 => row.Suggestion.Quests[0],
        var count => string.Create(CultureInfo.CurrentCulture, $"{row.Suggestion.Quests[0]} +{count - 1}"),
    } + (row.Suggestion.AnyMap ? " · any map" : string.Empty);

    public string Reason => row.Suggestion.Reason;

    public string QuestsTip => string.Join(", ", row.Suggestion.Quests);

    /// <summary>"You own it · Mechanic 20,000 ₽", as much of that as is known.</summary>
    public string Availability => string.Join(" · ", new[] { row.Have, row.Source }.Where(part => part.Length > 0));

    public bool HasAvailability => Availability.Length > 0;

    public bool IsOwned => row.IsOwned;

    /// <summary>A need the player can neither use from the stash nor buy now reads as a warning.</summary>
    public bool IsBlocked => HasAvailability && !row.IsOwned && !row.IsObtainable;
}

/// <summary>
/// Plan › Loadout's "For Customs today" card (#307): what the chosen map's active quests ask the
/// kit for, each with its reason and the quest it serves.
/// </summary>
public sealed class LoadoutSuggestionsViewModel : BindableViewModel
{
    private readonly LoadoutSuggestionService _service;
    private IReadOnlyList<LoadoutSuggestionMapOption> _maps = [];
    private LoadoutSuggestionMapOption? _selectedMap;
    private IReadOnlyList<LoadoutSuggestionRowViewModel> _rows = [];
    private string _status = "Reading your active quests…";
    private bool _applying;
    private int _generation;

    public LoadoutSuggestionsViewModel(LoadoutSuggestionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public IReadOnlyList<LoadoutSuggestionMapOption> Maps
    {
        get => _maps;
        private set
        {
            SetProperty(ref _maps, value);
            OnPropertyChanged(nameof(HasMaps));
        }
    }

    public bool HasMaps => Maps.Count > 0;

    public LoadoutSuggestionMapOption? SelectedMap
    {
        get => _selectedMap;
        set
        {
            // A combo box writes null when its items are replaced; that is not a choice.
            if (value is null || !SetProperty(ref _selectedMap, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Heading));
            if (!_applying)
            {
                RefreshAsync(CancellationToken.None).Observe("loadout", "suggestions");
            }
        }
    }

    public string Heading => SelectedMap is { } map ? $"For {map.Name} today" : "For your next raid";

    public IReadOnlyList<LoadoutSuggestionRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            SetProperty(ref _rows, value);
            OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => Rows.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string RulesLabel => PlannerVersions.Label(PlannerVersions.LoadoutSuggestions);

    /// <summary>Chooses a map by name or id, then plans for it; the render tool's way in.</summary>
    public async Task SelectMapAsync(string nameOrId, CancellationToken cancellationToken)
    {
        if (Maps.Count == 0)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }

        var map = Maps.FirstOrDefault(option =>
            string.Equals(option.MapId, nameOrId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        if (map is not null)
        {
            _applying = true;
            try
            {
                SelectedMap = map;
            }
            finally
            {
                _applying = false;
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        try
        {
            var mapId = SelectedMap?.MapId;
            var plan = await OffInterfaceThread.Run(() => _service.PlanAsync(mapId, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _applying = true;
            try
            {
                Maps = plan.Maps.Select(map => new LoadoutSuggestionMapOption(map.MapId, map.Name, map.ObjectiveCount)).ToArray();
                SelectedMap = Maps.FirstOrDefault(map => string.Equals(map.MapId, plan.MapId, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _applying = false;
            }

            Rows = plan.Rows.Select(row => new LoadoutSuggestionRowViewModel(row)).ToArray();
            Status = plan.UnavailableReason
                ?? (Maps.Count == 0
                    ? "No active quest names a map. Mark quests active in Plan."
                    : Rows.Count == 0
                        ? "Nothing on this map asks for gear."
                        : string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} changes from your active quests"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (generation == _generation)
            {
                Rows = [];
                Status = "Suggestions unavailable · " + exception.Message;
            }
        }
    }
}
