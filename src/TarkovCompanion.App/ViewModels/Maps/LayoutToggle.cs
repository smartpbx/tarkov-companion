using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>[#902] One remembered on/off in the workspace layout, stored as "on" or "off".</summary>
/// <remarks>
/// A missing or unreadable value is the default, and nothing is written until the player changes
/// it, so a later change to a default reaches everyone who never touched the switch. Read again
/// after Backup &amp; reset, which empties the store and raises its Replaced event.
/// </remarks>
internal sealed class LayoutToggle(IWorkspaceLayoutStore? store, string key, bool defaultValue)
{
    public bool Read() => Parse(store?.Get(key), defaultValue);

    public void Write(bool value) => store?.Set(key, value ? "on" : "off");

    internal static bool Parse(string? stored, bool defaultValue) => stored switch
    {
        "on" => true,
        "off" => false,
        _ => defaultValue,
    };
}
