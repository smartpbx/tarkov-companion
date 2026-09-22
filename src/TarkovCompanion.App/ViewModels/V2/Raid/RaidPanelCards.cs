using System.Windows.Input;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Whether one card of the Raid side panel is open, remembered per card.</summary>
/// <remarks>
/// A card the player opens or closes stays that way on the next launch. A card the app opens for
/// him — the loot filters when there is no loot data, Extract options when he picks an extract on
/// the map — opens without being remembered, so his own choice is what the next launch shows.
/// </remarks>
public sealed class RaidPanelCardViewModel : BindableViewModel
{
    private readonly Action<string, bool> _remember;
    private bool _isExpanded;

    internal RaidPanelCardViewModel(string id, bool isExpanded, Action<string, bool> remember)
    {
        Id = id;
        _isExpanded = isExpanded;
        _remember = remember;
        ToggleCommand = new DelegateCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>The card's key in the workspace layout store.</summary>
    public string Id { get; }

    /// <summary>Open. Set by the card's own header, so it is the player's choice and remembered.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsCollapsed));
                _remember(Id, value);
            }
        }
    }

    /// <summary>Closed: the header shows the card's one-line summary instead.</summary>
    public bool IsCollapsed => !_isExpanded;

    public ICommand ToggleCommand { get; }

    /// <summary>Opens the card for something the app wants seen, without remembering it.</summary>
    public void Reveal()
    {
        if (SetProperty(ref _isExpanded, true, nameof(IsExpanded)))
        {
            OnPropertyChanged(nameof(IsCollapsed));
        }
    }
}

/// <summary>
/// The Raid side panel's cards: which are open, remembered between launches.
/// </summary>
/// <remarks>
/// The panel had grown to a dozen cards, all open, so what a player needs mid-raid (squad,
/// objectives, extracts) sat under reference lists (every spawn area, where the others started,
/// every way out). The reference cards start closed and show one line; the rest start open.
/// </remarks>
public sealed class RaidPanelCards
{
    /// <summary>Cards that start closed until the player opens them.</summary>
    public static readonly IReadOnlySet<string> ClosedByDefault = new HashSet<string>(StringComparer.Ordinal)
    {
        "spawn-areas", "spawns", "ways-out", "loot-nearby", "loot-filters", "corrections",
    };

    private readonly IWorkspaceLayoutStore? _layout;

    public RaidPanelCards(IWorkspaceLayoutStore? layout)
    {
        _layout = layout;
        Summary = Card("summary");
        Squad = Card("squad");
        Objectives = Card("objectives");
        Extracts = Card("extracts");
        ExtractSelection = Card("extract-selection");
        Route = Card("route");
        Marks = Card("marks");
        GroupMarks = Card("group-marks");
        Tasks = Card("tasks");
        LootSelection = Card("loot-selection");
        SpawnAreas = Card("spawn-areas");
        Spawns = Card("spawns");
        WaysOut = Card("ways-out");
        LootNearby = Card("loot-nearby");
        LootFilters = Card("loot-filters");
        Corrections = Card("corrections");
    }

    public RaidPanelCardViewModel Summary { get; }

    public RaidPanelCardViewModel Squad { get; }

    public RaidPanelCardViewModel Objectives { get; }

    public RaidPanelCardViewModel Extracts { get; }

    public RaidPanelCardViewModel ExtractSelection { get; }

    public RaidPanelCardViewModel Route { get; }

    public RaidPanelCardViewModel Marks { get; }

    public RaidPanelCardViewModel GroupMarks { get; }

    public RaidPanelCardViewModel Tasks { get; }

    public RaidPanelCardViewModel LootSelection { get; }

    public RaidPanelCardViewModel SpawnAreas { get; }

    public RaidPanelCardViewModel Spawns { get; }

    public RaidPanelCardViewModel WaysOut { get; }

    public RaidPanelCardViewModel LootNearby { get; }

    public RaidPanelCardViewModel LootFilters { get; }

    public RaidPanelCardViewModel Corrections { get; }

    private RaidPanelCardViewModel Card(string id)
    {
        var stored = _layout?.Get(WorkspaceLayoutKeys.RaidCard(id));
        var isExpanded = stored switch
        {
            "open" => true,
            "closed" => false,
            _ => !ClosedByDefault.Contains(id),
        };
        return new(id, isExpanded, (key, open) => _layout?.Set(WorkspaceLayoutKeys.RaidCard(key), open ? "open" : "closed"));
    }
}
