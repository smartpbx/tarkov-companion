using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#902] The maps an objective route from Plan was last opened on, kept across a restart.</summary>
/// <remarks>
/// "Open in Raid" used to put the route on the Raid map for this session only: after a restart the
/// map came back without it, and the only way back was Plan again. Whether it is drawn is a
/// separate choice, the Objective route layer; this only remembers that there is one to draw.
/// Only map ids are stored: the stops follow Plan, and until Plan hands them over again the Raid
/// map takes them from its own objective pins.
/// </remarks>
internal sealed class ObjectiveRouteMaps
{
    /// <summary>More maps than the game has; the oldest is forgotten first.</summary>
    private const int MaximumMaps = 16;

    private const int MaximumValueLength = 256;

    private readonly IWorkspaceLayoutStore? _store;
    private readonly List<string> _maps;

    public ObjectiveRouteMaps(IWorkspaceLayoutStore? store)
    {
        _store = store;
        _maps = Parse(store?.Get(WorkspaceLayoutKeys.RaidObjectiveRouteMaps));
    }

    public bool Contains(string locationId) =>
        _maps.Contains(locationId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers a map a route was opened on, as the most recent one.</summary>
    public void Add(string locationId)
    {
        if (!IsStorable(locationId) ||
            (_maps.Count > 0 && string.Equals(_maps[^1], locationId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _maps.RemoveAll(map => string.Equals(map, locationId, StringComparison.OrdinalIgnoreCase));
        _maps.Add(locationId);
        while (_maps.Count > MaximumMaps || (string.Join(',', _maps).Length > MaximumValueLength && _maps.Count > 1))
        {
            _maps.RemoveAt(0);
        }

        _store?.Set(WorkspaceLayoutKeys.RaidObjectiveRouteMaps, string.Join(',', _maps));
    }

    internal static List<string> Parse(string? stored)
    {
        var maps = new List<string>();
        foreach (var entry in (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsStorable(entry) && !maps.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                maps.Add(entry);
            }
        }

        return maps;
    }

    private static bool IsStorable(string id) =>
        id.Length is > 0 and <= 64 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
