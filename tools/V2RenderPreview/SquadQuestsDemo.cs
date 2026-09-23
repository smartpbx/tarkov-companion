using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#780] "--squad-quests": the demo squad's members share real catalog quests on the selected map,
/// by id, exactly as the relay would deliver them, so Team and Raid render the resolved picture.
/// </summary>
internal static class SquadQuestsDemo
{
    public static async Task ApplyAsync(IServiceProvider services, MapViewModel map, GroupSnapshot squad) =>
        // Shared with the Windows page gallery's squad scene (#279), so both show the same squad.
        Console.WriteLine("Squad quests demo: " +
            await TarkovCompanion.App.Services.Diagnostics.GallerySquad.ShareQuestsAsync(services, map, squad, CancellationToken.None));

    public static void ShowOnRaid(IServiceProvider services, bool routeSquad)
    {
        var raid = services.GetRequiredService<RaidCockpitViewModel>();
        raid.ShowSquadObjectives = true;
        raid.RouteSquadStops = routeSquad;
    }
}
