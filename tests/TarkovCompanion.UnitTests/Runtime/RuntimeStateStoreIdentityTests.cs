using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

/// <summary>
/// A publication that leaves a slice alone hands out the same instance of it, which is what lets
/// a subscriber tell "the raid changed" from "something else was published".
/// </summary>
public sealed class RuntimeStateStoreIdentityTests
{
    [Fact]
    public void Publishing_an_unrelated_slice_keeps_the_raid_and_group_instances()
    {
        var store = Store();
        store.Update(current => current with { Raid = Raid(trail: 12), Group = Group(members: 3) });
        var raid = store.Current.Raid;
        var group = store.Current.Group;
        var trail = store.Current.Raid.PositionTrail;

        store.Update(current => current with { DatabaseReady = !current.DatabaseReady });
        store.Update(current => current with { RecentScreenshotNames = ["a.png"] });

        Assert.Same(raid, store.Current.Raid);
        Assert.Same(group, store.Current.Group);
        Assert.Same(trail, store.Current.Raid.PositionTrail);
    }

    [Fact]
    public void Publishing_the_raid_gives_a_new_raid_and_keeps_the_group()
    {
        var store = Store();
        store.Update(current => current with { Raid = Raid(trail: 4), Group = Group(members: 2) });
        var raid = store.Current.Raid;
        var group = store.Current.Group;

        store.Update(current => current with { Raid = current.Raid with { UpdatedUtc = current.Raid.UpdatedUtc.AddSeconds(1) } });

        Assert.NotSame(raid, store.Current.Raid);
        Assert.Same(group, store.Current.Group);
    }

    [Fact]
    public void A_slice_kept_by_identity_is_still_a_copy_the_publisher_cannot_change()
    {
        var store = Store();
        var trail = new List<ScreenshotPosition> { Shot(0) };
        store.Update(current => current with { Raid = Raid(trail: 0) with { PositionTrail = trail } });
        store.Update(current => current with { DatabaseReady = true });

        trail.Add(Shot(1));

        Assert.Single(store.Current.Raid.PositionTrail);
    }

    private static RuntimeStateStore Store() => new(new(
        DemoMode: false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(1),
        TimeSpan.FromSeconds(5)));

    internal static ScreenshotPosition Shot(int index) => new(
        new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero).AddSeconds(index * 30),
        new(index, 0, index),
        default,
        (index * 20) % 360,
        null,
        null,
        $"shot-{index}.png");

    internal static RaidSnapshot Raid(int trail) => new(
        Guid.Parse("00000000-0000-0000-0000-000000000042"),
        RaidLifecycleState.InRaid,
        "customs",
        new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
        new(0.9),
        trail > 0 ? Shot(trail - 1) : null,
        [],
        false)
    {
        Side = "PMC",
        PositionTrail = [.. Enumerable.Range(0, trail).Select(Shot)],
    };

    internal static GroupSnapshot Group(int members) => new(
        true,
        [.. Enumerable.Range(0, members).Select(index => new GroupMemberView(
            $"Member{index}",
            "customs",
            RaidLifecycleState.InRaid,
            "PMC",
            new(index, 0, index),
            90,
            TimeSpan.FromSeconds(10),
            [],
            [])
        {
            Trail = [.. Enumerable.Range(0, 10).Select(step => new GroupTrailPointView(step, step, TimeSpan.FromSeconds(step)))],
        })],
        "Sharing",
        new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
}
