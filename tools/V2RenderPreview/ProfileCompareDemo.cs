using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#269] "--profiles-compare-demo" (with "--profiles-demo"): two profiles with their own progress,
/// owned keys and ammo from the seeded catalog, and raids, so Setup's compare shows real counts.
/// "--team-mode-demo": the Team page's one-line warning for a squadmate on another game mode.
/// </summary>
internal static class ProfileCompareDemo
{
    /// <summary>Before any other profile exists: the first profile's progress, in its own file.</summary>
    public static async Task SeedFirstAsync(IServiceProvider services)
    {
        var players = services.GetRequiredService<IPlayerProfileService>();
        var (keys, ammo) = await ItemsAsync(services);
        var profile = await players.GetActiveAsync(CancellationToken.None);
        await players.SaveAsync(profile with
        {
            Level = 42,
            CompletedTaskIds = Ids("pvp-task", 118),
            HideoutStationLevels = new Dictionary<string, int> { ["lavatory"] = 3, ["medstation"] = 3, ["workbench"] = 3, ["intel"] = 2, ["gym"] = 1 },
            OwnedItemCounts = Owned(keys.Take(9), 1, ammo.Take(3), 240),
        }, CancellationToken.None);
        await RaidsAsync(services, profile.Id, "Regular", survived: 31, died: 17);
    }

    /// <summary>After the demo made the PvE profile active: its own, smaller progress.</summary>
    public static async Task SeedActiveAndCompareAsync(IServiceProvider services, SetupProfilesViewModel profiles)
    {
        var players = services.GetRequiredService<IPlayerProfileService>();
        var (keys, ammo) = await ItemsAsync(services);
        var profile = await players.GetActiveAsync(CancellationToken.None);
        await players.SaveAsync(profile with
        {
            Level = 19,
            CompletedTaskIds = Ids("pve-task", 46),
            HideoutStationLevels = new Dictionary<string, int> { ["lavatory"] = 2, ["medstation"] = 1, ["workbench"] = 2 },
            OwnedItemCounts = Owned(keys.Take(3), 1, ammo.Take(2), 120),
        }, CancellationToken.None);
        await RaidsAsync(services, profile.Id, "Pve", survived: 12, died: 3);
        var management = services.GetRequiredService<ProfileManagementService>();
        await profiles.Compare!.SetProfilesAsync(management.Current.Workspace);
        Console.WriteLine("Profile compare demo: " + string.Join(" | ", profiles.Compare.Rows.Select(row => $"{row.Label}: {row.Left} / {row.Right}")));
    }

    /// <summary>The Team page after a PvE squadmate joined this PvP player's group.</summary>
    public static void ShowTeamModeWarning(IServiceProvider services, GroupSnapshot squad)
    {
        var store = services.GetRequiredService<IRuntimeStateStore>();
        var members = squad.Members.Select((member, index) => member with { GameMode = index == 0 ? GroupModeCheck.Pve : GroupModeCheck.Pvp }).ToArray();
        var group = squad with { Members = GroupModeCheck.Separate(GroupModeCheck.Pvp, members), MyGameMode = GroupModeCheck.Pvp };
        store.Update(snapshot => snapshot with { Group = group });
        services.GetRequiredService<TeamWorkspaceViewModel>().Apply(store.Current);
        Console.WriteLine("Team mode demo: " + services.GetRequiredService<TeamWorkspaceViewModel>().ModeWarning);
    }

    private static async Task<(string[] Keys, string[] Ammo)> ItemsAsync(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IItemFactCatalog>();
        var keys = (await catalog.GetKeyFactsAsync(CancellationToken.None)).Select(key => key.ItemId).Order(StringComparer.Ordinal).ToArray();
        var ammo = (await catalog.GetAmmoAsync(CancellationToken.None)).Select(round => round.ItemId).Order(StringComparer.Ordinal).ToArray();
        return (keys, ammo);
    }

    private static HashSet<string> Ids(string prefix, int count) =>
        [.. Enumerable.Range(1, count).Select(index => $"{prefix}-{index}")];

    private static Dictionary<string, int> Owned(IEnumerable<string> keys, int perKey, IEnumerable<string> rounds, int perRound)
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            owned[key] = perKey;
        }

        foreach (var round in rounds)
        {
            owned[round] = perRound;
        }

        return owned;
    }

    private static async Task RaidsAsync(IServiceProvider services, Guid profileId, string mode, int survived, int died)
    {
        // The store itself, as the Debrief demo does: the outbox only takes typed game commands.
        IRaidHistoryService history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
        var start = DateTimeOffset.UtcNow.AddDays(-20);
        for (var index = 0; index < survived + died; index++)
        {
            var started = start.AddHours(index * 3);
            var id = await history.StartAsync(new(Guid.NewGuid(), profileId, "customs", mode, started, null, null, null), CancellationToken.None);
            await history.EndAsync(id, started.AddMinutes(25), index < survived ? "Survived" : "Killed", null, CancellationToken.None);
        }
    }
}
