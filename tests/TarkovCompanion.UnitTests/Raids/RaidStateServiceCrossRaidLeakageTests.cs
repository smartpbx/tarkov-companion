using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// A raid's extract scan, unmatched lines and transit offers must not survive into the next
/// raid when that raid is confirmed directly, without a LoadingRaid step in between.
/// </summary>
/// <remarks>
/// Every other per-raid field (the position trail, the raid clock, the HUD reading) was already
/// cleared on <c>enteringRaid</c> as well as <c>enteringNewRaid</c>. These three were only
/// cleared on <c>enteringNewRaid</c> or on returning to the menu, so a raid that began by an
/// extract-list bootstrap — the game confirming a raid is running before its LoadingRaid line
/// was ever seen — inherited whatever the previous raid had last scanned.
/// </remarks>
public sealed class RaidStateServiceCrossRaidLeakageTests
{
    private static readonly DateTimeOffset FirstRaidUtc = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnExtractScanDoesNotSurviveIntoARaidConfirmedDirectly()
    {
        var service = new RaidStateService();
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "First raid confirmed."));
        service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            FirstRaidUtc.AddMinutes(5),
            linesNotMatched: ["Bad line"],
            transits: ["Factory"]);

        // Back to the menu, then straight into a new raid on the strength of an extract-list
        // screenshot alone -- the same bootstrap ApplyExtracts already performs when nothing
        // else has said a raid is running yet.
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc.AddMinutes(30),
            null,
            RaidLifecycleState.Menu,
            new Confidence(0.9),
            "Raid ended."));
        var second = service.ApplyExtracts(
            [new ActiveExtract("extract:2", "Dorms", new Confidence(0.9), "extracts-scan")],
            FirstRaidUtc.AddMinutes(45));

        Assert.Equal("Dorms", Assert.Single(second.ActiveExtracts).Name);
        Assert.Empty(second.ExtractLinesNotMatched);
        Assert.Empty(second.Transits);
    }

    [Fact]
    public void AnExtractScanDoesNotSurviveAConfirmedRaidThatSkippedLoading()
    {
        var service = new RaidStateService();
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "First raid confirmed."));
        service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            FirstRaidUtc.AddMinutes(5),
            linesNotMatched: ["Bad line"],
            transits: ["Factory"]);
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc.AddMinutes(30),
            null,
            RaidLifecycleState.Menu,
            new Confidence(0.9),
            "Raid ended."));

        // The next raid is confirmed by a log line straight to InRaid -- StartsNewRaid is what
        // a real confirmation line carries -- never passing through LoadingRaid.
        var second = service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc.AddMinutes(40),
            "streets",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "Second raid confirmed.")
        {
            StartsNewRaid = true,
        });

        Assert.Empty(second.ActiveExtracts);
        Assert.Empty(second.ExtractLinesNotMatched);
        Assert.Empty(second.Transits);
    }

    /// <summary>
    /// A second confirmation of the raid already running -- the same event, written to both log
    /// files, or a mid-raid map transfer that keeps the same raid id -- must not clear what has
    /// already been scanned. Only a genuinely new raid does.
    /// </summary>
    [Fact]
    public void ARepeatConfirmationOfTheSameRaidKeepsItsExtractScan()
    {
        var service = new RaidStateService();
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "Raid confirmed.")
        {
            StartsNewRaid = true,
            EventId = "E1",
        });
        service.ApplyExtracts(
            [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
            FirstRaidUtc.AddMinutes(5),
            linesNotMatched: ["Bad line"],
            transits: ["Factory"]);

        // The second copy of the same notification, written to the other log file. Same event
        // id, so this is recognised as the raid already running rather than a new one.
        var repeated = service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstRaidUtc.AddMinutes(6),
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "Raid confirmed (second copy).")
        {
            StartsNewRaid = true,
            EventId = "E1",
        });

        Assert.Equal("Crossroads", Assert.Single(repeated.ActiveExtracts).Name);
        Assert.Equal(["Bad line"], repeated.ExtractLinesNotMatched);
        Assert.Equal(["Factory"], repeated.Transits);
    }
}
