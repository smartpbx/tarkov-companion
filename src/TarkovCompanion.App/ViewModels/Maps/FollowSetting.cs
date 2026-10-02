using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>Whether a new screenshot should move the raid map back to the player, per map.</summary>
/// <remarks>
/// [#992] Follow turned on for a map stays on there, raid after raid and across restarts, until
/// the player turns it off. Reported three times. The choice used to be one
/// value for every map, and a drag on the plan saved it as off: so one look around in one raid
/// turned Follow off for every later raid on every map. Now only the Follow control writes it,
/// for the map it is pressed on; a drag pauses following (MapViewModel.IsFollowPaused) and
/// saves nothing.
///
/// Stored under the old key as <c>woods:on,customs:off</c>. The old single value (<c>on</c> or
/// <c>off</c>) is read as the answer for maps with no entry of their own, so a player who had it
/// on everywhere still has; with nothing stored a map starts with Follow off.
/// </remarks>
internal sealed class FollowSetting
{
    /// <summary>The answer for a map that has no entry and no old single value to fall back on.</summary>
    public const bool Default = false;

    private const string Fallback = "*";

    private readonly IWorkspaceLayoutStore? _store;
    private Dictionary<string, bool> _maps = new(StringComparer.OrdinalIgnoreCase);

    public FollowSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Reload();
    }

    /// <summary>The choice for one map; <see langword="null"/> asks for maps with no entry of their own.</summary>
    public bool For(string? mapId) =>
        mapId is { Length: > 0 } && _maps.TryGetValue(mapId, out var value) ? value
        : _maps.TryGetValue(Fallback, out var fallback) ? fallback
        : Default;

    /// <summary>[#902] Reads the stored choices again, after Backup &amp; reset replaced them.</summary>
    public void Reload() => _maps = Parse(_store?.Get(WorkspaceLayoutKeys.RaidFollow));

    /// <summary>Saves the choice for one map, the only thing that changes what is stored.</summary>
    public void Set(string? mapId, bool value)
    {
        _maps[mapId is { Length: > 0 } ? mapId : Fallback] = value;
        _store?.Set(WorkspaceLayoutKeys.RaidFollow, Format(_maps));
    }

    internal static Dictionary<string, bool> Parse(string? stored)
    {
        var maps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.LastIndexOf(':');
            var (map, value) = colon < 0 ? (Fallback, part) : (part[..colon].Trim(), part[(colon + 1)..].Trim());
            if (map.Length > 0 && ParseValue(value) is { } on)
            {
                maps[map] = on;
            }
        }

        return maps;
    }

    private static bool? ParseValue(string value) =>
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
        : value.Equals("off", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    private static string Format(Dictionary<string, bool> maps) => string.Join(
        ',',
        maps.OrderBy(entry => entry.Key == Fallback ? 0 : 1).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Key}:{(entry.Value ? "on" : "off")}"));
}
