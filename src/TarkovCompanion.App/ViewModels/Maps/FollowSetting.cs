using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>Whether a new screenshot should move the raid map back to the player.</summary>
/// <remarks>
/// Follow is a player's workspace choice, not raid or map state. Keeping the setting beside the
/// shared map model means a map switch, a new raid and a new cockpit all read the same answer.
/// Only an explicit toggle or a manual camera move should update it.
/// </remarks>
internal sealed class FollowSetting
{
    public const bool Default = true;

    private readonly IWorkspaceLayoutStore? _store;

    public FollowSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Value = Parse(store?.Get(WorkspaceLayoutKeys.RaidFollow));
    }

    public bool Value { get; private set; }

    public bool Set(bool value)
    {
        Value = value;
        _store?.Set(WorkspaceLayoutKeys.RaidFollow, value ? "on" : "off");
        return Value;
    }

    internal static bool Parse(string? stored) => stored switch
    {
        "on" => true,
        "off" => false,
        _ => Default,
    };
}
