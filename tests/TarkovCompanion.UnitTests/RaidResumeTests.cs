using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Picking up a raid the companion was already recording when it stopped.
/// </summary>
/// <remarks>
/// The raid lives in memory. A companion that restarts mid-raid mints a new id and a new start
/// time, the row the last run opened keeps <c>end_utc NULL</c> for ever, and the map draws an
/// empty trail over screenshots already on disk. Velopack restarts this application whenever it
/// installs an update, which it checks for on every launch, so this is an ordinary evening
/// rather than a crash.
///
/// The risk runs the other way: adopting a row from a raid that ended while the companion was
/// closed would draw the last raid's trail across this one and date this raid to whenever that
/// one started. Most of what is pinned here is the refusals.
/// </remarks>
public sealed class RaidResumeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T02:00:00Z");

    [Fact]
    public void A_raid_still_running_on_this_map_is_adopted()
    {
        var resumption = RaidResume.Choose(
            [Open("customs", Now.AddMinutes(-12))],
            "customs",
            Now);

        Assert.NotNull(resumption.Adopt);
        Assert.Equal("customs", resumption.Adopt.MapId);
        Assert.Empty(resumption.Close);
    }

    [Fact]
    public void A_raid_on_another_map_is_closed_rather_than_adopted()
    {
        // The player went to Woods; the Customs row is from a raid that ended while the
        // companion was shut.
        var woods = Open("woods", Now.AddMinutes(-3));
        var customs = Open("customs", Now.AddMinutes(-12));

        var resumption = RaidResume.Choose([woods, customs], "woods", Now);

        Assert.Equal(woods.Id, resumption.Adopt?.Id);
        Assert.Equal([customs.Id], resumption.Close);
    }

    [Fact]
    public void A_raid_too_old_to_still_be_running_is_not_adopted()
    {
        // Longer ago than any raid lasts, so whatever this row is, it is not what is on
        // screen now.
        var stale = Open("customs", Now.AddHours(-4));

        var resumption = RaidResume.Choose([stale], "customs", Now);

        Assert.Null(resumption.Adopt);
        Assert.Equal([stale.Id], resumption.Close);
    }

    /// <summary>
    /// The bound is the map's own duration, because maps do not last the same length.
    /// </summary>
    [Fact]
    public void A_short_maps_raid_is_given_a_short_maps_bound()
    {
        var factory = Open("factory4_day", Now.AddMinutes(-40));

        Assert.Null(RaidResume.Choose([factory], "factory4_day", Now, TimeSpan.FromMinutes(20)).Adopt);
        Assert.NotNull(RaidResume.Choose([factory], "factory4_day", Now, TimeSpan.FromMinutes(50)).Adopt);
    }

    [Fact]
    public void A_raid_within_the_margin_past_its_duration_is_still_adopted()
    {
        // The stated duration is when the raid ends, not when the player leaves: extracting
        // takes a countdown and the notification arrives after it.
        var raid = Open("customs", Now.AddMinutes(-44));

        Assert.NotNull(RaidResume.Choose([raid], "customs", Now, TimeSpan.FromMinutes(40)).Adopt);
    }

    [Fact]
    public void A_raid_that_has_not_started_yet_is_not_adopted()
    {
        // A clock that stepped back leaves rows dated in the future. Whatever they are, this
        // session did not start after them.
        var future = Open("customs", Now.AddMinutes(20));

        var resumption = RaidResume.Choose([future], "customs", Now);

        Assert.Null(resumption.Adopt);
        Assert.Equal([future.Id], resumption.Close);
    }

    [Fact]
    public void A_raid_whose_map_is_unknown_adopts_nothing_and_closes_everything()
    {
        // Picking the newest open row on recency alone is exactly the mistake that draws the
        // last raid's trail over this one.
        var first = Open("customs", Now.AddMinutes(-12));
        var second = Open("woods", Now.AddMinutes(-4));

        var resumption = RaidResume.Choose([first, second], null, Now);

        Assert.Null(resumption.Adopt);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), resumption.Close.Order());
    }

    [Fact]
    public void The_newest_of_two_open_rows_on_one_map_is_the_one_adopted()
    {
        var older = Open("customs", Now.AddMinutes(-30));
        var newer = Open("customs", Now.AddMinutes(-6));

        var resumption = RaidResume.Choose([older, newer], "customs", Now);

        Assert.Equal(newer.Id, resumption.Adopt?.Id);
        Assert.Equal([older.Id], resumption.Close);
    }

    [Fact]
    public void A_finished_raid_is_neither_adopted_nor_closed_again()
    {
        var finished = Open("customs", Now.AddMinutes(-12)) with { EndedUtc = Now.AddMinutes(-2) };

        var resumption = RaidResume.Choose([finished], "customs", Now);

        Assert.Null(resumption.Adopt);
        Assert.Empty(resumption.Close);
    }

    [Fact]
    public void A_row_with_no_start_time_is_closed_rather_than_adopted()
    {
        // Nothing to bound it by, so it cannot be shown to be this raid.
        var undated = Open("customs", null);

        var resumption = RaidResume.Choose([undated], "customs", Now);

        Assert.Null(resumption.Adopt);
        Assert.Equal([undated.Id], resumption.Close);
    }

    [Fact]
    public void Nothing_open_is_nothing_to_do()
    {
        var resumption = RaidResume.Choose([], "customs", Now);

        Assert.Null(resumption.Adopt);
        Assert.Empty(resumption.Close);
    }

    [Fact]
    public void The_map_is_matched_the_way_the_catalog_spells_it_either_way()
    {
        var raid = Open("Customs", Now.AddMinutes(-12));

        Assert.NotNull(RaidResume.Choose([raid], "customs", Now).Adopt);
    }

    /// <summary>
    /// The conservative half of the same question, for a launch into the menu.
    /// </summary>
    /// <remarks>
    /// The resume only runs when a raid is in progress, and most launches are not. A player who
    /// crashed mid-raid on Tuesday and opened the companion to look at the flea on Wednesday
    /// left Tuesday's row open for the rest of the wipe.
    /// </remarks>
    [Fact]
    public void An_old_open_row_is_abandoned_whatever_map_it_is_on()
    {
        var stale = Open("customs", Now.AddHours(-4));

        Assert.Equal([stale.Id], RaidResume.Abandoned([stale], Now));
    }

    [Fact]
    public void A_row_recent_enough_to_be_resumed_is_left_for_the_resume_to_decide_about()
    {
        // This runs before observation starts, so closing a row the resume is about to adopt
        // would be a race this could never win.
        var running = Open("customs", Now.AddMinutes(-12));

        Assert.Empty(RaidResume.Abandoned([running], Now));
    }

    [Fact]
    public void A_row_with_no_start_time_is_abandoned()
    {
        var undated = Open("customs", null);

        Assert.Equal([undated.Id], RaidResume.Abandoned([undated], Now));
    }

    [Fact]
    public void A_row_dated_in_the_future_is_abandoned()
    {
        var future = Open("customs", Now.AddHours(2));

        Assert.Equal([future.Id], RaidResume.Abandoned([future], Now));
    }

    [Fact]
    public void A_finished_row_is_not_abandoned_again()
    {
        var finished = Open("customs", Now.AddHours(-4)) with { EndedUtc = Now.AddHours(-3) };

        Assert.Empty(RaidResume.Abandoned([finished], Now));
    }

    private static RaidHistoryEntry Open(string? mapId, DateTimeOffset? startedUtc) => new(
        Guid.NewGuid(),
        Guid.Parse("2c2f2a0d-6f1e-4a7f-9c5a-1f4f0c6ad2b1"),
        mapId,
        "Regular",
        startedUtc,
        null,
        null,
        null);
}
