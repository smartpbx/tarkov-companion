using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#893] The Raid map rebuilds when what it draws of the group changed, not on every exchange.
/// </summary>
/// <remarks>
/// The group session publishes a new snapshot about three times a second with a squad, and the
/// cockpit rebuilt its whole scene for each one because it compared references: 195 rebuilds a
/// minute on the owner's logs, nearly all identical. Every test here that says "equal" fails
/// against that reference comparison, since each snapshot is a new instance.
/// </remarks>
public sealed class RaidGroupSceneSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnExchangeThatOnlyMovedTheClocksDrawsTheSameScene()
    {
        var first = Snapshot(Mate(positionAge: 2, since: 0.3), updated: Now);
        var second = Snapshot(Mate(positionAge: 2, since: 0.6), updated: Now.AddMilliseconds(310)) with
        {
            PositionLatency = GroupPositionLatencySnapshot.None,
        };

        Assert.NotSame(first, second);
        Assert.Equal(RaidGroupSceneSignature.Of(first), RaidGroupSceneSignature.Of(second));
    }

    /// <summary>The latency guarantee: a moved squadmate is never held back by this comparison.</summary>
    [Fact]
    public void AMovedSquadmateAlwaysChangesTheScene()
    {
        var before = Snapshot(Mate(x: 100, z: 200));
        var after = Snapshot(Mate(x: 101, z: 200));

        Assert.NotEqual(RaidGroupSceneSignature.Of(before), RaidGroupSceneSignature.Of(after));
    }

    [Fact]
    public void ATurnANewTrailPointAndAQuietMemberEachChangeTheScene()
    {
        var baseline = RaidGroupSceneSignature.Of(Snapshot(Mate()));

        Assert.NotEqual(baseline, RaidGroupSceneSignature.Of(Snapshot(Mate(heading: 90))));
        Assert.NotEqual(baseline, RaidGroupSceneSignature.Of(Snapshot(Mate() with { Trail = [new(1, 2, TimeSpan.FromSeconds(30))] })));
        Assert.NotEqual(baseline, RaidGroupSceneSignature.Of(Snapshot(Mate(since: 60))));
    }

    [Fact]
    public void ANewPingOrWaypointChangesTheScene()
    {
        var baseline = RaidGroupSceneSignature.Of(Snapshot(Mate()));
        var pinged = Snapshot(Mate()) with { Pings = [new(7, "Geo", "customs", 1, 0, 2, null, Now)] };
        var marked = Snapshot(Mate()) with { Waypoints = [new(8, "Geo", "customs", 1, 0, 2, "Loot", null)] };

        Assert.NotEqual(baseline, RaidGroupSceneSignature.Of(pinged));
        Assert.NotEqual(baseline, RaidGroupSceneSignature.Of(marked));
    }

    /// <summary>The marker's age text still moves on when its words would change.</summary>
    [Fact]
    public void TheAgeTextChangesTheSceneOnlyWhenItsWordsChange()
    {
        Assert.Equal(
            RaidGroupSceneSignature.Of(Snapshot(Mate(positionAge: 3))),
            RaidGroupSceneSignature.Of(Snapshot(Mate(positionAge: 9))));
        Assert.NotEqual(
            RaidGroupSceneSignature.Of(Snapshot(Mate(positionAge: 3))),
            RaidGroupSceneSignature.Of(Snapshot(Mate(positionAge: 20))));
    }

    [Fact]
    public void LosingTheRelayChangesTheScene()
    {
        var live = Snapshot(Mate());

        Assert.NotEqual(RaidGroupSceneSignature.Of(live), RaidGroupSceneSignature.Of(live with { StaleSince = Now }));
    }

    private static GroupSnapshot Snapshot(GroupMemberView member, DateTimeOffset? updated = null) =>
        new(true, [member], string.Empty, updated ?? Now);

    private static GroupMemberView Mate(
        double x = 100,
        double z = 200,
        double? heading = 45,
        double positionAge = 2,
        double since = 0.3) =>
        new("Geo", "customs", RaidLifecycleState.InRaid, "PMC", new WorldPosition(x, 0, z), heading, TimeSpan.FromSeconds(positionAge), [], [])
        {
            Since = TimeSpan.FromSeconds(since),
        };
}
