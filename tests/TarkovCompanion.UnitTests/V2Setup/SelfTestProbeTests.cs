using System.Globalization;
using TarkovCompanion.App.Services.V2.SelfTest;

using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// The verdict each probe reaches, and the fact it puts under it.
/// </summary>
/// <remarks>
/// Three shapes for every capability: it worked, it did not, and it could not be measured. The
/// third is the one worth pinning. Setup already had a checklist that showed green for anything
/// nobody had tested, which is how a stale log folder and an unnamed endpoint failure both
/// survived an evening — so an untested capability must never come back as a pass.
/// </remarks>
public sealed class SelfTestProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeSpan Took = TimeSpan.FromMilliseconds(120);

    [Fact]
    public void FoldersPassWhenEveryRootExistsAndIsBeingWrittenTo()
    {
        var result = SelfTestProbes.Folders(Folders(logsChanged: Now.AddMinutes(-2)), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains(@"D:\Games\EFT\Logs", StringComparison.Ordinal));
        Assert.All(result.Facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.Source)));
    }

    /// <summary>#414: the log folder the game stopped writing to, while screenshots kept arriving.</summary>
    [Fact]
    public void FoldersFailWhenTheLogFolderHasStoodStillWhileScreenshotsArrived()
    {
        var result = SelfTestProbes.Folders(Folders(logsChanged: Now.AddDays(-3)), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("stood still", StringComparison.Ordinal));
        Assert.Equal(
            "The log folder has stopped changing while screenshots keep arriving.",
            result.Headline);
    }

    [Fact]
    public void FoldersFailAndNameTheRootThatIsMissing()
    {
        var reading = Folders(logsChanged: Now) with
        {
            Folders =
            [
                new("Install", null, "nothing on this machine looks like an install", false, null, 0, "not found"),
                new("Logs", @"D:\Games\EFT\Logs", "it is the first that exists", true, Now, 4),
                new("Screenshots", @"D:\Shots", "it holds the newest screenshot", true, Now, 9),
            ],
        };

        var result = SelfTestProbes.Folders(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Equal("The install folder could not be used.", result.Headline);
    }

    [Fact]
    public void FoldersAreUnknownOffWindowsRatherThanPassing()
    {
        var reading = new SelfTestFolders(false, "This platform cannot look.", Now, [], "this platform cannot look");

        var result = SelfTestProbes.Folders(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void LogsPassAndSayWhatWasUnderstood()
    {
        var result = SelfTestProbes.Logs(Logs(), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("2 raid(s) recognised", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("Queue time 24.7 s", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("3 quest notification(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void LogsFailWhenAFileWasThereAndNotOneLineCameBack()
    {
        var result = SelfTestProbes.Logs(Logs() with { LinesRead = 0, RaidsSeen = 0, QuestEvents = 0, FleaSales = 0, QueueTime = null }, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains("not one line came back", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAreUnknownWhenNothingWasReadAndSayWhy()
    {
        var reading = new SelfTestLogs(
            null, null, null, 0, 0, 0, null, null, null, null, 0, 0, Now, "the log folder D:\\gone does not exist");

        var result = SelfTestProbes.Logs(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
        Assert.Contains("does not exist", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAreUnknownWhenLinesWereReadAndNothingWasRecognised()
    {
        var reading = Logs() with { RaidsSeen = 0, QuestEvents = 0, FleaSales = 0, QueueTime = null };

        var result = SelfTestProbes.Logs(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
        Assert.Contains("recognised nothing", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotsPassAndSayWhichClockAndHowLongEndToEnd()
    {
        var result = SelfTestProbes.Screenshots(Screenshot(), Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("the file's own write time", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("end to end", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("Position 140.2", StringComparison.Ordinal));
    }

    /// <summary>The failure nobody could see: the file is there and no position comes out of it.</summary>
    [Fact]
    public void ScreenshotsFailOnlyWhenAnInRaidNameYieldsNoPosition()
    {
        var result = SelfTestProbes.Screenshots(UnreadableInRaidScreenshot(), Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("put nobody on the map", StringComparison.Ordinal));
    }

    /// <summary>
    /// [V2 rough package 43a] Reported by Clayton: the probe called
    /// <c>2026-09-18[19-03]_19.67 (1).png</c> broken. The game writes the coordinate blocks only
    /// for a shot taken in a raid, so that name never had a position and refusing to read one is
    /// the parser working. This is the branch that stops correct behaviour being reported as a
    /// fault.
    /// </summary>
    [Fact]
    public void AScreenshotTakenOutsideARaidIsAFactRatherThanAFault()
    {
        var result = SelfTestProbes.Screenshots(OutsideRaidScreenshot(), Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
        Assert.Contains("outside a raid", result.Headline, StringComparison.Ordinal);
        Assert.Contains("during a raid", result.Headline, StringComparison.Ordinal);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("carries no coordinates", StringComparison.Ordinal));
    }

    /// <summary>A shot already on disk answers the same question, and says which file it used.</summary>
    [Fact]
    public void AScreenshotAlreadyOnDiskCountsAndIsNamed()
    {
        var reading = Screenshot() with { WasAlreadyThere = true, Age = TimeSpan.FromMinutes(3) };

        var result = SelfTestProbes.Screenshots(reading, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains("already had", result.Headline, StringComparison.Ordinal);
        Assert.Contains(result.Facts, fact => fact.Text.Contains(reading.FileName!, StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("3 min before this ran", StringComparison.Ordinal));
        // The reporting the original probe had, kept: which clock, and the end-to-end delay.
        Assert.Contains(result.Facts, fact => fact.Text.Contains("taken from the file's own write time", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("to this position being parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void ScreenshotsAreUnknownWhenNoneArrivedAndAskForOne()
    {
        var reading = new SelfTestScreenshot(
            @"D:\Shots", null, null, null, false, null, null, null, "no clock", TimeSpan.FromSeconds(45), null);

        var result = SelfTestProbes.Screenshots(reading, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
        // Never a red failure for something the player did not do in time.
        Assert.NotEqual(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains("Nothing is wrong", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDataPassesAndNamesEveryEndpointWithItsSizeAgeAndRows()
    {
        var result = SelfTestProbes.GameData(GameData(), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        var items = Assert.Single(result.Facts, fact => fact.Text.StartsWith("items:", StringComparison.Ordinal));
        Assert.Contains("5,321 row(s)", items.Text, StringComparison.Ordinal);
        Assert.Contains("MB", items.Text, StringComparison.Ordinal);
        Assert.Contains("h ago", items.Text, StringComparison.Ordinal);
    }

    /// <summary>"2 endpoint refresh(es) failed" with no names is the thing this replaces.</summary>
    [Fact]
    public void GameDataFailsNamingTheEndpointAndTheReason()
    {
        var reading = GameData() with
        {
            Endpoints =
            [
                new("items", 14_900_000, Now.AddHours(-3), 5_321, "current"),
                new("tasks", 2_100_000, Now.AddDays(-2), 515, "failed", "502 from json.tarkov.dev"),
            ],
        };

        var result = SelfTestProbes.GameData(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains("tasks", result.Headline, StringComparison.Ordinal);
        Assert.Contains(
            result.Facts,
            fact => fact.Text.Contains("tasks: did not refresh — 502 from json.tarkov.dev", StringComparison.Ordinal));
    }

    [Fact]
    public void GameDataIsUnknownWhenNoEndpointHasEverBeenRecorded()
    {
        var result = SelfTestProbes.GameData(GameData() with { Endpoints = [] }, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void DatabasePassesWithItsSizeMigrationsAndTableCounts()
    {
        var result = SelfTestProbes.Database(Database(), Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("All 2 migrations applied", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("items: 5,321 row(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseFailsWhenTheSchemaIsBehindTheBuild()
    {
        var reading = Database() with { Applied = ["0001_initial"] };

        var result = SelfTestProbes.Database(reading, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("0002_data_cache", StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseIsUnknownWhenThereIsNoneToRead()
    {
        var reading = Database() with { Path = null, Problem = "the database has not been opened yet" };

        var result = SelfTestProbes.Database(reading, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
    }

    /// <summary>The measured number package 31 times, not the age of one marker.</summary>
    [Fact]
    public void RelayPassesWithItsBuildRoundTripRoomAndTheMeasuredPositionLatency()
    {
        var result = SelfTestProbes.Relay(Relay(), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("Running 1.4.2", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("Sharing is on as Clayton", StringComparison.Ordinal));
        Assert.Contains(
            result.Facts,
            fact => fact.Text.Contains(
                "Squadmate positions arrive in 0.40 s, 0.47 s at the slow end, over 12 of 31 delivered",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RelayFallsBackToTheFreshestMarkerBeforeAnythingHasBeenDelivered()
    {
        var reading = Relay() with { PositionLatency = SelfTestPositionLatency.None };

        var result = SelfTestProbes.Relay(reading, Now, Took, Culture);

        Assert.Contains(
            result.Facts,
            fact => fact.Text.Contains("the freshest marker is 0.4 s old (Dave)", StringComparison.Ordinal));
    }

    [Fact]
    public void RelayFailsWhenItDoesNotAnswer()
    {
        var reading = Relay() with { Reachable = false, Problem = "it did not answer within 5 s" };

        var result = SelfTestProbes.Relay(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("did not answer within 5 s", StringComparison.Ordinal));
    }

    [Fact]
    public void RelayIsUnknownWhenNoneIsConfigured()
    {
        var reading = Relay() with { Configured = false, Origin = null, Reachable = false };

        var result = SelfTestProbes.Relay(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
        Assert.Contains("nothing to reach", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayPositionLatencySaysItCouldNotBeMeasuredRatherThanReportingZero()
    {
        var reading = Relay() with { Others = [], PositionLatency = SelfTestPositionLatency.None };

        var result = SelfTestProbes.Relay(reading, Now, Took, Culture);

        Assert.Contains(
            result.Facts,
            fact => fact.Text.Contains("position latency could not be measured", StringComparison.Ordinal));
    }

    [Fact]
    public void TabletPassesWhenADeviceIsPairedAndTheDesktopIsPublishing()
    {
        var result = SelfTestProbes.Tablet(Tablet(), Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Pass, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("iPad", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("Customs", StringComparison.Ordinal));
    }

    [Fact]
    public void TabletFailsWhenADeviceIsPairedAndNothingHasBeenPublished()
    {
        var reading = Tablet() with { Publishing = false, PublishedUtc = null, MapName = null, Objects = 0 };

        var result = SelfTestProbes.Tablet(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Fail, result.Outcome);
        Assert.Contains(result.Facts, fact => fact.Text.Contains("published no scene", StringComparison.Ordinal));
    }

    [Fact]
    public void TabletIsUnknownWhenNothingIsPaired()
    {
        var reading = Tablet() with { Devices = [] };

        var result = SelfTestProbes.Tablet(reading, Now, Took, Culture);

        Assert.Equal(SelfTestOutcome.Unknown, result.Outcome);
    }

    /// <summary>The rule the whole page rests on: a line states its source, never only a verdict.</summary>
    [Fact]
    public void EveryFactEveryProbeProducesNamesWhatItWasReadFrom()
    {
        SelfTestCapability[] capabilities =
        [
            SelfTestProbes.Folders(Folders(Now.AddMinutes(-2)), Now, Took, Culture),
            SelfTestProbes.Logs(Logs(), Now, Took, Culture),
            SelfTestProbes.Screenshots(Screenshot(), Took, Culture),
            SelfTestProbes.GameData(GameData(), Now, Took, Culture),
            SelfTestProbes.Database(Database(), Took, Culture),
            SelfTestProbes.Relay(Relay(), Now, Took, Culture),
            SelfTestProbes.Tablet(Tablet(), Now, Took, Culture),
        ];

        foreach (var capability in capabilities)
        {
            Assert.NotEmpty(capability.Facts);
            Assert.All(capability.Facts, fact =>
            {
                Assert.False(string.IsNullOrWhiteSpace(fact.Text));
                Assert.False(string.IsNullOrWhiteSpace(fact.Source));
            });
        }
    }

    internal static SelfTestFolders Folders(DateTimeOffset logsChanged) => new(
        true,
        "Escape from Tarkov installation and file roots are available.",
        Now,
        [
            new("Install", @"D:\Games\EFT", "it is where the game itself says it is installed", true, Now.AddDays(-9), 22),
            new("Logs", @"D:\Games\EFT\Logs", "it is the first of the game's usual log folders that exists", true, logsChanged, 41),
            new("Screenshots", @"D:\Shots", "of the folders that exist, it holds the newest screenshot", true, Now.AddMinutes(-1), 620),
        ]);

    internal static SelfTestLogs Logs() => new(
        "log_2026.09.18_20-31-04",
        Now.AddMinutes(-30),
        "2026.09.18_20-31-04 application.log",
        4_200_000,
        18_400,
        2,
        "customs",
        "PostRaid",
        Now.AddMinutes(-4),
        TimeSpan.FromSeconds(24.73),
        3,
        1,
        Now);

    internal static SelfTestScreenshot Screenshot() => new(
        @"D:\Shots",
        "2026-09-18[20-58]_140.2, 3.4, -77.9_0.0, 0.7, 0.0, 0.7_12.34 (0).png",
        Now.AddSeconds(-1),
        Now,
        true,
        140.2,
        3.4,
        -77.9,
        "the file's own write time",
        TimeSpan.FromSeconds(6.2),
        TimeSpan.FromMilliseconds(410));

    /// <summary>A folder that was looked at and held no screenshot worth reading.</summary>
    internal static SelfTestScreenshot NoScreenshot(TimeSpan? waited = null) => new(
        @"D:\Shots",
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        "no clock",
        waited ?? TimeSpan.Zero,
        null);

    /// <summary>A screenshot taken outside a raid: the game wrote no position into the name.</summary>
    internal static SelfTestScreenshot OutsideRaidScreenshot() => new(
        @"D:\Shots",
        "2026-09-18[19-03]_19.67 (1).png",
        Now.AddMinutes(-2),
        Now,
        false,
        null,
        null,
        null,
        "the file's own write time",
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2))
    {
        WasAlreadyThere = true,
        NameKind = ScreenshotNameKind.OutsideRaid,
        Age = TimeSpan.FromMinutes(2),
    };

    /// <summary>A screenshot whose name says it was taken in a raid and still will not parse.</summary>
    internal static SelfTestScreenshot UnreadableInRaidScreenshot() => new(
        @"D:\Shots",
        "2026-09-18[19-03]_-125.4, 2.3, 189.7_0.0, 0.7, 0.0, -0.7_12.34.png",
        Now.AddSeconds(-1),
        Now,
        false,
        null,
        null,
        null,
        "the file's own write time",
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(900))
    {
        NameKind = ScreenshotNameKind.InRaid,
    };

    internal static SelfTestGameData GameData() => new(
        "regular",
        "en",
        [
            new("items", 14_900_000, Now.AddHours(-3), 5_321, "current"),
            new("tasks", 2_100_000, Now.AddHours(-3), 515, "current"),
        ],
        Now);

    internal static SelfTestDatabase Database() => new(
        @"C:\Users\c\AppData\Roaming\TarkovCompanion\Database\tarkov-companion.db",
        94_000_000,
        ["0001_initial", "0002_data_cache"],
        ["0001_initial", "0002_data_cache"],
        [new("items", 5_321), new("raids", 254)],
        Now);

    internal static SelfTestRelay Relay() => new SelfTestRelay(
        true,
        "https://relay.example/",
        true,
        "1.4.2",
        "abc1234",
        7,
        1,
        3,
        TimeSpan.FromMilliseconds(38),
        true,
        "Clayton",
        [new("Dave", "customs", TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1))],
        null,
        Now)
    {
        // The numbers package 31 measured after its own fix: 0.40 s median, 0.47 s p95.
        PositionLatency = new(31, 12, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(470), TimeSpan.FromMilliseconds(420)),
    };

    internal static SelfTestTablet Tablet() => new(
        true,
        "https://relay.example/",
        [new("iPad", "Control", "Active", Now.AddMinutes(-2))],
        true,
        Now.AddSeconds(-3),
        "Customs",
        140,
        Now);
}
