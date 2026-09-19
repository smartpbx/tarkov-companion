using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.UnitTests.Watching;

/// <summary>
/// A quest handed in before the companion started watching reaches recorded progress.
/// </summary>
/// <remarks>
/// A player mapped a whole raid with the companion open, handed in quests during it, and the
/// board never moved. Every part of the chain worked in isolation, which is why it took a day to
/// find: the game announces the quest, the parser reads the line, the service records it, the
/// board refreshes after mutations. What did not happen was the handover.
///
/// The watcher starts every already-existing file at its end, so the tail only ever delivers
/// what is appended after the first poll. Everything already written is recovered by a separate
/// startup replay, and that replay fed the raid state machine and nothing else -- it never
/// called the observer. So in the ordinary case, where the game's session folder exists before
/// the companion starts watching, the raid was recognised and every quest in it was dropped.
///
/// These tests run the real watcher against a real session folder on disk, so they fail on the
/// old code for the reason the player's machine did.
/// </remarks>
public sealed class QuestLogToProgressTests
{
    /// <summary>The quest whose hand-in the fixture announces.</summary>
    private const string HandedIn = "5936d90786f7742b1420ba5b";

    [Fact]
    public async Task AQuestHandedInBeforeWatchingStartsIsRecordedOnce()
    {
        using var session = new QuestLogSessionFixture();
        session.Write(
            "backend",
            QuestLogSessionFixture.Chatter("session start"),
            QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"),
            QuestLogSessionFixture.Chatter("session continues"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        var recorded = Assert.Single(chain.Commands.Calls);
        Assert.Equal(HandedIn, recorded.TaskId);
        Assert.Equal(RecordedTaskState.Completed, recorded.State);
        Assert.Equal(1, chain.Quests.Reading.Recorded);
        Assert.Equal(1, chain.Quests.Reading.Observed);
    }

    /// <summary>
    /// The shape that was actually on the player's disk, end to end.
    /// </summary>
    /// <remarks>
    /// backend_000.log announces a quest as "new_message", not "ChatMessageReceived", and the
    /// parser rejected any line without the latter. The companion was reading the right file the
    /// whole time and throwing every quest in it away on a substring scan. Measured on
    /// 1.1.5.1.47510: zero ChatMessageReceived in backend, fourteen new_message lines carrying one
    /// raid's quest events.
    /// </remarks>
    [Fact]
    public async Task AQuestAnnouncedTheWayTheBackendLogAnnouncesItIsRecorded()
    {
        using var session = new QuestLogSessionFixture();
        session.Write(
            "backend",
            QuestLogSessionFixture.Chatter("session start"),
            QuestLogSessionFixture.BackendQuest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        var recorded = Assert.Single(chain.Commands.Calls);
        Assert.Equal(HandedIn, recorded.TaskId);
        // The message's own text reads "quest started" on a hand-in. Keyed on message.type.
        Assert.Equal(RecordedTaskState.Completed, recorded.State);
    }

    /// <summary>
    /// Reading the same session again does not record it again.
    /// </summary>
    /// <remarks>
    /// This is what makes replaying a whole session at startup safe to do at all. The
    /// notification carries its own id and the same id is not acted on twice.
    /// </remarks>
    [Fact]
    public async Task ReadingTheSameSessionTwiceRecordsOneCompletion()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);
        await chain.ReadAsync(session.Root);

        Assert.Single(chain.Commands.Calls);
        Assert.Equal(1, chain.Quests.Reading.Recorded);
        // Read twice, so heard twice: what came of it is a separate count from what arrived.
        Assert.Equal(2, chain.Quests.Reading.Observed);
    }

    /// <summary>
    /// The startup replay used to skip the output log by name.
    /// </summary>
    /// <remarks>
    /// On the grounds that "every notification it carries is duplicated into backend". That was
    /// measured on an older build. On 1.1.5.x a session's backend log can hold seven hundred
    /// lines, two recognised raids and not one ChatMessageReceived.
    /// </remarks>
    [Fact]
    public async Task AQuestAnnouncedInTheOutputLogIsRead()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Chatter("nothing about quests here"));
        session.Write("output", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        Assert.Equal(HandedIn, Assert.Single(chain.Commands.Calls).TaskId);
    }

