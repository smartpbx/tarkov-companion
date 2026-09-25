using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The map follows the raid the player is actually in (#568).
/// </summary>
/// <remarks>
/// <para>
/// Built from one player's real day, 2026-09-20: thirteen launches of the game, nine
/// <c>userConfirmed</c>, eight <c>userMatchOver</c>, every short id pairing except <c>CX0FLS</c>,
/// the Streets raid whose game process died 110 seconds in. The raid ids, maps, statuses and times
/// below are the real ones; profile ids, addresses and session ids are made up, because the
/// repository is public and the real lines carry all three.
/// </para>
/// <para>
/// The game writes no shutdown marker. Every one of the thirteen folders "just stops", the clean
/// exits included, so nothing here treats a log that stops as a crash.
/// </para>
/// </remarks>
public sealed class RaidFollowsTheRealRaidTests
{
    private const string Pmc = "PMCPROFILE0000000000001";
    private const string Scav = "SCAVPROFILE000000000001";

    private const string Launch1338 = "log_2026.09.20_13-38-42_1.1.5.1.47510";
    private const string Launch1400 = "log_2026.09.20_14-00-47_1.1.5.1.47510";
    private const string Launch1415 = "log_2026.09.20_14-15-46_1.1.5.1.47510";
    private const string Launch1441 = "log_2026.09.20_14-41-49_1.1.5.1.47510";
    private const string Launch1626 = "log_2026.09.20_16-26-51_1.1.5.1.47510";
    private const string Launch1936 = "log_2026.09.20_19-36-06_1.1.5.1.47510";

    /// <summary>The player's zone that day, so the local stamps below mean what they meant.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-4", TimeSpan.FromHours(-4), "test-4", "test-4");

    private static DateTimeOffset Local(string time) =>
        new DateTimeOffset(DateTime.Parse($"2026-09-20 {time}", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(-4)).ToUniversalTime();

    private static string Confirmed(string time, string location, string shortId, string profile = Pmc, string? eventId = null) =>
        Notification(time, "userConfirmed", "Busy", location, shortId, profile, eventId ?? $"EV-{shortId}-C");

    private static string Over(string time, string location, string status, string shortId, string profile = Pmc) =>
        Notification(time, "userMatchOver", status, location, shortId, profile, $"EV-{shortId}-O");

    private static string Notification(string time, string type, string status, string location, string shortId, string profile, string eventId) =>
        $"2026-09-20 {time}.000|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION [{eventId}] {type} " +
        "[{\"type\":\"" + type + "\",\"eventId\":\"" + eventId + "\",\"profileid\":\"" + profile +
        "\",\"status\":\"" + status + "\",\"location\":\"" + location +
        "\",\"raidMode\":\"Online\",\"mode\":\"deathmatch\",\"shortId\":\"" + shortId + "\"}]";

    private static string ProfileStatus(string time, string location, string shortId) =>
        $"2026-09-20 {time}.818|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: " +
        $"'Profileid: {Pmc}, Status: Busy, RaidMode: Online, Ip: 0.0.0.0, Port: 17002, Location: {location}, " +
        $"Sid: SID, GameMode: deathmatch, shortId: {shortId}'";

    private static string LocationLoaded(string time) =>
        $"2026-09-20 {time}.364|1.1.5.1.47510|Info|application|LocationLoaded:14.85 real:20.32 diff:5.46";

    private static string GameStarted(string time) =>
        $"2026-09-20 {time}.684|1.1.5.1.47510|Info|application|GameStarted:137.44(137.44) real:149.64(149.64) diff:12.19";

    /// <summary>Parses a line the way the tail does: observed at the time it was written, in its launch.</summary>
    private static RaidEvidence Read(EftLogParser parser, string line, string launch)
    {
        var written = RaidReplayDecision.WrittenUtc(line, Zone);
        Assert.NotNull(written);
        var evidence = parser.ParseLine(line, written.Value);
        Assert.NotNull(evidence);
        return evidence with { LogSession = launch };
    }

    private static ReplayedRaidLine[] Replay(params string[] lines)
    {
        var parser = new EftLogParser();
        return
        [
            .. lines
                .Select((line, index) => (Evidence: parser.ParseLine(line, DateTimeOffset.UnixEpoch), Line: line, Index: index))
                .Where(read => read.Evidence is not null)
                // Each line says which file wrote it, in the field after the level.
                .Select(read => new ReplayedRaidLine(read.Evidence!, RaidReplayDecision.WrittenUtc(read.Line, Zone), read.Index)
                {
                    Source = read.Line.Split('|')[3],
                }),
        ];
    }

    [Fact]
    public void ACleanConfirmAndOverPairOpensAndClosesOneRaidUnderItsShortId()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();

        var opened = state.Apply(Read(parser, Confirmed("13:39:48", "Lighthouse", "CX00HG", Scav), Launch1338));
        Assert.Equal(RaidLifecycleState.InRaid, opened.State);
        Assert.Equal("lighthouse", opened.MapId);
        Assert.Equal("CX00HG", opened.RaidKey);
        Assert.Equal(Launch1338, opened.LogSession);

        var closed = state.Apply(Read(parser, Over("13:51:39", "Lighthouse", "Free", "CX00HG", Scav), Launch1338));
        Assert.Equal(RaidLifecycleState.PostRaid, closed.State);
        Assert.Equal(opened.RaidId, closed.RaidId);
    }

    /// <summary>
    /// The real line of 16:44:44: <c>userMatchOver location=Lighthouse status=Transfer shortId=CXK77H</c>.
    /// </summary>
    [Fact]
    public void TheRealTransferExitEndsTheRaid()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        state.Apply(Read(parser, Confirmed("16:34:46", "Lighthouse", "CXK77H", Scav), Launch1626));

        var closed = state.Apply(Read(parser, Over("16:44:44", "Lighthouse", "Transfer", "CXK77H", Scav), Launch1626));

        Assert.Equal(RaidLifecycleState.PostRaid, closed.State);
        Assert.Equal("scav", closed.Side);
    }

    /// <summary>
    /// CX0FLS: confirmed on Streets at 13:57:21, the game process died at 13:59:11 with no end
    /// written, the game was relaunched, and the next raid was on another map.
    /// </summary>
    /// <remarks>
    /// The next raid is read from the application log alone here, because that is the path that
    /// kept the dead raid's identity: the map moved, but the raid id, the start time and the trail
    /// stayed CX0FLS's, and its row stayed open.
    /// </remarks>
    [Fact]
    public async Task ARaidTheGameDiedInGivesWayToTheNextRaidOnItsOwnMap()
    {
        var parser = new EftLogParser();
        var history = new MemoryRaidHistory();
        var coordinator = Coordinator(history);

        var dead = await coordinator.ApplyEvidenceAsync(Read(parser, Confirmed("13:57:21", "TarkovStreets", "CX0FLS"), Launch1338), default);
        await coordinator.ApplyEvidenceAsync(Read(parser, GameStarted("13:58:54"), Launch1338), default);
        Assert.Equal("streets-of-tarkov", dead.MapId);

        // A later launch of the game. Its loading lines are not the dead raid doing anything.
        await coordinator.ApplyEvidenceAsync(Read(parser, LocationLoaded("16:34:40"), Launch1626), default);
        var next = await coordinator.ApplyEvidenceAsync(Read(parser, ProfileStatus("16:34:41", "Lighthouse", "CXK77H"), Launch1626), default);

        Assert.Equal(RaidLifecycleState.InRaid, next.State);
        Assert.Equal("lighthouse", next.MapId);
        Assert.Equal("CXK77H", next.RaidKey);
        Assert.NotEqual(dead.RaidId, next.RaidId);
        Assert.Equal(Local("16:34:41").AddMilliseconds(818), next.StartedUtc);
        Assert.Empty(next.PositionTrail);

        var row = Assert.Single(history.Rows, raid => raid.Id == dead.RaidId);
        Assert.Equal(RaidClosure.NotReportedOutcome, row.Outcome);
        // The last moment the dead raid itself showed activity, not when the next one was noticed.
        Assert.Equal(Local("13:58:54").AddMilliseconds(684), row.EndedUtc);
        Assert.Null(Assert.Single(history.Rows, raid => raid.Id == next.RaidId).EndedUtc);
    }

    /// <summary>
    /// The same dead raid through the durable outbox, which is how the running app records raids.
    /// </summary>
    /// <remarks>
    /// Every other test here writes straight to an in-memory history, which takes the coordinator's
    /// compatibility path. The outbox copies each command through its own reviewed codec, so the
    /// outcome, the notes and the end time have to survive that to reach Debrief at all.
    /// </remarks>
    [Fact]
    public async Task TheUnreportedEndReachesHistoryThroughTheDurableOutbox()
    {
        var parser = new EftLogParser();
        var history = new MemoryRaidHistory();
        await using var outbox = new RaidHistoryOutbox(history, store: new FixtureOutboxStore(capacity: 64));
        var coordinator = new RaidActivityCoordinator(
            new RaidStateService(),
            outbox,
            new StubProfileService(),
            new RuntimeStateStore(new(false, Offline: true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))));

        var dead = await coordinator.ApplyEvidenceAsync(Read(parser, Confirmed("13:57:21", "TarkovStreets", "CX0FLS"), Launch1338), default);
        await coordinator.ApplyEvidenceAsync(Read(parser, GameStarted("13:58:54"), Launch1338), default);
        var next = await coordinator.ApplyEvidenceAsync(Read(parser, ProfileStatus("16:34:41", "Lighthouse", "CXK77H"), Launch1626), default);
        await outbox.FlushAsync(default);

        Assert.Equal("lighthouse", next.MapId);
        var row = Assert.Single(history.Rows, raid => raid.Id == dead.RaidId);
        Assert.Equal(RaidClosure.NotReportedOutcome, row.Outcome);
        Assert.Equal(RaidClosure.NotReportedNotes, row.Notes);
        Assert.Equal(Local("13:58:54").AddMilliseconds(684), row.EndedUtc);
        Assert.Null(Assert.Single(history.Rows, raid => raid.Id == next.RaidId).EndedUtc);
    }

    /// <summary>
    /// [#891] Real, 2026-09-24 (build 1533): Lighthouse began at 04:19:52 on a PC four hours fast,
    /// Windows set the clock at 00:20:20, and the raid ended at 00:23:09, so it was stored ending
    /// 3 h 56 min before it began. The held times were already moved (#799); the row was not.
    /// </summary>
    [Fact]
    public async Task ARaidAcrossAClockStepIsStoredEndingAfterItBegan()
    {
        var history = new MemoryRaidHistory();
        await using var outbox = new RaidHistoryOutbox(history, store: new FixtureOutboxStore(capacity: 64));
        var coordinator = new RaidActivityCoordinator(
            new RaidStateService(),
            outbox,
            new StubProfileService(),
            new RuntimeStateStore(new(false, Offline: true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))));
        var realStart = new DateTimeOffset(2026, 9, 24, 0, 19, 52, TimeSpan.Zero);
        var fourHours = TimeSpan.FromHours(4);

        var begun = await coordinator.ApplyEvidenceAsync(
            new RaidEvidence(RaidEvidenceKind.LogLine, realStart + fourHours, "lighthouse", RaidLifecycleState.InRaid, new Confidence(0.9), "test"),
            default);
        await coordinator.RebaseClockAsync(-fourHours, default);
        await coordinator.ApplyEvidenceAsync(
            new RaidEvidence(RaidEvidenceKind.LogLine, realStart.AddSeconds(197), "lighthouse", RaidLifecycleState.PostRaid, new Confidence(0.9), "test"),
            default);
        await outbox.FlushAsync(default);

        var row = Assert.Single(history.Rows, raid => raid.Id == begun.RaidId);
        Assert.Equal(realStart, row.StartedUtc);
        Assert.Equal(realStart.AddSeconds(197), row.EndedUtc);
    }

    [Fact]
    public async Task TwoRaidsBackToBackOnTheSameMapAreTwoRaidsEvenWhenTheFirstWasNeverReportedOver()
    {
        var parser = new EftLogParser();
        var history = new MemoryRaidHistory();
        var coordinator = Coordinator(history);

        var first = await coordinator.ApplyEvidenceAsync(Read(parser, Confirmed("19:37:06", "Lighthouse", "CX86JJ", Scav), Launch1936), default);
        // No userMatchOver for it this time, and the second raid is seen by its profileStatus.
        var second = await coordinator.ApplyEvidenceAsync(Read(parser, ProfileStatus("20:12:20", "Lighthouse", "CX8L0J"), Launch1936), default);

        Assert.Equal("lighthouse", second.MapId);
        Assert.NotEqual(first.RaidId, second.RaidId);
        Assert.Equal(RaidClosure.NotReportedOutcome, Assert.Single(history.Rows, raid => raid.Id == first.RaidId).Outcome);
    }

    /// <summary>
    /// What the player saw: Lighthouse, a look at Shoreline by hand, Lighthouse again, and a map
    /// that stayed on Shoreline because "lighthouse" was the map id it had already followed.
    /// </summary>
    [Fact]
    public void TheMapFollowsTheSecondRaidOnTheSameMapAfterThePlayerLookedElsewhere()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        var follow = new RaidMapFollow();

        Assert.Equal("lighthouse", follow.Next(state.Apply(Read(parser, Confirmed("19:37:06", "Lighthouse", "CX86JJ", Scav), Launch1936))));
        // Still the same raid: the player may have opened another map, and is left alone.
        Assert.Null(follow.Next(state.Apply(Read(parser, GameStarted("19:38:30"), Launch1936))));
        Assert.Null(follow.Next(state.Apply(Read(parser, Over("19:48:01", "Lighthouse", "Free", "CX86JJ", Scav), Launch1936))));

        Assert.Equal("lighthouse", follow.Next(state.Apply(Read(parser, Confirmed("20:12:19", "Lighthouse", "CX8L0J", Scav), Launch1936))));
        Assert.Null(follow.Next(state.Apply(Read(parser, ProfileStatus("20:12:20", "Lighthouse", "CX8L0J"), Launch1936))));
    }

    /// <summary>
    /// CXWGQ9: confirmed at 14:08:11, the machine bugchecked, the game was relaunched three times
    /// and reconnected into the same raid, and the end arrived at 14:49:45 three launches later.
    /// </summary>
    [Fact]
    public void AReconnectWithTheSameShortIdIsTheSameRaid()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        var opened = state.Apply(Read(parser, Confirmed("14:08:11", "TarkovStreets", "CXWGQ9"), Launch1400));

        state.Apply(Read(parser, LocationLoaded("14:17:40"), Launch1415));
        var reconnected = state.Apply(Read(parser, ProfileStatus("14:17:41", "TarkovStreets", "CXWGQ9"), Launch1415));
        // A reconnect that is confirmed again carries a new event id and the same raid.
        var reconfirmed = state.Apply(Read(parser, Confirmed("14:17:42", "TarkovStreets", "CXWGQ9", eventId: "EV-RECONNECT"), Launch1415));

        Assert.Equal(opened.RaidId, reconnected.RaidId);
        Assert.Equal(opened.RaidId, reconfirmed.RaidId);
        Assert.Equal(opened.StartedUtc, reconfirmed.StartedUtc);
        Assert.Equal(Launch1415, reconfirmed.LogSession);

        var closed = state.Apply(Read(parser, Over("14:49:45", "TarkovStreets", "Free", "CXWGQ9"), Launch1441));
        Assert.Equal(RaidLifecycleState.PostRaid, closed.State);
        Assert.Equal(opened.RaidId, closed.RaidId);
    }

    [Fact]
    public void TheEndOfAnotherRaidDoesNotEndThisOne()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        state.Apply(Read(parser, Confirmed("16:53:02", "Shoreline", "CXKVBX"), Launch1626));

        var still = state.Apply(Read(parser, Over("16:53:03", "Lighthouse", "Free", "CXK77H", Scav), Launch1626));

        Assert.Equal(RaidLifecycleState.InRaid, still.State);
        Assert.Equal("shoreline", still.MapId);
    }

    /// <summary>
    /// 18:28 that evening, and again at 23:52: the companion started, replayed the newest folder,
    /// and announced a raid that was long dead. The game was not running the first time.
    /// </summary>
    [Fact]
    public async Task StartingHoursAfterADeadRaidWithNoGameRunningIsNotInARaid()
    {
        var folder = Replay(
            // In the order the files are replayed, which is not time order: application first.
            GameStarted("13:40:50"),
            ProfileStatus("13:57:30", "TarkovStreets", "CX0FLS"),
            GameStarted("13:58:54"),
            Confirmed("13:39:48", "Lighthouse", "CX00HG", Scav),
            Over("13:51:39", "Lighthouse", "Free", "CX00HG", Scav),
            Confirmed("13:57:21", "TarkovStreets", "CX0FLS"));

        var verdict = RaidReplayDecision.Decide(folder, gameIsRunning: false, Local("19:52:15"));

        Assert.False(verdict.IsLive);
        Assert.Equal("CX0FLS", verdict.Start?.RaidKey);
        Assert.Equal(Local("13:58:54").AddMilliseconds(684), verdict.LastSeenUtc);

        // And what the watcher hands on for it leaves the companion outside a raid, with the row
        // the last run opened closed as not reported at the last thing the game wrote.
        var history = new MemoryRaidHistory();
        var deadRow = new RaidHistoryEntry(Guid.NewGuid(), Guid.NewGuid(), "streets-of-tarkov", "Regular", Local("13:57:21"), null, null, null);
        history.Seed(deadRow);
        var coordinator = Coordinator(history);
        var current = await coordinator.ApplyEvidenceAsync(
            new RaidEvidence(RaidEvidenceKind.LogLine, Local("19:52:15"), null, null, new Confidence(0.98), verdict.Reason)
            {
                ResumesSession = true,
                EndsUnreported = true,
                RaidLastSeenUtc = verdict.LastSeenUtc,
            },
            default);

        Assert.NotEqual(RaidLifecycleState.InRaid, current.State);
        Assert.Null(current.MapId);
        var closed = Assert.Single(history.Rows);
        Assert.Equal(RaidClosure.NotReportedOutcome, closed.Outcome);
        Assert.Equal(verdict.LastSeenUtc, closed.EndedUtc);
    }

    [Fact]
    public void ARaidOlderThanAnyRaidLastsIsNotResumedEvenWithTheGameRunning()
    {
        var folder = Replay(Confirmed("13:57:21", "TarkovStreets", "CX0FLS"), GameStarted("13:58:54"));

        Assert.True(RaidReplayDecision.Decide(folder, gameIsRunning: true, Local("14:40:00")).IsLive);
        Assert.False(RaidReplayDecision.Decide(folder, gameIsRunning: true, Local("15:00:00")).IsLive);
    }

    /// <summary>The 23:52 case: the folder held the raid's end, and the old replay still resurrected it.</summary>
    [Fact]
    public void AFolderWhoseLastRaidWasReportedOverLeavesNoRaidToResume()
    {
        var folder = Replay(
            ProfileStatus("19:37:10", "Lighthouse", "CX86JJ"),
            GameStarted("19:38:30"),
            Confirmed("19:37:06", "Lighthouse", "CX86JJ", Scav),
            Over("19:48:01", "Lighthouse", "Free", "CX86JJ", Scav),
            // The same raid again from the second file, which is read after the end above.
            ProfileStatus("19:37:10", "Lighthouse", "CX86JJ"),
            GameStarted("19:38:30"));

        var verdict = RaidReplayDecision.Decide(folder, gameIsRunning: true, Local("19:52:15"));

        Assert.Null(verdict.Start);
        Assert.False(verdict.IsLive);
    }

    [Fact]
    public void StartingDuringAGenuinelyLiveRaidIsInThatRaidOnItsMapFromWhenItBegan()
    {
        var folder = Replay(
            LocationLoaded("20:12:40"),
            ProfileStatus("20:12:41", "Lighthouse", "CX8L0J"),
            GameStarted("20:14:50"),
            Confirmed("19:37:06", "Lighthouse", "CX86JJ", Scav),
            Over("19:48:01", "Lighthouse", "Free", "CX86JJ", Scav),
            Confirmed("20:12:19", "Lighthouse", "CX8L0J", Scav));

        var verdict = RaidReplayDecision.Decide(folder, gameIsRunning: true, Local("20:16:00"));

        Assert.True(verdict.IsLive);
        Assert.Equal("lighthouse", verdict.Start?.MapId);
        Assert.Equal("CX8L0J", verdict.Start?.RaidKey);
        Assert.Equal(Local("20:12:19"), verdict.StartedUtc);

        var state = new RaidStateService();
        var resumed = state.Apply(new RaidEvidence(
            RaidEvidenceKind.LogLine, Local("20:16:00"), verdict.Start!.MapId, RaidLifecycleState.InRaid, new Confidence(0.98), verdict.Reason)
        {
            RaidKey = verdict.Start.RaidKey,
            RaidStartedUtc = verdict.StartedUtc,
            ResumesSession = true,
        });
        Assert.Equal(RaidLifecycleState.InRaid, resumed.State);
        Assert.Equal("lighthouse", resumed.MapId);
        Assert.Equal(Local("20:12:19"), resumed.StartedUtc);
    }

    /// <summary>A reconnect folder holds no confirmation at all: only the loading lines and the id.</summary>
    [Fact]
    public void AReconnectFolderWithTheGameRunningIsALiveRaid()
    {
        var folder = Replay(LocationLoaded("20:14:28"), ProfileStatus("20:14:28", "Lighthouse", "CX8L0J"), GameStarted("20:16:37"));

        var verdict = RaidReplayDecision.Decide(folder, gameIsRunning: true, Local("20:17:00"));

        Assert.True(verdict.IsLive);
        Assert.Equal("lighthouse", verdict.Start?.MapId);
    }

    [Fact]
    public async Task AnOpenRaidOlderThanTheLongestRaidEndsItselfAsNotReported()
    {
        var parser = new EftLogParser();
        var history = new MemoryRaidHistory();
        var clock = new ManualTimeProvider(Local("13:57:21"));
        var coordinator = Coordinator(history, clock);
        var raid = await coordinator.ApplyEvidenceAsync(Read(parser, Confirmed("13:57:21", "TarkovStreets", "CX0FLS"), Launch1338), default);
        await coordinator.ApplyEvidenceAsync(Read(parser, GameStarted("13:58:54"), Launch1338), default);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(await coordinator.ExpireOverdueRaidAsync(default));
        Assert.Equal(RaidLifecycleState.InRaid, coordinator.Current.State);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await coordinator.ExpireOverdueRaidAsync(default));

        Assert.Equal(RaidLifecycleState.PostRaid, coordinator.Current.State);
        var row = Assert.Single(history.Rows, entry => entry.Id == raid.RaidId);
        Assert.Equal(RaidClosure.NotReportedOutcome, row.Outcome);
        Assert.Equal(Local("13:58:54").AddMilliseconds(684), row.EndedUtc);
    }

    /// <summary>Debrief marks the companion's own text as inferred, and a hand correction as manual.</summary>
    [Fact]
    public void TheNotReportedOutcomeIsTheCompanionsOwnAndCanBeCorrectedByHand()
    {
        var raid = new RaidHistoryEntry(
            Guid.NewGuid(), Guid.NewGuid(), "streets-of-tarkov", "Regular", Local("13:57:21"), Local("13:59:11"),
            RaidClosure.NotReportedOutcome, RaidClosure.NotReportedNotes);

        var written = RaidFactRules.Classify(raid, []);
        Assert.Equal(RaidFactKind.Inferred, written.Outcome);
        Assert.Equal(RaidFactKind.Inferred, written.Ended);

        var correction = RaidCorrection.Between(raid, "Died", raid.Notes, Local("20:30:00"));
        Assert.NotNull(correction);
        var corrected = RaidFactRules.Classify(raid with { Outcome = "Died" }, [correction]);
        Assert.Equal(RaidFactKind.Manual, corrected.Outcome);
        Assert.Equal(RaidFactKind.Inferred, corrected.Ended);
    }

    private static RaidActivityCoordinator Coordinator(MemoryRaidHistory history, TimeProvider? clock = null) => new(
        new RaidStateService(),
        history,
        new StubProfileService(),
        new RuntimeStateStore(new(false, Offline: true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))),
        mapDataService: null,
        clock);

    /// <remarks>
    /// Locked, because the durable outbox keeps order within a raid and not between raids, so two
    /// raids' rows can be written from two threads at once. An unlocked list lost one of them in
    /// about one run in ten, which read exactly like the product losing a raid.
    /// </remarks>
    private sealed class MemoryRaidHistory : IRaidHistoryService
    {
        private readonly object _gate = new();
        private readonly List<RaidHistoryEntry> _rows = [];

        /// <summary>A copy, so a test never enumerates the list while the outbox writes to it.</summary>
        public IReadOnlyList<RaidHistoryEntry> Rows
        {
            get
            {
                lock (_gate)
                {
                    return [.. _rows];
                }
            }
        }

        public void Seed(RaidHistoryEntry raid)
        {
            lock (_gate)
            {
                _rows.Add(raid);
            }
        }

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _rows.Add(raid);
            }

            return Task.FromResult(raid.Id);
        }

        public Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var index = _rows.FindIndex(raid => raid.Id == raidId);
                if (index >= 0)
                {
                    _rows[index] = _rows[index] with { EndedUtc = endUtc, Outcome = outcome, Notes = notes };
                }
            }

            return Task.CompletedTask;
        }

        public Task RebaseStartAsync(Guid raidId, DateTimeOffset startUtc, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var index = _rows.FindIndex(raid => raid.Id == raidId);
                if (index >= 0)
                {
                    _rows[index] = _rows[index] with { StartedUtc = startUtc };
                }
            }

            return Task.CompletedTask;
        }

        public Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Rows);

        public Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult<RaidManualMetadata?>(null);

        public Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(Guid raidId, string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(string mapId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubProfileService : IPlayerProfileService
    {
        private static readonly PlayerProfile Profile = new(
            Guid.Parse("6f1c1f4a-0b6e-4a0f-9c2f-2a7a3c4d5e6f"),
            "Raid identity profile",
            GameMode.Regular,
            1,
            Faction.Usec,
            null,
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(Profile);
    }
}
