using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#712 T7] "--squad-plan-demo" (after "--squad-quests"): the demo squad overlaps on more of the
/// quests it already shares, with the same objectives open, and each member's companion shares a
/// Loadout check and a level, so Team's shared-task planner and ready check have something to show.
/// </summary>
internal static class SquadPlanDemo
{
    public static void Apply(IServiceProvider services, string[] args, Action<int> pump)
    {
        if (!args.Contains("--squad-plan-demo"))
        {
            return;
        }

        var store = services.GetRequiredService<IRuntimeStateStore>();
        var members = store.Current.Group.Members.ToArray();
        var quests = members.SelectMany(member => member.QuestIds).Distinct(StringComparer.Ordinal).ToArray();
        var objectives = members.SelectMany(member => member.Objectives)
            .DistinctBy(objective => objective.ObjectiveId, StringComparer.Ordinal)
            .ToArray();
        if (quests.Length < 2 || members.Length < 3)
        {
            Console.WriteLine($"Squad plan demo: needs --squad-quests first ({quests.Length} quests, {members.Length} members)");
            return;
        }

        // Every quest is held by at least two, and the first two are held by all three.
        string[][] holds =
        [
            [.. quests],
            [.. quests.Take(2), .. quests.Skip(2).Where((_, index) => index % 2 == 0)],
            [.. quests.Take(2), .. quests.Skip(2).Where((_, index) => index % 2 == 1)],
        ];
        GroupLoadoutCheckView Check(params LoadoutCheckItem[] items) => new(null, items, TimeSpan.FromMinutes(3));
        GroupLoadoutCheckView[] checks =
        [
            Check(new(LoadoutCheckKinds.Keys, true), new(LoadoutCheckKinds.Items, true)),
            Check(new(LoadoutCheckKinds.Keys, true), new(LoadoutCheckKinds.Items, false, "MS2000 Marker")),
            Check(new(LoadoutCheckKinds.Keys, true), new(LoadoutCheckKinds.Items, null)),
        ];
        int[] levels = [42, 37, 29];
        for (var index = 0; index < members.Length && index < 3; index++)
        {
            var held = holds[index].ToHashSet(StringComparer.Ordinal);
            members[index] = members[index] with
            {
                QuestIds = [.. held],
                Objectives = [.. objectives.Where(objective => held.Contains(objective.TaskId))],
                LoadoutCheck = index == 2 ? null : checks[index],
                Level = index == 2 ? null : levels[index],
            };
        }

        var team = services.GetRequiredService<TeamWorkspaceViewModel>();
        for (var i = 0; i < 6; i++)
        {
            store.Update(snapshot => snapshot with { Group = snapshot.Group with { Members = members } });
            team.Apply(store.Current);
            pump(20);
        }

        Console.WriteLine($"Squad plan demo: plan '{team.PlanTitle}' rows={team.SharedQuestRows.Count} ready rows={team.ReadyRows.Count} summary='{team.ReadyCheckSummary}'");

        // "--squad-plan-to-raid": press "Put on the Raid map", as a player would.
        if (args.Contains("--squad-plan-to-raid"))
        {
            team.PutPlanOnRaidCommand.Execute(null);
            pump(200);
            var raid = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel>();
            Console.WriteLine($"Squad plan to raid: route={raid.HasObjectiveRoute} squad objectives={raid.ShowSquadObjectives} here={raid.HasSquadObjectivesHere} route squad={raid.RouteSquadStops}");
        }
    }
}
