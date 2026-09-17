using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// Covers the reported defect: after an extracts screenshot, the app showed the map's full
/// raid length instead of the actual time remaining.
/// </summary>
public sealed class RaidStateServiceApplyExtractsTests
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnExtractsScreenshotThatCannotReadTheClockLeavesTheStartTimeUnknown()
    {
        var service = new RaidStateService();

        // No log evidence has been seen yet: the extracts screen is the first thing telling
        // this companion a raid is running, and this particular scan could not read the clock
        // panel (raidClock: null), the way a partial OCR read plausibly would.
        var snapshot = service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            ObservedUtc,
            raidClock: null);

        Assert.Equal(RaidLifecycleState.InRaid, snapshot.State);
        // The old behaviour stamped StartedUtc = observedUtc here, which made RaidTimer count
        // down from the map's full length a moment later — a confident, wrong reading. Admitting
        // the start time is unknown is the honest alternative.
        Assert.Null(snapshot.StartedUtc);
        Assert.Null(snapshot.RaidClock);

        var remaining = RaidTimer.Resolve(
            null,
            snapshot.StartedUtc,
            TimeSpan.FromMinutes(40),
            ObservedUtc.AddMinutes(1));
        Assert.Equal(RaidTimeBasis.Unknown, remaining.Basis);
        Assert.Null(remaining.Remaining);
    }

    [Fact]
    public void AnExtractsScreenshotThatDoesReadTheClockKeepsThatReading()
    {
        var service = new RaidStateService();

        var snapshot = service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            ObservedUtc,
            raidClock: TimeSpan.FromMinutes(12));

        Assert.Equal(TimeSpan.FromMinutes(12), snapshot.RaidClock);
        Assert.Equal(ObservedUtc, snapshot.RaidClockReadUtc);

        var remaining = RaidTimer.Resolve(
            (snapshot.RaidClock!.Value, snapshot.RaidClockReadUtc!.Value),
            snapshot.StartedUtc,
            TimeSpan.FromMinutes(40),
            ObservedUtc.AddMinutes(1));
        Assert.Equal(RaidTimeBasis.Observed, remaining.Basis);
        Assert.Equal(TimeSpan.FromMinutes(11), remaining.Remaining);
    }

    [Fact]
    public void AlreadyInRaidKeepsItsKnownStartTimeWhenALaterScreenshotMissesTheClock()
    {
        var service = new RaidStateService();
        service.Apply(new RaidEvidence(
            RaidEvidenceKind.LogLine,
            ObservedUtc,
            "bigmap",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "Log confirmation names a real start time."));

        var snapshot = service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            ObservedUtc.AddMinutes(5),
            raidClock: null);

        // Already in raid on real log evidence: this call must not disturb the known start.
        Assert.Equal(ObservedUtc, snapshot.StartedUtc);
    }
}
