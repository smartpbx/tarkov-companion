using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Shell;

internal static class V2ShellTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every V1 page a V2 route still hosts as <c>LegacyPage</c>. "Scanner" and "Settings" are
    /// deliberately absent: the Loot route hosts the real <c>LootScanView</c> instead of Scanner
    /// (package 1, #282), and Setup hosts the real <c>V2SetupWorkspaceView</c> instead of Settings
    /// (package 6, #292), so neither v1 page is reachable through the V2 shell any more.
    /// </summary>
    public static readonly string[] V1Destinations =
    [
        "Raid", "Squad", "Group", "Items", "Ammo", "Keys",
        "Flea", "Quests", "Hideout", "Events", "Loadout", "History",
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
