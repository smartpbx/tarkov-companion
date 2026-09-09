using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
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

        Assert.Equal(3, evidence.Length);
        Assert.Equal(RaidLifecycleState.LoadingRaid, evidence[0].SuggestedState);
        Assert.Equal("customs", evidence[0].MapId);
        Assert.Equal(RaidLifecycleState.InRaid, evidence[1].SuggestedState);
        Assert.Equal(RaidLifecycleState.PostRaid, evidence[2].SuggestedState);
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
