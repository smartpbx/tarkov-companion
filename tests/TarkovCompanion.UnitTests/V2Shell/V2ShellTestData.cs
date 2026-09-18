using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Shell;

internal static class V2ShellTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every V1 page a V2 route still hosts as <c>LegacyPage</c>. "Raid", "Scanner", "History",
    /// "Quests", "Hideout", "Settings", "Squad", and "Group" are deliberately absent: the Raid
    /// route now hosts the raid cockpit (<c>V2RouteContent.RaidCockpit</c>, package 2) instead of
    /// a passthrough to V1's "Raid" page, the Loot route now hosts the real <c>LootScanView</c>
    /// instead of Scanner (package 1, #282), the Debrief route now hosts the real
    /// <c>DebriefWorkspaceView</c> instead of History (package 3, #291), the Plan and Hideout
    /// routes now host the real Plan workspace and its Hideout section instead (package 10, #288),
    /// Setup hosts the real <c>V2SetupWorkspaceView</c> instead of Settings (package 6, #292), and
    /// the Team and Group routes both host the real Team workspace
    /// (<c>V2RouteContent.Workspace</c>, package 9) instead of a passthrough to either V1 page.
    /// Ammo, Keys and Flea now host native Intel workspaces over the V1 pages' view models
    /// (package 28). All eight V1 pages remain reachable from V1 navigation; none of them is a V2
    /// LegacyPage route any more except the two Plan sections below.
    /// </summary>
    public static readonly string[] V1Destinations =
    [
        "Events", "Loadout",
    ];

    /// <summary>A snapshot as the runtime store starts one, so tests change only what they mean to.</summary>
    public static ApplicationRuntimeSnapshot Snapshot(bool demo = false, bool offline = false) =>
        new RuntimeStateStore(new RuntimeOptions(demo, offline, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)))
            .Current;

    public static ApplicationRuntimeSnapshot WithData(
        this ApplicationRuntimeSnapshot snapshot,
        DataAvailability availability,
        int items,
        DateTimeOffset? updatedUtc,
        bool databaseReady = true) =>
        snapshot with
        {
            DatabaseReady = databaseReady,
            Data = new RuntimeDataState(availability, items, items > 0 ? 7 : 0, updatedUtc, $"{availability} fixture data"),
        };

    public static ApplicationRuntimeSnapshot Observing(this ApplicationRuntimeSnapshot snapshot) =>
        snapshot with
        {
            Observation = new EftObservationState(true, true, true, "logs", "screenshots", Confidence.Unknown, "Watching both folders"),
            Scan = ScanExecutionResult.Ready("Scanner ready", Now),
        };

    public static string RepositoryPath(params string[] segments)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
                {
                    return Path.Combine([directory.FullName, .. segments]);
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    public static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-v2shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
