using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[Issue 796] The Raid map's Layers-menu choices, remembered per layer id.</summary>
/// <remarks>
/// Before this, a layer's on/off lived only in the scene's view, and a new map (or a restart)
/// started from an empty view, so every layer fell back to its default: the traffic heatmap came
/// back on each time it was turned off. Global, not per map: every raid layer id means the same
/// thing on every map, and a player who hides the heatmap hides it everywhere. Only layers the
/// player has touched are stored, so an untouched one keeps its map default (Switches on for
/// Labs, Reserve and Interchange; the opening window's Spawns).
///
/// One key rather than one per layer: the layout file caps its entry count, and a key per layer
/// would spend a third of it. The value is capped too, so the most recent choices are the ones
/// kept when it is full.
/// </remarks>
internal sealed class MapLayerVisibilitySetting
{
    private const int MaximumValueLength = 256;

    /// <summary>
    /// [#902] The four V1 layers no map has any more. A player who switched one off before it
    /// was removed still has "companion-markers:0" stored, which would take a place in the
    /// capped value for good; they are dropped on read, and gone from the value at the next write.
    /// </summary>
    private static readonly IReadOnlySet<string> RetiredLayerIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "companion-markers",
        "routes",
        "risk-traffic",
        "filters",
    };

    /// <summary>
    /// [#902] Choices made before schema 2, through controls that could not show or undo them:
    /// the objective route's two disagreeing switches, the suggested routes' missing one, the View
    /// menu's "Visited" (now the My trail layer) and the single Spawns switch that is now All
    /// spawns beside its own Nearby spawns. Dropped once, so each is back at its default.
    /// </summary>
    internal static readonly IReadOnlySet<string> ResetAtSchema2 = new HashSet<string>(StringComparer.Ordinal)
    {
        "objective-route",
        "traffic-routes",
        "visited",
        "spawns",
    };

    internal const string CurrentSchema = "2";

    private readonly IWorkspaceLayoutStore? _store;
    private readonly List<(string Id, bool IsVisible)> _choices = [];

    public MapLayerVisibilitySetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Reload();
    }

    /// <summary>Reads the stored choices again: after Backup &amp; reset or an import, and at startup.</summary>
    public void Reload()
    {
        _choices.Clear();
        var stored = _store?.Get(WorkspaceLayoutKeys.RaidLayerVisibility);
        _choices.AddRange(Parse(stored));
        if (_store is null || string.IsNullOrEmpty(stored) ||
            string.Equals(_store.Get(WorkspaceLayoutKeys.RaidLayerSchema), CurrentSchema, StringComparison.Ordinal))
        {
            return;
        }

        // Idempotent: a second run finds the schema written and nothing to drop.
        _choices.RemoveAll(choice => ResetAtSchema2.Contains(choice.Id));
        _store.Set(WorkspaceLayoutKeys.RaidLayerVisibility, Format(_choices));
        _store.Set(WorkspaceLayoutKeys.RaidLayerSchema, CurrentSchema);
    }

    /// <summary>The remembered choice for one layer, or null when the player never touched it.</summary>
    public bool? Get(MapSceneLayerId layerId)
    {
        foreach (var (id, isVisible) in _choices)
        {
            if (string.Equals(id, layerId.Value, StringComparison.Ordinal))
            {
                return isVisible;
            }
        }

        return null;
    }

    public void Set(MapSceneLayerId layerId, bool isVisible)
    {
        if (!IsStorable(layerId.Value) || Get(layerId) == isVisible)
        {
            return;
        }

        _choices.RemoveAll(choice => string.Equals(choice.Id, layerId.Value, StringComparison.Ordinal));
        _choices.Add((layerId.Value, isVisible));
        var value = Format(_choices);
        while (value.Length > MaximumValueLength && _choices.Count > 1)
        {
            _choices.RemoveAt(0);
            value = Format(_choices);
        }

        _store?.Set(WorkspaceLayoutKeys.RaidLayerVisibility, value);
        // Written with the first choice, so the next start does not take it for an old one.
        if (_store is not null && !string.Equals(_store.Get(WorkspaceLayoutKeys.RaidLayerSchema), CurrentSchema, StringComparison.Ordinal))
        {
            _store.Set(WorkspaceLayoutKeys.RaidLayerSchema, CurrentSchema);
        }
    }

    /// <summary>
    /// Remembers a Layers-menu change once the scene has taken it. A change Loot focus made is
    /// not the player's choice for that layer: Loot focus is undone by pressing it again, and
    /// saving its steps is what made the old gem preset hide every layer on every map for good.
    /// </summary>
    public void Record(MapSceneViewChange change, MapSceneViewChangeStatus status, bool isLootFocus)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!isLootFocus &&
            change.Kind == MapSceneViewChangeKind.SetLayerVisibility &&
            status == MapSceneViewChangeStatus.Applied &&
            change.LayerId is { } layerId &&
            change.IsVisible is { } isVisible)
        {
            Set(layerId, isVisible);
        }
    }

    /// <summary>
    /// Applies one view change the renderer asked for, shows it, and remembers it when it is the
    /// player's. This is the Raid map's whole handling of a layer switch, in one place, so the
    /// tests exercise the order the cockpit uses rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// [#933] "Turning on things like the rare loot spawns dont seem to persist, it turns off every
    /// raid still." Whose change this is has to be read before the scene is presented: presenting
    /// it is what sends Loot focus's next step, and the renderer had cleared its "this is Loot
    /// focus" flag by the time that nested step returned. Every step but the last was then saved
    /// as the player's own choice, so one press of Loot focus switched Spawns, Keys, Quests,
    /// Traffic and the rest off for good, on every map and after a restart.
    /// </remarks>
    public MapSceneViewChangeResult ApplyChange(MapSceneRendererViewModel renderer, MapSceneViewChange change)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(change);
        var isLootFocus = renderer.IsDispatchingLootFocus;
        var result = MapSceneViewReducer.Apply(renderer.Scene, change);
        if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
        {
            renderer.Present(result.Scene);
        }

        Record(change, result.Status, isLootFocus);
        return result;
    }

    /// <summary>
    /// The layer states one scene build asks for: the view's own, with every remembered choice laid
    /// over them. [#902] While Loot focus is on, the layers it hid stay hidden through each
    /// rebuild: only a layer the view does not have yet takes its remembered choice.
    /// </summary>
    public IReadOnlyList<MapSceneLayerState> Compose(IReadOnlyList<MapSceneLayerState> current, bool isLootFocused)
    {
        ArgumentNullException.ThrowIfNull(current);
        var remembered = Apply(current);
        if (!isLootFocused)
        {
            return remembered;
        }

        var held = current.Select(state => state.LayerId).ToHashSet();
        return [.. current, .. remembered.Where(state => !held.Contains(state.LayerId))];
    }

    /// <summary>
    /// The requested layer states with every remembered choice laid over them. A stored layer the
    /// scene does not have is harmless: the assembler reads states only for layers it builds.
    /// </summary>
    public IReadOnlyList<MapSceneLayerState> Apply(IReadOnlyList<MapSceneLayerState> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (_choices.Count == 0)
        {
            return requested;
        }

        var stored = _choices.Select(choice => choice.Id).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. requested.Where(state => !stored.Contains(state.LayerId.Value)),
            .. _choices.Select(choice => new MapSceneLayerState(new(choice.Id), choice.IsVisible)),
        ];
    }

    internal static List<(string Id, bool IsVisible)> Parse(string? stored)
    {
        var choices = new List<(string Id, bool IsVisible)>();
        if (string.IsNullOrEmpty(stored))
        {
            return choices;
        }

        foreach (var entry in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.LastIndexOf(':');
            if (colon <= 0 || entry[(colon + 1)..] is not ("1" or "0"))
            {
                continue;
            }

            var id = entry[..colon];
            if (IsStorable(id) && !RetiredLayerIds.Contains(id) && !choices.Any(choice => string.Equals(choice.Id, id, StringComparison.Ordinal)))
            {
                choices.Add((id, entry[(colon + 1)..] == "1"));
            }
        }

        return choices;
    }

    private static string Format(IEnumerable<(string Id, bool IsVisible)> choices) =>
        string.Join(',', choices.Select(choice => $"{choice.Id}:{(choice.IsVisible ? '1' : '0')}"));

    private static bool IsStorable(string id) =>
        id.Length is > 0 and <= 96 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
