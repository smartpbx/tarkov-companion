using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether a squadmate's reading of the extract screen may be used as your own.
/// </summary>
/// <remarks>
/// One player photographs the extract list and gains the offered exits, the transits and the
/// raid clock. The other four see ten possible exits and "counted from the raid's start".
///
/// This is the riskiest sharing in the application, and for a stated reason: <b>"the offered
/// exits are the same for everybody in a PMC party" is game knowledge, not something the
/// research documents measured.</b> Every test here is a way of making being wrong about that
/// cost as little as possible.
/// </remarks>
public sealed class SharedExtractsTests
{
    [Fact]
    public void A_squadmate_who_scanned_the_screen_answers_for_somebody_who_has_not()
    {
        var reading = SharedExtracts.Choose(Raid(), [Member("Geo")]);

        Assert.Equal("Geo", reading!.From);
        Assert.Equal(["ZB-014", "Outskirts"], reading.Extracts);
    }

    [Fact]
    public void Your_own_scan_always_wins()
    {
        // A reading of your own screen is about your own raid. Everything shared is an
        // inference from somebody else's.
        var mine = Raid() with
        {
            ActiveExtracts = [new("zb-014", "ZB-014", Confidence.Certain, "scan")],
        };

        Assert.Null(SharedExtracts.Choose(mine, [Member("Geo")]));
    }

    [Fact]
    public void A_squadmate_on_another_map_is_not_telling_you_about_yours()
    {
        // The failure this is most likely to produce, and it would look convincing: a correct
        // list for the wrong map is indistinguishable on screen from a correct one.
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", mapId: "woods")]));
    }

    [Fact]
    public void A_scav_neither_gives_nor_takes()
    {
        // Their exit list genuinely differs, which is the one case where the premise this
        // whole feature rests on is known to be false.
        Assert.Null(SharedExtracts.Choose(Raid() with { Side = "scav" }, [Member("Geo")]));
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", side: "scav")]));
    }

    [Fact]
    public void An_unstated_side_is_a_mismatch_rather_than_a_match()
    {
        // Strict in the direction that costs nothing. Accepting an unknown is accepting a guess
        // about the one thing that decides whether the list applies at all.
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", side: null)]));
        Assert.Null(SharedExtracts.Choose(Raid() with { Side = null }, [Member("Geo")]));
    }

    [Fact]
    public void The_freshest_reading_wins()
    {
        var reading = SharedExtracts.Choose(
            Raid(),
            [
                Member("Older", age: TimeSpan.FromMinutes(9)),
                Member("Newer", age: TimeSpan.FromMinutes(1)),
            ]);

        Assert.Equal("Newer", reading!.From);
    }

    [Fact]
    public void A_reading_nobody_dated_is_not_used()
    {
        // Without an age there is no way to say how old the clock is, and an unlabelled clock
        // is exactly the thing this is careful about.
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", undated: true)]));
    }

    [Fact]
    public void A_reading_older_than_a_quarter_of_an_hour_is_let_go()
    {
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", age: TimeSpan.FromMinutes(20))]));
    }

    [Fact]
    public void The_clock_is_aged_forward_from_when_it_was_read()
    {
        // Their clock said thirty minutes four minutes ago, so it says twenty-six now.
        // Publishing what they read without ageing it presents a stale number as current.
        var reading = SharedExtracts.Choose(
            Raid(),
            [Member("Geo", age: TimeSpan.FromMinutes(4), clock: TimeSpan.FromMinutes(30))]);

        Assert.Equal(TimeSpan.FromMinutes(26), reading!.RaidClock);
    }

    [Fact]
    public void A_clock_that_has_run_out_does_not_go_backwards()
    {
        var reading = SharedExtracts.Choose(
            Raid(),
            [Member("Geo", age: TimeSpan.FromMinutes(10), clock: TimeSpan.FromMinutes(2))]);

        Assert.Equal(TimeSpan.Zero, reading!.RaidClock);
    }

    [Fact]
    public void Nothing_is_taken_outside_a_raid()
    {
        Assert.Null(SharedExtracts.Choose(Raid() with { State = RaidLifecycleState.Menu }, [Member("Geo")]));
    }

    [Fact]
    public void A_squadmate_who_has_scanned_nothing_offers_nothing()
    {
        Assert.Null(SharedExtracts.Choose(Raid(), [Member("Geo", extracts: [])]));
    }

    private static RaidSnapshot Raid() => new(
        Guid.NewGuid(),
        RaidLifecycleState.InRaid,
        "customs",
        DateTimeOffset.Parse("2026-09-14T04:00:00Z"),
        DateTimeOffset.Parse("2026-09-14T04:05:00Z"),
        Confidence.Certain,
        null,
        [],
        false)
    {
        Side = "pmc",
    };

    /// <summary>
    /// A squadmate who has scanned their extract screen.
    /// </summary>
    /// <remarks>
    /// The age defaults to something recent, so <paramref name="undated"/> is how a test asks
    /// for a member with no age at all — passing null read as "use the default" and silently
    /// tested the opposite of what it said.
    /// </remarks>
    private static GroupMemberView Member(
        string name,
        string? mapId = "customs",
        string? side = "pmc",
        TimeSpan? age = null,
        TimeSpan? clock = null,
        bool undated = false,
        IReadOnlyList<string>? extracts = null) => new(
        name,
        mapId,
        RaidLifecycleState.InRaid,
        side,
        null,
        null,
        null,
        [],
        [])
    {
        Extracts = extracts ?? ["ZB-014", "Outskirts"],
        Transits = [],
        RaidClock = clock,
        RaidClockAge = undated ? null : age ?? TimeSpan.FromMinutes(2),
    };
}
