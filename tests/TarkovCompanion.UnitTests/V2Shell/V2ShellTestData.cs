using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Shell;

internal static class V2ShellTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The V1 shell's fourteen page names, read from V1's own navigation list.
    /// </summary>
    /// <remarks>
    /// [#294] Read rather than copied. A copied list agrees with itself for ever: the point is to
    /// notice a fifteenth V1 page, or a renamed one, that has no V2 home and therefore no way to
    /// be reached by name under the default shell.
    ///
    /// None of the fourteen is a V2 <c>LegacyPage</c> route any more — that field and that route
    /// content are gone. Raid hosts the raid cockpit (package 2, #286), Loot the real LootScanView
    /// (package 1, #282), Debrief the DebriefWorkspaceView (package 3, #291), Plan and Hideout the
    /// Plan workspace and its section (package 10, #288), Team and Group the Team workspace
    /// (package 9, #289), Setup the V2SetupWorkspaceView (package 6, #292), Intel plus Ammo, Keys
    /// and Flea their own Intel workspaces, and Loadout and Events their Plan workspaces
    /// (packages 17 and 28). All fourteen stay reachable from V1 navigation under
    /// <c>--ui-shell legacy</c>.
    /// </remarks>
    public static IReadOnlyList<string> V1PageNames()
    {
        var source = File.ReadAllText(
            RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "MainWindowViewModel.cs"));
        var names = Regex.Matches(source, @"CreateNavigation\(""([A-Za-z]+)""")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(14, names.Length);
        return names;
    }

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
