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

    [Fact]
    public void RaidStateRetainsEvidenceAndRejectsOutOfOrderUpdates()
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
        var stale = service.Apply(new(
            RaidEvidenceKind.LogLine,
            started.AddSeconds(-1),
            null,
            RaidLifecycleState.Menu,
            Confidence.Certain,
            "late delivery"));

        Assert.NotNull(active.RaidId);
        Assert.Equal(started, active.StartedUtc);
        Assert.Equal(RaidLifecycleState.InRaid, stale.State);
        Assert.Equal("customs", stale.MapId);
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
