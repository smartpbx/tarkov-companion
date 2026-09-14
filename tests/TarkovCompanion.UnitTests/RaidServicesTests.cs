using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class RaidServicesTests
{
    [Fact]
    public void FixtureLogProducesConservativeRaidLifecycleEvidence()
    {
        var parser = new EftLogParser();
        var observedUtc = new DateTimeOffset(2026, 9, 4, 22, 35, 0, TimeSpan.Zero);
        var evidence = File.ReadLines(Fixture("fixtures/logs/raid-session.log.fixture"))
            .Select(line => parser.ParseLine(line, observedUtc))
            .Where(item => item is not null)
            .Cast<RaidEvidence>()
            .ToArray();

        // The fixture is a real session: profile selected, matching, location loaded, the
        // map-bearing profileStatus line, game started, then the raid-over notification.
        Assert.Equal(RaidLifecycleState.LoadingRaid, evidence[0].SuggestedState);
        Assert.Equal(RaidLifecycleState.InRaid, evidence[1].SuggestedState);
        Assert.Equal("customs", evidence[1].MapId);
        Assert.Equal(RaidLifecycleState.PostRaid, evidence[^1].SuggestedState);
        Assert.Equal("customs", evidence[^1].MapId);
    }

    /// <summary>
    /// Evidence keeps what an earlier line established when it says nothing itself.
    /// </summary>
    [Fact]
    public void RaidStateRetainsWhatLaterEvidenceDoesNotRestate()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 4, 22, 0, 0, TimeSpan.Zero);
        var active = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "fixture"));
        var later = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddSeconds(30),
            null,
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "still running"));

        Assert.NotNull(active.RaidId);
        Assert.Equal(started, active.StartedUtc);
        Assert.Equal(RaidLifecycleState.InRaid, later.State);
        Assert.Equal("customs", later.MapId);
    }

    /// <summary>
    /// Adopting makes the running raid the one already on record.
    /// </summary>
    /// <remarks>
    /// This service is memory, so a companion restarted mid-raid gives the raid a new identity
    /// and an empty trail. Whether this raid is that raid is a question about what was
    /// recorded, so the answer is handed in; what this does is make the two the same raid.
    /// </remarks>
    [Fact]
    public void AnAdoptedRaidKeepsTheIdentityAndTrailAlreadyRecorded()
    {
        var service = new RaidStateService();
        var recorded = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 9, 14, 2, 0, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddMinutes(11),
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.80),
            "A raid was already running when the companion started."));

        var adopted = service.Adopt(recorded, started, [Position(started.AddMinutes(4))]);

        Assert.Equal(recorded, adopted.RaidId);
        Assert.Equal(started, adopted.StartedUtc);
        Assert.Single(adopted.PositionTrail);
        Assert.Equal("screenshot.png", adopted.LastKnownPosition?.Filename);
    }

    /// <summary>
    /// Nothing to take over outside a raid, and taking over anyway would poison the next one.
    /// </summary>
    [Fact]
    public void AdoptingIntoTheMenuDoesNothing()
    {
        var service = new RaidStateService();

        var adopted = service.Adopt(Guid.NewGuid(), DateTimeOffset.UnixEpoch, []);

        Assert.Null(adopted.RaidId);
        Assert.Empty(adopted.PositionTrail);
    }

    /// <summary>
    /// A recorded raid with no start time is still this raid.
    /// </summary>
    /// <remarks>
    /// Inventing one from the restart would date the raid to the moment the companion came
    /// back, which is the mistake adoption exists to undo.
    /// </remarks>
    [Fact]
    public void AdoptingARaidWithNoRecordedStartKeepsTheOneThisRunWorkedOut()
    {
        var service = new RaidStateService();
        var noticed = new DateTimeOffset(2026, 9, 14, 2, 11, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            noticed,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.80),
            "A raid was already running when the companion started."));

        var adopted = service.Adopt(Guid.NewGuid(), null, []);

        Assert.Equal(noticed, adopted.StartedUtc);
    }

    /// <summary>
    /// A clock that steps backwards must not stop the raid being tracked.
    /// </summary>
    /// <remarks>
    /// This asserted the opposite until a live machine's clock moved back four hours during a
    /// session. Evidence older than the raid clock was discarded, which on that step would
    /// have silently dropped four hours of raids while everything else kept working, because
    /// discarding is what the guard was built to do. A time-zone correction or an NTP resync
    /// does this, so it is an ordinary event rather than a strange one.
    ///
    /// What the guard was protecting against cannot happen on this path: log evidence arrives
    /// from one sequential loop, so arrival order is the log's own order.
    /// </remarks>
    [Fact]
    public void KeepsTrackingTheRaidWhenTheSystemClockStepsBackwards()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 4, 22, 0, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "raid running"));

        var afterTheStep = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddHours(-4),
            "customs",
            RaidLifecycleState.PostRaid,
            new Confidence(0.98),
            "the game reported the raid as over"));

        Assert.Equal(RaidLifecycleState.PostRaid, afterTheStep.State);
        // The clock the player reads never rewinds, even though the evidence behind it did.
        Assert.Equal(started, afterTheStep.UpdatedUtc);
    }

    /// <summary>
    /// A raid writes log lines constantly, so the screenshot is never the newest thing.
    /// </summary>
    /// <remarks>
    /// This is the defect that made the position feature look dead on a live installation: the
    /// player pressed the screenshot key mid-raid, the file was read correctly, and the
    /// position was then dropped because a log line from two seconds later had already moved
    /// the raid clock past it.
    /// </remarks>
    [Fact]
    public void KeepsAPositionTakenBeforeTheLatestLogLine()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "streets",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "raid running"));
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddMinutes(5),
            "streets",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "still running"));

        var snapshot = service.ApplyPosition(Position(started.AddMinutes(4)));

        Assert.NotNull(snapshot.LastKnownPosition);
        Assert.Equal(80.02, snapshot.LastKnownPosition.Position.X, 3);
        Assert.Equal(RaidLifecycleState.InRaid, snapshot.State);
        // The raid clock still belongs to the newer log line rather than to the screenshot.
        Assert.Equal(started.AddMinutes(5), snapshot.UpdatedUtc);
    }

    [Fact]
    public void RefusesAPositionOlderThanTheOneAlreadyRecorded()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        service.ApplyPosition(Position(started.AddMinutes(4)));

        var snapshot = service.ApplyPosition(Position(started.AddMinutes(1)) with
        {
            Position = new(0, 0, 0),
        });

        Assert.NotNull(snapshot.LastKnownPosition);
        Assert.Equal(80.02, snapshot.LastKnownPosition.Position.X, 3);
    }

    private static ScreenshotPosition Position(DateTimeOffset takenUtc) => new(
        takenUtc,
        new WorldPosition(80.02, 1.39, -51.06),
        new QuaternionOrientation(0, 0, 0, 1),
        0,
        null,
        null,
        "screenshot.png");

    /// <summary>
    /// How the side was established travels with the side itself.
    /// </summary>
    /// <remarks>
    /// Two routes exist and they are not equally strong: the profile that ran the raid is an
    /// inference, a transfer on the ending notification is proof. The summary describes
    /// whichever applied, so the basis has to survive the trip through raid state rather than
    /// being reconstructed by whoever displays it.
    /// </remarks>
    [Fact]
    public void CarriesHowTheSideWasEstablishedAlongsideIt()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

        var snapshot = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "streets-of-tarkov",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "The game confirmed a scav raid on streets-of-tarkov.")
        {
            Side = "scav",
            SideBasis = "Inferred from which profile ran the raid.",
        });

        Assert.Equal("scav", snapshot.Side);
        Assert.Equal("Inferred from which profile ran the raid.", snapshot.SideBasis);
    }

    /// <summary>
    /// Evidence that cannot tell the side does not overwrite what an earlier line established.
    /// </summary>
    [Fact]
    public void KeepsTheSideAndItsBasisWhenALaterLineCannotTell()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "streets-of-tarkov",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "confirmed")
        {
            Side = "scav",
            SideBasis = "Inferred from which profile ran the raid.",
        });

        var snapshot = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddMinutes(1),
            null,
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "still running"));

        Assert.Equal("scav", snapshot.Side);
        Assert.Equal("Inferred from which profile ran the raid.", snapshot.SideBasis);
    }

    /// <summary>
    /// A raid does not go back to loading, however the log lines are interleaved.
    /// </summary>
    /// <remarks>
    /// Measured on a live machine: nine transitions in two seconds, five of them inside one
    /// second and some two hundred microseconds apart, as a buffered batch of lines was
    /// applied one at a time and each flipped the state. Anything that fires on entering a
    /// state fired repeatedly, and the raid came to rest in whichever state ended the batch.
    /// </remarks>
    [Fact]
    public void DoesNotFallBackToLoadingWhileTheRaidIsRunning()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "streets-of-tarkov",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "The game confirmed a raid on streets-of-tarkov."));

        // The interleaving that caused the thrash, applied line by line as it arrives.
        for (var line = 0; line < 5; line++)
        {
            service.Apply(new(
                RaidEvidenceKind.LogLine,
                started.AddMilliseconds(line),
                null,
                RaidLifecycleState.LoadingRaid,
                new Confidence(0.90),
                "Log indicates LoadingRaid."));
        }

        var snapshot = service.Current;
        Assert.Equal(RaidLifecycleState.InRaid, snapshot.State);
        // The raid is the same one throughout, so nothing that keys on it restarts.
        Assert.Equal("streets-of-tarkov", snapshot.MapId);
        Assert.Equal(started, snapshot.StartedUtc);
    }

    /// <summary>
    /// Loading still means something when no raid is running, which is when it is reported.
    /// </summary>
    [Fact]
    public void StillEntersLoadingFromOutsideARaid()
    {
        var service = new RaidStateService();

        var snapshot = service.Apply(new(
            RaidEvidenceKind.LogLine,
            new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero),
            null,
            RaidLifecycleState.LoadingRaid,
            new Confidence(0.90),
            "Log indicates LoadingRaid."));

        Assert.Equal(RaidLifecycleState.LoadingRaid, snapshot.State);
    }

    /// <summary>
    /// The trail is where the player has been this raid, in the order they were there.
    /// </summary>
    [Fact]
    public void RecordsEveryPositionOfTheRaidInOrder()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

        service.ApplyPosition(Position(started) with { Filename = "one.png" });
        service.ApplyPosition(Position(started.AddMinutes(2)) with
        {
            Position = new(90, 1.4, -40),
            Filename = "two.png",
        });

        var trail = service.Current.PositionTrail;
        Assert.Equal(2, trail.Count);
        Assert.Equal(80.02, trail[0].Position.X, 3);
        Assert.Equal(90, trail[1].Position.X, 3);
    }

    /// <summary>
    /// The same screenshot read twice is one place the player stood, not two.
    /// </summary>
    /// <remarks>
    /// The watcher can report a file more than once, and a trail that doubled its points every
    /// time would draw a line that went nowhere.
    /// </remarks>
    [Fact]
    public void DoesNotRepeatAPositionReadTwice()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

        service.ApplyPosition(Position(started) with { Filename = "one.png" });
        service.ApplyPosition(Position(started) with { Filename = "one.png" });

        Assert.Single(service.Current.PositionTrail);
    }

    /// <summary>
    /// A trail belongs to the raid it was walked in.
    /// </summary>
    /// <remarks>
    /// Carrying it into the next raid would draw a line across a map the player has left, and
    /// on a different map entirely it would be drawn somewhere meaningless.
    /// </remarks>
    [Fact]
    public void StartsANewTrailWithTheNextRaid()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        service.ApplyPosition(Position(started) with { Filename = "one.png" });

        // The game's own confirmation, which is what begins a raid. A loading marker would
        // not, because those are ignored while a raid is believed to be running.
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddMinutes(20),
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "The game confirmed a raid on customs.")
        {
            StartsNewRaid = true,
        });

        Assert.Empty(service.Current.PositionTrail);
        Assert.Null(service.Current.LastKnownPosition);
        Assert.Equal("customs", service.Current.MapId);
    }

    /// <summary>
    /// A raid beginning before the last one was seen to end is still a new raid.
    /// </summary>
    /// <remarks>
    /// Ignoring loading markers during a raid stopped the state flapping, but it also meant
    /// nothing was left to mark a second raid starting when the first one's end was missed.
    /// The game's own confirmation carries that, so it is what this turns on.
    /// </remarks>
    [Fact]
    public void StartsAFreshRaidWhenTheGameConfirmsOneWhileAnotherIsBelievedRunning()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        var first = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started,
            "streets-of-tarkov",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "The game confirmed a raid on streets-of-tarkov.")
        {
            StartsNewRaid = true,
            EventId = "EVENT_1",
        });

        var second = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddMinutes(30),
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.98),
            "The game confirmed a raid on customs.")
        {
            StartsNewRaid = true,
            EventId = "EVENT_2",
        });

        Assert.NotEqual(first.RaidId, second.RaidId);
        Assert.Equal("customs", second.MapId);
        Assert.Equal(started.AddMinutes(30), second.StartedUtc);
    }

    /// <summary>
    /// The same confirmation delivered twice is one raid, not two.
    /// </summary>
    /// <remarks>
    /// Every notification is written into two log files, so the companion sees each one twice.
    /// Without the event id the second copy started a second raid and discarded the identity,
    /// start time and trail the first had just created.
    /// </remarks>
    [Fact]
    public void TreatsARepeatedConfirmationAsTheSameRaid()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        var first = service.Apply(Confirmation(started, "streets-of-tarkov", "EVENT_1"));
        service.ApplyPosition(Position(started.AddMinutes(1)) with { Filename = "one.png" });

        var repeat = service.Apply(Confirmation(started.AddMilliseconds(40), "streets-of-tarkov", "EVENT_1"));

        Assert.Equal(first.RaidId, repeat.RaidId);
        Assert.Equal(started, repeat.StartedUtc);
        Assert.Single(repeat.PositionTrail);
    }

    /// <summary>A different confirmation is a different raid, however soon it arrives.</summary>
    [Fact]
    public void TreatsADifferentConfirmationAsANewRaid()
    {
        var service = new RaidStateService();
        var started = new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);
        var first = service.Apply(Confirmation(started, "streets-of-tarkov", "EVENT_1"));

        var second = service.Apply(Confirmation(started.AddMinutes(30), "customs", "EVENT_2"));

        Assert.NotEqual(first.RaidId, second.RaidId);
        Assert.Equal("customs", second.MapId);
    }

    private static RaidEvidence Confirmation(DateTimeOffset observedUtc, string mapId, string eventId) => new(
        RaidEvidenceKind.LogLine,
        observedUtc,
        mapId,
        RaidLifecycleState.InRaid,
        new Confidence(0.98),
        $"The game confirmed a raid on {mapId}.")
    {
        StartsNewRaid = true,
        EventId = eventId,
    };

    [Fact]
    public void ProductionStateIgnoresSimulatorEvidence()
    {
        var evidence = new RaidEvidence(
            RaidEvidenceKind.Simulator,
            DateTimeOffset.UtcNow,
            "factory",
            RaidLifecycleState.InRaid,
            Confidence.Certain,
            "simulator");

        Assert.Equal(RaidLifecycleState.Unknown, new RaidStateService().Apply(evidence).State);
        Assert.Equal(RaidLifecycleState.InRaid, new RaidStateService(developerMode: true).Apply(evidence).State);
    }

    private static string Fixture(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate test fixture {relativePath}.");
    }
}
