using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Shell;

internal static class V2ShellTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // "Raid" is deliberately absent: the V2 Raid route now hosts the raid cockpit
    // (V2RouteContent.RaidCockpit), not a passthrough to the V1 page of the same name. The V1
    // page remains reachable from V1 navigation; it is simply no longer a V2 LegacyPage route.
    public static readonly string[] V1Destinations =
    [
        "Squad", "Group", "Scanner", "Items", "Ammo", "Keys",
        "Flea", "Quests", "Hideout", "Events", "Loadout", "History", "Settings",
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
