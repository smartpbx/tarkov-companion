using TarkovCompanion.App.Services.V2.SelfTest;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// Render-only readings for the Setup self-test, chosen to show all three verdicts at once.
/// </summary>
/// <remarks>
/// [V2 rough package 41] A Linux headless run has no game, no relay and no tablet, so the real
/// readings answer "could not be tested" seven times and the render proves nothing about how a
/// failure or a pass looks. These are fixtures at the readings seam only: the probes, the
/// session, the view model and the view downstream of them are the shipping path.
///
/// The shape is one Clayton has actually had — a log folder the game stopped writing to while
/// screenshots kept arriving, one endpoint that refused, and a tablet paired to a desktop that
/// has published nothing.
/// </remarks>
internal sealed class SelfTestDemoReadings(bool waitsForScreenshot = false) : ISelfTestReadings
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public Task<SelfTestFolders> ReadFoldersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestFolders(
            true,
            "Escape from Tarkov installation and file roots are available.",
            Now,
            [
                new(
                    SelfTestFolderPurpose.Install,
                    @"E:\Battlestate Games\EFT",
                    "it is where the game itself says it is installed",
                    true,
                    Now.AddDays(-11),
                    24),
                new(
                    SelfTestFolderPurpose.Logs,
                    @"E:\Battlestate Games\EFT\Logs",
                    "it is the first of the game's usual log folders that exists",
                    true,
                    Now.AddDays(-6),
                    38),
                new(
                    SelfTestFolderPurpose.Screenshots,
                    @"C:\Users\Player\Documents\Escape from Tarkov\Screenshots",
                    "of the folders that exist, it holds the newest screenshot",
                    true,
                    Now.AddMinutes(-3),
                    1_284),
            ]));

    public Task<SelfTestLogs> ReadLogsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestLogs(
            "log_2026.09.18_19-41-22",
            Now.AddHours(-2),
            "2026.09.18_19-41-22 application.log",
            5_182_400,
            21_407,
            3,
            "customs",
            "PostRaid",
            Now.AddMinutes(-19),
            TimeSpan.FromSeconds(24.73),
            4,
            2,
            Now));

    /// <summary>
    /// [V2 rough package 43a] The screenshot the probe found, or nothing, so both states render.
    /// </summary>
    /// <remarks>
    /// Defaults to the shot it found already on disk, which is the usual answer now; the waiting
    /// state is what a folder with nothing recent in it produces, and it is worth a picture
    /// because it is the state that used to be a red failure.
    /// </remarks>
    public Task<SelfTestScreenshot> RecentScreenshotAsync(TimeSpan lookBack, CancellationToken cancellationToken) =>
        waitsForScreenshot
            ? Task.FromResult(new SelfTestScreenshot(
                @"C:\Users\Player\Documents\Escape from Tarkov\Screenshots",
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                "no clock",
                TimeSpan.Zero,
                null))
            : Task.FromResult(Found() with { WasAlreadyThere = true, Age = TimeSpan.FromMinutes(3) });

    public Task<SelfTestScreenshot> WatchScreenshotAsync(TimeSpan patience, CancellationToken cancellationToken) =>
        // Never answers in a render: the point of the waiting picture is the waiting.
        waitsForScreenshot
            ? new TaskCompletionSource<SelfTestScreenshot>().Task
            : Task.FromResult(Found());

    private static SelfTestScreenshot Found() =>
        new(
            @"C:\Users\Player\Documents\Escape from Tarkov\Screenshots",
            "2026-09-18[20-58]_140.2, 3.4, -77.9_-0.033, -0.133, 0.004, -0.991_21.87 (0).png",
            Now.AddSeconds(-1),
            Now,
            true,
            140.2,
            3.4,
            -77.9,
            "the file's own write time",
            TimeSpan.FromSeconds(7.4),
            TimeSpan.FromMilliseconds(412));

    public Task<SelfTestGameData> ReadGameDataAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestGameData(
            "regular",
            "en",
            [
                new("barters", 1_180_000, Now.AddHours(-4), 1_642, "current"),
                new("crafts", 410_000, Now.AddHours(-4), 312, "current"),
                new("hideout", 260_000, Now.AddHours(-4), 26, "current"),
                new("items", 48_900_000, Now.AddHours(-4), 5_321, "current"),
                new("maps", 3_100_000, Now.AddHours(-4), 17, "current"),
                new("tasks", 9_400_000, Now.AddDays(-2), 515, "refused", "502 Bad Gateway from json.tarkov.dev"),
                new("traders", 180_000, Now.AddHours(-4), 8, "current"),
            ],
            Now));

    public Task<SelfTestDatabase> ReadDatabaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestDatabase(
            @"C:\Users\clayton\AppData\Roaming\TarkovCompanion\Database\tarkov-companion.db",
            98_566_144,
            [.. Migrations],
            [.. Migrations],
            [
                new("items", 5_321),
                new("tasks", 515),
                new("maps", 17),
                new("traders", 8),
                new("hideout_stations", 26),
                new("barters", 1_642),
                new("crafts", 312),
                new("raids", 254),
                new("raid_events", 3_918),
                new("scan_history", 611),
            ],
            Now));

    public Task<SelfTestRelay> ReadRelayAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestRelay(
            true,
            "https://relay.example.net/",
            true,
            "1.4.2",
            "a91c07d",
            7,
            1,
            3,
            TimeSpan.FromMilliseconds(38),
            true,
            "Clayton",
            [
                new("Dave", "customs", TimeSpan.FromMilliseconds(410), TimeSpan.FromSeconds(1)),
                new("Riley", "customs", TimeSpan.FromSeconds(6.2), TimeSpan.FromSeconds(2)),
            ],
            null,
            Now)
        {
            // Package 31's measured numbers after its fix: 0.40 s median, 0.47 s p95.
            PositionLatency = new(
                31,
                12,
                TimeSpan.FromMilliseconds(400),
                TimeSpan.FromMilliseconds(470),
                TimeSpan.FromMilliseconds(420)),
        });

    public Task<SelfTestTablet> ReadTabletAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SelfTestTablet(
            true,
            "https://relay.example.net/",
            [
                new("Clayton's iPad", "Control", "Active", Now.AddMinutes(-4)),
                new("Kitchen tablet", "Viewer", "Active", Now.AddDays(-2)),
            ],
            false,
            null,
            null,
            0,
            Now));

    private static IEnumerable<string> Migrations =>
    [
        "0001_initial",
        "0002_data_cache",
        "0003_recognition_scan_metadata",
        "0004_quest_catalog_fidelity",
        "0005_local_quest_progress",
        "0006_quest_progress_exchange",
        "0007_drop_superseded_tables",
        "0008_loot_containers",
        "0009_drop_unread_map_tables",
        "0010_drop_quest_catalog_orphans",
        "0011_v2_data_platform",
        "0012_task_wiki_link",
        "0013_task_objective_task_scoped_keys",
        "0015_flea_market_settings",
        "0016_restore_task_objective_items",
    ];
}
