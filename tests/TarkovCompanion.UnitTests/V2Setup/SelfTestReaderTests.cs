using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// The three readers that touch a real installation, against folders built for the occasion.
/// </summary>
/// <remarks>
/// These are the parts that cannot be proved by reasoning about records: whether the newest
/// session is found by the name the game wrote rather than by a timestamp that can drift,
/// whether a session's own lines come back through the same parsers the watcher uses, and
/// whether a screenshot that lands while the test is watching is noticed at all.
/// </remarks>
public sealed class SelfTestReaderTests : IDisposable
{
    private const string LogLines = """
        2026-09-18 20:31:14.000|1.1.5.0.47242|Info|application|MatchingCompleted:18.36 real:24.73 diff:6.66
        2026-09-18 20:31:38.000|1.1.5.0.47242|Info|application|LocationLoaded:18 real:24.73 diff:6.72
        2026-09-18 20:31:45.000|1.1.5.0.47242|Info|output|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: [REDACTED], Status: Busy, RaidMode: Online, Ip: [REDACTED], Port: 17009, Location: Shoreline, Sid: [REDACTED], GameMode: deathmatch, shortId: [REDACTED]'
        2026-09-18 20:33:00.000|1.1.5.0.47242|Info|backend|NOTIFICATION [EVENTID] RagfairOfferSold [{"type":"RagfairOfferSold","eventId":"ID_1","offerId":"OFFER_1","handbookId":"ITEM_1","count":3}]
        2026-09-18 20:34:00.000 +00:00|NOTIFICATION|6aa4bd43d4a840ddb8130198|ChatMessageReceived|[{"type":"new_message","eventId":"e1","dialogId":"5935c25fb3acc3127c3d8cd9","message":{"_id":"msg-1","uid":"5935c25fb3acc3127c3d8cd9","type":12,"text":"quest handed in","templateId":"5936d90786f7742b1420ba5b description"}}]
        2026-09-18 20:52:18.463|1.1.0.1.46911|Info|output|[Narrate] Game Stopped
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tarkov-selftest-" + Guid.NewGuid().ToString("N"));

    public SelfTestReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that outlives the test is not a test failure.
        }
    }

    [Fact]
    public void AFolderReportsWhatIsInItAndWhenItLastChanged()
    {
        var folder = Path.Combine(_root, "shots");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "one.png"), "x");
        var written = new DateTime(2026, 9, 18, 20, 58, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "one.png"), written);

        var state = new SelfTestFolderReader().Read(folder);

        Assert.True(state.Exists);
        Assert.Equal(1, state.Entries);
        Assert.Equal(written, state.NewestWriteUtc?.UtcDateTime);
        Assert.Null(state.Problem);
    }

    [Fact]
    public void AFolderThatIsNotThereSaysSoRatherThanReportingNothing()
    {
        var state = new SelfTestFolderReader().Read(Path.Combine(_root, "absent"));

        Assert.False(state.Exists);
        Assert.Contains("does not exist", state.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log root's newest change is its newest session's newest file.
    /// </summary>
    /// <remarks>
    /// The one measurement #414 turned on. Creating a session folder does not always restamp the
    /// root that holds it, so asking only the root would report a live installation as stale.
    /// </remarks>
    [Fact]
    public void ALogRootsChangeTimeComesFromTheFilesInsideItsSessionFolders()
    {
        var session = Path.Combine(_root, "log_2026.09.18_20-31-04");
        Directory.CreateDirectory(session);
        var file = Path.Combine(session, "2026.09.18_20-31-04 application.log");
        File.WriteAllText(file, LogLines);
        var written = new DateTime(2026, 9, 18, 20, 52, 20, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        Directory.SetLastWriteTimeUtc(session, new DateTime(2026, 9, 18, 20, 31, 4, DateTimeKind.Utc));

        var state = new SelfTestFolderReader().Read(_root);

        Assert.Equal(written, state.NewestWriteUtc?.UtcDateTime);
    }

    [Fact]
    public async Task TheNewestSessionIsReplayedAndWhatWasUnderstoodIsCounted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "log_2026.09.17_10-00-00"));
        var session = Path.Combine(_root, "log_2026.09.18_20-31-04");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "2026.09.18_20-31-04 application.log"), LogLines);

        var reading = await new SelfTestLogReader().ReadAsync(_root, CancellationToken.None);

        Assert.Null(reading.Problem);
        Assert.Equal("log_2026.09.18_20-31-04", reading.SessionFolder);
        Assert.Equal("2026.09.18_20-31-04 application.log", reading.FileName);
        Assert.Equal(6, reading.LinesRead);
        Assert.Equal("shoreline", reading.LastRaidMap);
        Assert.Equal(24.73, reading.QueueTime?.TotalSeconds ?? 0, 2);
        Assert.Equal(1, reading.QuestEvents);
        Assert.Equal(1, reading.FleaSales);
        Assert.Equal(1, reading.RaidsSeen);
    }

    /// <summary>The game's own output log is the largest file and is duplicated into backend.</summary>
    [Fact]
    public async Task TheOutputLogIsNotTheFileThatIsRead()
    {
        var session = Path.Combine(_root, "log_2026.09.18_20-31-04");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "2026.09.18_20-31-04 output.log"), LogLines);
        File.WriteAllText(Path.Combine(session, "2026.09.18_20-31-04 application.log"), LogLines);

        var reading = await new SelfTestLogReader().ReadAsync(_root, CancellationToken.None);

        Assert.Equal("2026.09.18_20-31-04 application.log", reading.FileName);
    }

    [Theory]
    [InlineData(null, "no log folder has been chosen")]
    [InlineData("", "no log folder has been chosen")]
    public async Task NoLogFolderSaysSoPlainly(string? root, string expected)
    {
        var reading = await new SelfTestLogReader().ReadAsync(root, CancellationToken.None);

        Assert.Contains(expected, reading.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogRootWithNoSessionInItSaysThatRatherThanReadingNothingQuietly()
    {
        var reading = await new SelfTestLogReader().ReadAsync(_root, CancellationToken.None);

        Assert.Contains("holds no session folder", reading.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScreenshotThatArrivesWhileWatchingIsParsedAndTimedEndToEnd()
    {
        var folder = Path.Combine(_root, "shots");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "already-there.png"), "x");
        var watch = new SelfTestScreenshotWatch(new ScreenshotFilenameParser());
        // The name carries the time the shot was taken, and the watch reports the file's own write
        // time as the clock only while the two agree to within the parser's hour of drift. Written
        // from now rather than from the hour this test was: hardcoded, it passed for an hour on the
        // day it was written and has failed on every run since.
        var taken = DateTime.Now;
        var name = $"{taken:yyyy-MM-dd}[{taken:HH-mm}]_140.2, 3.4, -77.9_-0.03, -0.13, 0.004, -0.99_21.87 (0).png";

        var watching = watch.WatchAsync(folder, TimeSpan.FromSeconds(10), TimeSpan.Zero, CancellationToken.None);
        await Task.Delay(120);
        File.WriteAllText(Path.Combine(folder, name), "x");
        var reading = await watching;

        Assert.Equal(name, reading.FileName);
        Assert.True(reading.Parsed);
        Assert.Equal(140.2, reading.X ?? 0, 1);
        Assert.Equal(-77.9, reading.Z ?? 0, 1);
        Assert.NotNull(reading.EndToEnd);
        Assert.Equal("the file's own write time", reading.Clock);
    }

    [Fact]
    public async Task AScreenshotWhoseNameYieldsNoPositionIsReportedRatherThanIgnored()
    {
        var folder = Path.Combine(_root, "shots");
        Directory.CreateDirectory(folder);
        var watch = new SelfTestScreenshotWatch(new ScreenshotFilenameParser());

        var watching = watch.WatchAsync(folder, TimeSpan.FromSeconds(10), TimeSpan.Zero, CancellationToken.None);
        await Task.Delay(120);
        File.WriteAllText(Path.Combine(folder, "Screenshot 2026-09-18 205812.png"), "x");
        var reading = await watching;

        Assert.Equal("Screenshot 2026-09-18 205812.png", reading.FileName);
        Assert.False(reading.Parsed);
    }

    [Fact]
    public async Task WaitingOutThePatienceReportsNothingArrivedRatherThanAFailure()
    {
        var folder = Path.Combine(_root, "shots");
        Directory.CreateDirectory(folder);

        var reading = await new SelfTestScreenshotWatch(new ScreenshotFilenameParser())
            .WatchAsync(folder, TimeSpan.FromMilliseconds(400), TimeSpan.Zero, CancellationToken.None);

        Assert.Null(reading.FileName);
        Assert.Null(reading.Problem);
    }

    [Fact]
    public async Task NoScreenshotFolderSaysSoRatherThanWaiting()
    {
        var reading = await new SelfTestScreenshotWatch(new ScreenshotFilenameParser())
            .WatchAsync(Path.Combine(_root, "absent"), TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None);

        Assert.Contains("does not exist", reading.Problem ?? string.Empty, StringComparison.Ordinal);
    }
}
