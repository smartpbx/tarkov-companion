using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// Without log evidence to say where one raid ends and the next begins, screenshots alone have
/// to bound the trail: two close together are one raid, two a day apart are not.
/// </summary>
/// <remarks>
/// Filed for #391: a companion that could not read the game's logs never left
/// <see cref="RaidLifecycleState.InRaid"/> on its own, so every later screenshot only ever
/// extended the trail it already had. A screenshot backlog replayed at startup then drew
/// several unrelated raids' trails as one continuous walk.
/// </remarks>
public sealed class RaidStateServiceApplyPositionTests
{
    private static readonly DateTimeOffset FirstShotUtc = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoScreenshotsCloseTogetherShareOneTrail()
    {
        var service = new RaidStateService();
        var first = service.ApplyPosition(Position(FirstShotUtc, "shot-1.png"));
        var second = service.ApplyPosition(Position(FirstShotUtc.AddMinutes(20), "shot-2.png"));

        Assert.Equal(first.RaidId, second.RaidId);
        Assert.Equal(2, second.PositionTrail.Count);
    }

    [Fact]
    public void AGapWiderThanAnyRaidStartsAFreshTrailInstead()
    {
        var service = new RaidStateService();
        var first = service.ApplyPosition(Position(FirstShotUtc, "shot-1.png"));

        // A day later: no raid runs anywhere near this long, so this cannot be the same raid,
        // however close the two screenshots' filenames sort.
        var second = service.ApplyPosition(Position(FirstShotUtc.AddDays(1), "shot-2.png"));

        Assert.NotEqual(first.RaidId, second.RaidId);
        Assert.Equal("shot-2.png", Assert.Single(second.PositionTrail).Filename);
    }

    /// <summary>
    /// A stale auto-detected map must not follow a raid it was never confirmed to be on.
    /// </summary>
    [Fact]
    public void AFreshTrailFromAGapDoesNotKeepTheOldRaidsMap()
    {
        var service = new RaidStateService();
        service.Apply(new(
            RaidEvidenceKind.LogLine,
            FirstShotUtc,
            "customs",
            RaidLifecycleState.InRaid,
            new(0.95),
            "First raid confirmed."));
        service.ApplyPosition(Position(FirstShotUtc.AddMinutes(1), "shot-1.png"));

        var second = service.ApplyPosition(Position(FirstShotUtc.AddDays(1), "shot-2.png"));

        Assert.Null(second.MapId);
    }

    private static ScreenshotPosition Position(DateTimeOffset timestamp, string filename) => new(
        timestamp,
        new WorldPosition(1, 2, 3),
        new QuaternionOrientation(0, 0, 0, 1),
        0,
        null,
        null,
        filename);
}
