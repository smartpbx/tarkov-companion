using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.PlayerTime;

/// <summary>
/// The one rule: a stored UTC instant is shown as the clock on the player's own wall.
/// </summary>
/// <remarks>
/// The reported case is 03:03:39 UTC, which the self-test printed as-is to a player at UTC-4 whose
/// clock read 23:03:39 the evening before. Every expectation below is for a zone that is neither UTC
/// nor the machine's, so none of them can pass on a UTC-configured CI box by coincidence.
/// </remarks>
public sealed class LocalTimeTests
{
    private static readonly DateTimeOffset Reported = new(2026, 9, 19, 3, 3, 39, TimeSpan.Zero);

    [Fact]
    public void The_pinned_zone_is_not_utc_so_a_utc_only_box_cannot_pass_by_coincidence()
    {
        using var pin = PlayerClock.Pin();

        Assert.NotEqual(TimeSpan.Zero, LocalTime.Zone.GetUtcOffset(Reported));
        Assert.NotEqual(Reported.ToString("g"), LocalTime.Moment(Reported));
    }

    [Fact]
    public void A_utc_instant_reads_as_the_clock_on_the_players_wall()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcMinusFour);

        Assert.Equal("09/18/2026 23:03", LocalTime.Moment(Reported));
        Assert.Equal("09/18/2026", LocalTime.Date(Reported));
        Assert.Equal("23:03:39", LocalTime.Time(Reported));
        Assert.Equal("23:03", LocalTime.ShortTime(Reported));
        Assert.Equal("2026-09-18 23:03", LocalTime.Sortable(Reported));
        Assert.Equal("2026-09-18 23:03:39", LocalTime.SortableSeconds(Reported));
        Assert.Equal("2026-09-18", LocalTime.SortableDate(Reported));
        Assert.Equal("20260918-230339", LocalTime.FileStamp(Reported));
        Assert.Equal("UTC-04:00", LocalTime.Offset(Reported));
    }

    [Fact]
    public void A_half_hour_zone_carries_its_offset_and_can_move_the_date_forward()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcPlusFiveThirty);
        var lateEvening = new DateTimeOffset(2026, 9, 19, 22, 30, 0, TimeSpan.Zero);

        Assert.Equal("2026-09-20 04:00", LocalTime.Sortable(lateEvening));
        Assert.Equal("2026-09-20", LocalTime.SortableDate(lateEvening));
        Assert.Equal("UTC+05:30", LocalTime.Offset(lateEvening));
    }

    [Fact]
    public void An_instant_given_at_any_offset_means_the_same_local_time()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcMinusFour);
        var sameInstantAtPlusTwo = Reported.ToOffset(TimeSpan.FromHours(2));

        Assert.Equal(LocalTime.SortableSeconds(Reported), LocalTime.SortableSeconds(sameInstantAtPlusTwo));
    }

    /// <summary>
    /// The machine-readable form is local for a person and exact for a program: the offset is part
    /// of the string, so it parses back to the very instant that was stored.
    /// </summary>
    [Fact]
    public void The_iso_form_names_its_offset_and_parses_back_to_the_stored_instant()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcMinusFour);

        var iso = LocalTime.Iso(Reported);

        Assert.Equal("2026-09-18T23:03:39.0000000-04:00", iso);
        var parsed = DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Reported, parsed);
        Assert.Equal(TimeSpan.FromHours(-4), parsed.Offset);
    }

    [Fact]
    public void The_offset_follows_daylight_saving_instant_by_instant()
    {
        var eastern = EasternLike();
        using var pin = PlayerClock.Pin(eastern);
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-01-15 07:00", LocalTime.Sortable(winter));
        Assert.Equal("UTC-05:00", LocalTime.Offset(winter));
        Assert.Equal("2026-07-15 08:00", LocalTime.Sortable(summer));
        Assert.Equal("UTC-04:00", LocalTime.Offset(summer));
    }

    /// <summary>
    /// The hour the clocks repeat is the one place local time is ambiguous. The sortable form cannot
    /// tell the two 01:30s apart; the ISO form can, which is why machine output carries the offset.
    /// </summary>
    [Fact]
    public void Only_the_iso_form_tells_the_repeated_hour_apart()
    {
        using var pin = PlayerClock.Pin(EasternLike());
        var firstPass = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var secondPass = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

        Assert.Equal(LocalTime.Sortable(firstPass), LocalTime.Sortable(secondPass));
        Assert.Equal("2026-11-01T01:30:00.0000000-04:00", LocalTime.Iso(firstPass));
        Assert.Equal("2026-11-01T01:30:00.0000000-05:00", LocalTime.Iso(secondPass));
    }

    [Fact]
    public void A_missing_instant_stays_missing_and_the_extremes_do_not_throw()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcMinusFour);

        Assert.Null(LocalTime.Moment((DateTimeOffset?)null));
        Assert.Equal("09/18/2026 23:03", LocalTime.Moment((DateTimeOffset?)Reported));
        Assert.NotNull(LocalTime.Moment(DateTimeOffset.MinValue));
        Assert.NotNull(LocalTime.Moment(DateTimeOffset.MaxValue));
        Assert.NotNull(LocalTime.Sortable(default));
    }

    [Fact]
    public void Disposing_a_pin_restores_whatever_was_in_force_before_it()
    {
        var outside = LocalTime.Zone;
        using (PlayerClock.Pin(PlayerClock.UtcMinusFour))
        {
            using (LocalTime.UseZone(PlayerClock.UtcPlusFiveThirty))
            {
                Assert.Equal("UTC+05:30", LocalTime.Offset(Reported));
            }

            Assert.Equal("UTC-04:00", LocalTime.Offset(Reported));
        }

        Assert.Same(outside, LocalTime.Zone);
    }

    [Fact]
    public async Task A_pinned_zone_belongs_to_its_own_async_flow()
    {
        var go = new TaskCompletionSource();

        async Task<string> OffsetInside(TimeZoneInfo zone)
        {
            using var pin = LocalTime.UseZone(zone);
            await go.Task;
            await Task.Yield();
            return LocalTime.Offset(Reported);
        }

        var minusFour = OffsetInside(PlayerClock.UtcMinusFour);
        var plusFiveThirty = OffsetInside(PlayerClock.UtcPlusFiveThirty);
        go.SetResult();

        Assert.Equal("UTC-04:00", await minusFour);
        Assert.Equal("UTC+05:30", await plusFiveThirty);
    }

    /// <summary>A US-style zone built in code, so the test needs no tz database on the runner.</summary>
    private static TimeZoneInfo EasternLike()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/Eastern-like",
            TimeSpan.FromHours(-5),
            "Eastern-like",
            "Standard",
            "Daylight",
            [rule]);
    }
}
