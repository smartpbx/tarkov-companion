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
    public static async Task ApplyAsync(IServiceProvider services, MapViewModel map, GroupSnapshot squad)
    {
        var store = services.GetRequiredService<IRuntimeStateStore>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var language = services.GetRequiredService<QuestTrackingOptions>().NormalizedLanguage;
        var catalog = await services.GetRequiredService<IQuestCatalog>().GetAsync(profile.GameMode, language, CancellationToken.None);
        IReadOnlyCollection<string> mapIds = [];
        await map.ProjectOtherObjectivesAsync(ids => { mapIds = ids; return null; }, CancellationToken.None);
        if (catalog is null || mapIds.Count == 0)
        {
            Console.WriteLine("Squad quests demo: no catalog or map ids.");
            return;
        }

        // Quests with at least one objective at a single spot on this map, the ones a render shows.
        var onMap = catalog.Tasks
            .Where(task => task.Objectives.Any(objective => objective.Zones.Any(zone =>
                zone.Position is not null && zone.MapId is { } id && mapIds.Contains(id, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(task => task.Name, StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (onMap.Length < 4)
        {
            Console.WriteLine($"Squad quests demo: only {onMap.Length} quests on this map.");
            return;
        }

        GroupObjectiveView[] Open(QuestTaskDefinition task, int skip) =>
            [.. task.Objectives.Where(objective => objective.Optional != true).Skip(skip)
                .Select(objective => new GroupObjectiveView(task.Id, objective.Id, null))];
        var plan = new Dictionary<string, QuestTaskDefinition[]>(StringComparer.Ordinal)
        {
            ["Geo"] = [onMap[0], onMap[1]],
            ["Riley"] = [onMap[1], onMap[2]],
            ["Sam"] = [onMap[3]],
        };
        store.Update(snapshot => snapshot with
        {
            Group = squad with
            {
                Members = [.. squad.Members.Select(member => plan.TryGetValue(member.Name, out var tasks)
                    ? member with
                    {
                        QuestIds = [.. tasks.Select(task => task.Id)],
                        Objectives = [.. tasks.SelectMany((task, index) => Open(task, index))],
                    }
                    : member)],
            },
        });
        Console.WriteLine($"Squad quests demo: {string.Join(", ", plan.Select(pair => $"{pair.Key}={string.Join('+', pair.Value.Select(task => $"{task.Name}[{task.Id}]"))}"))}");
    }

    public static void ShowOnRaid(IServiceProvider services, bool routeSquad)
    {
        var raid = services.GetRequiredService<RaidCockpitViewModel>();
        raid.ShowSquadObjectives = true;
        raid.RouteSquadStops = routeSquad;
    }
}
