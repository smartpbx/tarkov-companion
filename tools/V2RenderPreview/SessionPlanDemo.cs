using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// #712 2-3: one quest with every objective recorded done, and one raid that ended twenty minutes
/// ago, so Plan's session strip has a raid played and its hand-in list has a quest. Fixture data,
/// written through the real services. Dev tool only.
/// </summary>
internal static class SessionPlanDemo
{
    /// <summary>--session-demo (with --seed-active-quests): seeds both, before Plan refreshes.</summary>
    public static void Seed(IServiceProvider services, Action<Task> drain) => drain(SeedAsync(services));

    private static async Task SeedAsync(IServiceProvider services)
    {
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var board = await services.GetRequiredService<IQuestReadService>().GetQuestBoardAsync(scope, CancellationToken.None);
        var commands = services.GetRequiredService<IQuestProgressCommandService>();
        var ready = board.Tasks.FirstOrDefault(task => task.RecordedState != RecordedTaskState.Active
            && task.Objectives.Count is > 0 and <= 3 && task.Objectives.All(objective => objective.IsOptional == false && !objective.IsUnsupported));
        if (ready is not null)
        {
            await commands.SetTaskStateAsync(scope, ready.TaskId, RecordedTaskState.Active, CancellationToken.None);
            foreach (var objective in ready.Objectives)
            {
                await commands.SetObjectiveProgressAsync(scope, objective.ObjectiveId, RecordedObjectiveState.Completed, objective.TargetCount, CancellationToken.None);
            }

            Console.WriteLine($"Session demo: {ready.Name} ready to hand in.");
        }

        var history = services.GetRequiredService<SqliteRaidHistoryService>();
        var started = DateTimeOffset.UtcNow.AddMinutes(-45);
        var id = await history.StartAsync(new(Guid.NewGuid(), profile.Id, "customs", "Regular", started, null, null, null), CancellationToken.None);
        await history.EndAsync(id, started.AddMinutes(25), "Survived", null, CancellationToken.None);
    }
}
