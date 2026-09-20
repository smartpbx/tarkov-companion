using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.UnitTests.Intel;

/// <summary>
/// The Crafts &amp; barters tab's "I can do this now" filter (#287): a station/trader level the
/// active profile has never recorded must read as unknown, never as locked.
/// </summary>
public sealed class IntelTradeReadinessResolverTests
{
    private static readonly Dictionary<string, int> Empty = new(StringComparer.Ordinal);

    [Fact]
    public void A_barter_asking_for_loyalty_one_is_ready_even_with_no_recorded_levels_at_all()
    {
        // Loyalty 1 is where every trader relationship starts; it needs no profile data to grant.
        var result = IntelTradeReadinessResolver.ForBarter(1, "prapor", Empty);

        Assert.Equal(IntelTradeReadiness.Ready, result.Readiness);
        Assert.Null(result.RecordedLevel);
    }

    [Fact]
    public void A_barter_the_feed_states_no_requirement_for_is_ready()
    {
        var result = IntelTradeReadinessResolver.ForBarter(null, "prapor", Empty);

        Assert.Equal(IntelTradeReadiness.Ready, result.Readiness);
        Assert.Null(result.RecordedLevel);
    }

    [Fact]
    public void A_trader_level_above_one_with_nothing_recorded_is_unknown_not_locked()
    {
        var result = IntelTradeReadinessResolver.ForBarter(3, "prapor", Empty);

        Assert.Equal(IntelTradeReadiness.Unknown, result.Readiness);
        Assert.Null(result.RecordedLevel);
    }

    [Fact]
    public void A_recorded_trader_level_meeting_the_requirement_is_ready_and_reports_it()
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal) { ["prapor"] = 3 };

        var result = IntelTradeReadinessResolver.ForBarter(3, "prapor", levels);

        Assert.Equal(IntelTradeReadiness.Ready, result.Readiness);
        Assert.Equal(3, result.RecordedLevel);
    }

    [Fact]
    public void A_recorded_trader_level_below_the_requirement_is_locked_and_reports_it()
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal) { ["prapor"] = 2 };

        var result = IntelTradeReadinessResolver.ForBarter(3, "prapor", levels);

        Assert.Equal(IntelTradeReadiness.Locked, result.Readiness);
        Assert.Equal(2, result.RecordedLevel);
    }

    [Fact]
    public void A_stated_requirement_with_no_named_trader_is_unknown()
    {
        var result = IntelTradeReadinessResolver.ForBarter(3, null, Empty);

        Assert.Equal(IntelTradeReadiness.Unknown, result.Readiness);
        Assert.Null(result.RecordedLevel);
    }

    [Fact]
    public void A_craft_station_with_nothing_recorded_is_unknown_not_locked()
    {
        // Unlike trader loyalty, a hideout station has no free starting level (#287 review): a
        // level-1 requirement here is a real, unearned one.
        var result = IntelTradeReadinessResolver.ForCraft("workbench", 1, Empty);

        Assert.Equal(IntelTradeReadiness.Unknown, result.Readiness);
        Assert.Null(result.RecordedLevel);
    }

    [Fact]
    public void A_recorded_station_level_meeting_the_requirement_is_ready_and_reports_it()
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal) { ["workbench"] = 2 };

        var result = IntelTradeReadinessResolver.ForCraft("workbench", 2, levels);

        Assert.Equal(IntelTradeReadiness.Ready, result.Readiness);
        Assert.Equal(2, result.RecordedLevel);
    }

    [Fact]
    public void A_recorded_station_level_below_the_requirement_is_locked_and_reports_it()
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal) { ["workbench"] = 1 };

        var result = IntelTradeReadinessResolver.ForCraft("workbench", 2, levels);

        Assert.Equal(IntelTradeReadiness.Locked, result.Readiness);
        Assert.Equal(1, result.RecordedLevel);
    }

    [Fact]
    public void A_craft_with_no_stated_station_or_level_is_unknown()
    {
        Assert.Equal(IntelTradeReadiness.Unknown, IntelTradeReadinessResolver.ForCraft(null, 1, Empty).Readiness);
        Assert.Equal(IntelTradeReadiness.Unknown, IntelTradeReadinessResolver.ForCraft("workbench", null, Empty).Readiness);
    }
}
