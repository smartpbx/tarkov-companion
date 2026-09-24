using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#269] --debrief-context-demo: raids Debrief must keep out of the active context. On top of the
/// coverage demo's raids: the wipe label changed a week ago (older raids become last wipe's),
/// three PvE raids on this profile, and two raids from another profile. --debrief-show-all turns
/// the "Show all" toggle on. Dev tool only.
/// </summary>
internal static class DebriefContextDemo
{
    public static void Seed(IServiceProvider services, Action<Task> drain) => drain(SeedAsync(services));

    public static void Show(DebriefWorkspaceViewModel debrief, string[] args)
    {
        if (args.Contains("--debrief-show-all"))
        {
            debrief.ShowAllContexts = true;
        }
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var store = services.GetRequiredService<IProfileWorkspaceStore>();
        var snapshot = await store.ReadAsync(CancellationToken.None);
        var active = snapshot.ActiveProfile;
        const string wipe = "Wipe 4";
        // The label changed seven days ago, as UpdateModeAndWipeAsync would have written it then.
        var changed = new ProfileRecord(
            new ProfileContext(active.Context.Identity, active.Context.Mode, new WipeSeason(wipe), active.Context.Locale, active.Context.DataSnapshot),
            active.Name,
            active.Progress,
            active.Lifecycle,
            now,
            ProfileWipeHistory.Record(active.ExtensionJson, active.Context.WipeSeason.Value, wipe, now.AddDays(-7)));
        await store.TryReplaceAsync(
            snapshot.Revision,
            new ProfileWorkspaceSnapshot(
                snapshot.Revision + 1,
                snapshot.ActiveProfileId,
                [.. snapshot.Profiles.Select(profile => profile.Context.Identity.ProfileId == active.Context.Identity.ProfileId ? changed : profile)]),
            CancellationToken.None);
        await services.GetRequiredService<IProfileRuntimeContextService>().RefreshAsync(CancellationToken.None);

        var history = services.GetRequiredService<SqliteRaidHistoryService>();
        var profileId = (await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None)).Id;
        var other = Guid.NewGuid();
        (Guid Profile, string Map, string Mode, int DaysAgo, string Outcome)[] raids =
        [
            (profileId, "shoreline", "Pve", 3, "Survived"),
            (profileId, "lighthouse", "Pve", 2, "Died"),
            (profileId, "customs", "Pve", 1, "Survived"),
            (other, "woods", "Regular", 4, "Survived"),
            (other, "factory", "Regular", 1, "Died"),
        ];
        foreach (var (profile, map, mode, daysAgo, outcome) in raids)
        {
            var started = now.AddDays(-daysAgo).AddHours(-3);
            var id = await history.StartAsync(new(Guid.NewGuid(), profile, map, mode, started, null, null, null), CancellationToken.None);
            await history.EndAsync(id, started.AddMinutes(31), outcome, null, CancellationToken.None);
        }
    }
}
