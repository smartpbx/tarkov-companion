using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#289] "--team-status-demo": the ready check and a queued mark on the Team workspace.
/// </summary>
/// <remarks>
/// The squadmates' extract, note and ready state are laid on the demo group as the relay would
/// deliver them. This player's own status goes through the real <see cref="GroupSquadStatus"/>,
/// and the queued waypoint through the real store and forwarder: the render has no relay, so its
/// send fails exactly as it would with the relay down, and the mark is listed as queued.
/// </remarks>
internal static class TeamStatusDemo
{
    public static GroupSnapshot WithSquadStatus(GroupSnapshot group, string? extract)
    {
        var members = group.Members.ToArray();
        for (var index = 0; index < members.Length; index++)
        {
            members[index] = index switch
            {
                0 => members[index] with { Ready = true, PlannedExtract = extract, Note = "Meet at dorms, then out" },
                1 => members[index] with { Ready = false, Note = "Swapping ammo, 2 min" },
                _ => members[index],
            };
        }

        return group with { Members = members };
    }

    public static string? FirstExtract(IServiceProvider services) =>
        services.GetRequiredService<RaidCockpitViewModel>().MapExtracts.Select(row => row.Name).Order(StringComparer.CurrentCulture).FirstOrDefault();

    public static async Task PlaceQueuedAsync(IServiceProvider services, string? mapId, string? extract)
    {
        services.GetRequiredService<GroupSquadStatus>().Set(new SquadStatus(true, extract, mapId, "Covering the left side"));
        if (mapId is null || services.GetRequiredService<RaidCockpitViewModel>().Renderer is not { } renderer)
        {
            return;
        }

        var bounds = renderer.Scene.Bounds;
        await services.GetRequiredService<IRaidMarkStore>().PlaceAsync(
            mapId,
            null,
            bounds.MinimumX + bounds.Width * 0.45,
            bounds.MinimumY + bounds.Height * 0.55,
            "Regroup",
            RaidMarkScope.Squad,
            RaidMarkLifetime.UntilRemoved);
    }
}