    /// <summary>
    /// The notifications log is read for quests, having previously not been opened at all.
    /// </summary>
    [Fact]
    public async Task AQuestAnnouncedInThePushNotificationsLogIsRead()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Chatter("nothing about quests here"));
        session.Write("push-notifications", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        Assert.Equal(HandedIn, Assert.Single(chain.Commands.Calls).TaskId);
    }

    /// <summary>
    /// And nothing else in that file is read.
    /// </summary>
    /// <remarks>
    /// The privacy reason for leaving push-notifications shut is real: its group blobs carry
    /// teammates' nicknames, full inventories, health and looted dogtags. Opening it for quests
    /// must not open it for those, so a party notification in it reaches nobody.
    /// </remarks>
    [Fact]
    public async Task ThePushNotificationsLogGivesUpItsQuestsAndNothingElse()
    {
        using var session = new QuestLogSessionFixture();
        session.Write(
            "push-notifications",
            QuestLogSessionFixture.GroupBlob(),
            QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        Assert.Equal(HandedIn, Assert.Single(chain.Commands.Calls).TaskId);
        Assert.Empty(chain.Squad.Current.Members);
    }

    /// <summary>A quest announced while the companion is watching still arrives by the tail.</summary>
    [Fact]
    public async Task AQuestAnnouncedWhileWatchingIsRecordedOnce()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Chatter("session start"));
        var chain = new Chain();

        await chain.ReadAsync(
            session.Root,
            appendAfterReplay: () => session.Append("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1")));

        Assert.Equal(HandedIn, Assert.Single(chain.Commands.Calls).TaskId);
    }

    /// <summary>
    /// A quest the loaded catalog does not have is counted as that, not as a write failure.
    /// </summary>
    /// <remarks>
    /// A catalog older than the patch the player is on is an ordinary situation with an obvious
    /// remedy, and reporting it as "could not be saved" sends somebody looking at their database.
    /// </remarks>
    [Fact]
    public async Task AQuestTheCatalogDoesNotHaveIsCountedSeparately()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain(new UnknownToTheCatalogCommands());

        await chain.ReadAsync(session.Root);

        Assert.Equal(1, chain.Quests.Reading.Observed);
        Assert.Equal(0, chain.Quests.Reading.Recorded);
        Assert.Equal(1, chain.Quests.Reading.Unmatched);
        Assert.Equal(0, chain.Quests.Reading.Failed);
    }

    /// <summary>The game's own word is stored as the game's own word.</summary>
    [Fact]
    public async Task AQuestReadFromTheLogIsAttributedToTheLog()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();

        await chain.ReadAsync(session.Root);

        var recorded = Assert.Single(chain.Commands.Calls);
        Assert.Equal(QuestProgressActor.GameLog, recorded.Actor);
        Assert.Equal(QuestProgressSources.GameLog, recorded.Source);
    }

    /// <summary>A board on screen is told, so it does not have to be reloaded by hand.</summary>
    [Fact]
    public async Task RecordingAQuestRaisesTheChangeTheBoardListensFor()
    {
        using var session = new QuestLogSessionFixture();
        session.Write("backend", QuestLogSessionFixture.Quest(12, HandedIn, "msg-1"));
        var chain = new Chain();
        var announced = new List<QuestLogProgressReading>();
        chain.Quests.Changed += (_, reading) => announced.Add(reading);

        await chain.ReadAsync(session.Root);

        Assert.Equal(1, Assert.Single(announced).Recorded);
    }

    /// <summary>
    /// The whole chain, from the file on disk to the recorded command.
    /// </summary>
    /// <remarks>
    /// The real watcher, the real observer fan-out and the real progress service. Only the
    /// profile and the storage behind them are stood in for, because what is being pinned is the
    /// handover between the reading and the recording.
    /// </remarks>
    private sealed class Chain
    {
        public Chain(RecordingCommands? commands = null)
        {
            Commands = commands ?? new RecordingCommands();
            Quests = new(new StubProfiles(), Commands, NullLogger<QuestLogProgressService>.Instance);
        }

        public RecordingCommands Commands { get; }

        public SquadStateService Squad { get; } = new();

        public QuestLogProgressService Quests { get; }

        /// <summary>
        /// Watches the folder until the expected work is done, then stops.
        /// </summary>
        /// <remarks>
        /// The startup replay runs before the watcher yields anything, so the interesting part is
        /// finished by the time the first poll comes round. The wait is bounded so a regression
        /// fails the test rather than hanging the suite.
        /// </remarks>
        public async Task ReadAsync(string root, Action? appendAfterReplay = null)
        {
            var observer = new EftLogObservers(Squad, new FleaSaleStateService(), Quests);
            var watcher = new WindowsEftLogWatcher(new EftLogParser(), observer);
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var before = Commands.Calls.Count;
            var pump = Task.Run(
                async () =>
                {
                    await foreach (var _ in watcher.WatchAsync(root, stopping.Token).ConfigureAwait(false))
                    {
                    }
                },
                CancellationToken.None);

            if (appendAfterReplay is not null)
            {
                // After a poll interval, so the startup replay has certainly finished and the
                // line being appended is genuinely delivered by the tail rather than by the
                // replay. Without the wait this test would pass on a watcher with no tail at all.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), CancellationToken.None).ConfigureAwait(false);
                appendAfterReplay();
            }

            while (Commands.Calls.Count == before && !stopping.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }

            // One more poll interval, so a line that would have been recorded twice has had the
            // chance to be. Without it "recorded once" would only mean "recorded at least once".
            await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None).ConfigureAwait(false);
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private class RecordingCommands : StubCommands
    {
        public List<(string TaskId, RecordedTaskState State, QuestProgressActor Actor, string Source)> Calls { get; } = [];

        public override Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            QuestProgressActor actor,
            string source,
            CancellationToken cancellationToken)
        {
            Calls.Add((taskId, state, actor, source));
            return Task.FromResult(new QuestProgressCommandResult(Guid.NewGuid(), Calls.Count, true));
        }
    }

    private sealed class UnknownToTheCatalogCommands : RecordingCommands
    {
        public override Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            QuestProgressActor actor,
            string source,
            CancellationToken cancellationToken) =>
            throw new QuestCatalogEntryUnknownException($"'{taskId}' is not in the catalog.", taskId);
    }

    private class StubCommands : IQuestProgressCommandService
    {
        public Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            CancellationToken cancellationToken) =>
            SetTaskStateAsync(scope, taskId, state, QuestProgressActor.User, QuestProgressSources.Manual, cancellationToken);

        public virtual Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            QuestProgressActor actor,
            string source,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QuestProgressCommandResult(Guid.NewGuid(), 1, true));

        public Task<QuestProgressCommandResult> SetObjectiveProgressAsync(
            QuestProfileScope scope,
            string objectiveId,
            RecordedObjectiveState state,
            decimal? count,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetItemHoldingAsync(
            QuestProfileScope scope,
            string itemId,
            bool foundInRaid,
            int? count,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetPinAsync(
            QuestProfileScope scope,
            QuestPinTargetKind targetKind,
            string targetId,
            bool isPinned,
            int sortOrder,
            string? note,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                Guid.Empty,
                "Local profile",
                GameMode.Regular,
                1,
                Faction.Unknown,
                null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, EventItemState>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
