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

    private readonly IWorkspaceLayoutStore? _store;
    private readonly List<(string Id, bool IsVisible)> _choices;

    public MapLayerVisibilitySetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        _choices = Parse(store?.Get(WorkspaceLayoutKeys.RaidLayerVisibility));
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
            if (IsStorable(id) && !choices.Any(choice => string.Equals(choice.Id, id, StringComparison.Ordinal)))
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
