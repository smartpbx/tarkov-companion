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
