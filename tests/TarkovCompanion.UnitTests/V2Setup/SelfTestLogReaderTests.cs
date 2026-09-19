using System.Globalization;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.UnitTests.Runtime;
using TarkovCompanion.UnitTests.Watching;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// What the Setup self-test says it understood of the newest game session.
/// </summary>
/// <remarks>
/// This reading is the instrument that was supposed to catch a session whose quests went
/// unrecorded, and it is part of why nobody did. It read one file -- whichever of the session's
/// files had been written to last -- and reported that file's counts as the session's. On a real
/// machine it picked the backend log, said "0 quest notification(s)" for a session in which
/// several quests had been handed in, and passed, because it had recognised two raids.
/// </remarks>
public sealed class SelfTestLogReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 23, 0, 0, TimeSpan.Zero);
    private const string HandedIn = "5936d90786f7742b1420ba5b";

    /// <summary>
    /// A quest in a file that is not the newest-written one is still counted.
    /// </summary>
    /// <remarks>
    /// The backend log is written last and holds no quest; the quest is in output. The old
    /// reading chose backend alone and reported zero.
    /// </remarks>
    [Fact]
    public async Task EveryFileOfTheSessionIsRead()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("output", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var backend = session.Write("backend", QuestLogSessionFixture.Chatter("no quests in here"));
        File.SetLastWriteTimeUtc(backend, DateTime.UtcNow.AddMinutes(5));

        var reading = await Read(session.Root);

        Assert.Null(reading.Problem);
        Assert.Equal(1, reading.QuestEvents);
        Assert.Equal(2, reading.Files.Count);
        Assert.Contains(reading.Files, file => file.Name.Contains("output", StringComparison.Ordinal) && file.QuestEvents == 1);
        Assert.Contains(reading.Files, file => file.Name.Contains("backend", StringComparison.Ordinal) && file.QuestEvents == 0);
    }

    /// <summary>The notifications log is reported, narrowly, exactly as the watcher reads it.</summary>
    [Fact]
    public async Task TheNotificationsLogIsReportedAsReadForItsChatOnly()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Chatter("no quests in here"));
        session.Write(
            "push-notifications",
            QuestLogSessionFixture.GroupBlob(),
            QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));

        var reading = await Read(session.Root);

        var narrow = Assert.Single(reading.Files, file => file.Mode == LogReadMode.ChatOnly);
        Assert.Equal(1, narrow.QuestEvents);
        Assert.Equal(1, reading.QuestEvents);
    }

    /// <summary>A log file the companion has no use for is named, so it is not a silent absence.</summary>
    [Fact]
    public async Task AFileThatIsNotOpenedIsNamed()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        session.Write("traces", QuestLogSessionFixture.Chatter("stack frames"));

        var reading = await Read(session.Root);

        Assert.Contains(reading.SkippedFiles, name => name.Contains("traces", StringComparison.Ordinal));
        Assert.DoesNotContain(reading.Files, file => file.Name.Contains("traces", StringComparison.Ordinal));
    }

    /// <summary>
    /// A session with raids and no quest anywhere no longer passes quietly.
    /// </summary>
    /// <remarks>
    /// This is the exact shape of the reading a player was handed: raids recognised, not one
    /// quest notification, reported as a pass. It reads a raid and misses every quest for one
    /// reason, and the report has to say so where he will see it.
    /// </remarks>
    [Fact]
    public void RaidsWithoutASingleQuestIsNotReportedAsAPass()
    {
        var reading = new SelfTestLogs(
            "log_2026.09.18_22-11-05_1.1.5.1.47510",
            Now.AddHours(-1),
            "backend_000.log",
            171_520,
            703,
            2,
            "reserve",
            "PostRaid",
            Now.AddMinutes(-20),
            null,
            0,
            0,
            Now)
        {
            Files = [new("backend_000.log", LogReadMode.Full, 171_520, 703, 0, 0)],
        };

        var capability = SelfTestProbes.Logs(reading, Now, TimeSpan.FromMilliseconds(40), CultureInfo.InvariantCulture);

        Assert.NotEqual(SelfTestOutcome.Pass, capability.Outcome);
        Assert.Contains("not one quest notification", capability.Headline, StringComparison.Ordinal);
    }

    private static Task<SelfTestLogs> Read(string root) =>
        new SelfTestLogReader(new ManualTimeProvider(Now)).ReadAsync(root, CancellationToken.None);
}
