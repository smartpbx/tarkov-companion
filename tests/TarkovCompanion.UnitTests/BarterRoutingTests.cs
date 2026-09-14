using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether bartering for something beats buying it.
/// </summary>
/// <remarks>
/// 789 barters are rewritten on every sync and no query had ever read one. They were hidden from
/// <c>sweep-unread.sh</c> by its DELETE blind spot: the refresh clears each table before
/// repopulating it, and "DELETE FROM barters" contains "FROM barters", so the sweep counted the
/// clear as a read.
///
/// This is the half of Next 1 the trader-level stepper unblocked. Every barter's loyalty
/// requirement was unanswerable while <c>PlayerProfile.TraderLevels</c> was written by nothing,
/// so a route line would have had to either ignore the requirement — offering routes the player
/// cannot take — or assume level 1 and hide most of them.
/// </remarks>
public sealed class BarterRoutingTests
{
    private static readonly Dictionary<string, long?> Prices = new(StringComparer.Ordinal)
    {
        ["cheap"] = 10_000,
        ["dear"] = 90_000,
        ["unpriced"] = null,
    };

    [Fact]
    public void The_cheapest_barter_a_player_can_take_wins()
    {
        var route = BarterRouting.Cheapest(
            "wanted",
            [Barter("a", [("dear", 1)]), Barter("b", [("cheap", 2)])],
            Levels(),
            Price);

        Assert.Equal("b", route!.BarterId);
        Assert.Equal(20_000, route.Roubles);
    }

    [Fact]
    public void A_barter_behind_loyalty_the_player_does_not_have_is_not_a_route()
    {
        // Not a worse route — not a route. Putting it on screen sends somebody to a trader who
        // will not serve them.
        var route = BarterRouting.Cheapest(
            "wanted",
            [Barter("locked", [("cheap", 1)], trader: "prapor", level: 3)],
            Levels(("prapor", 1)),
            Price);

        Assert.Null(route);
    }

    [Fact]
    public void The_same_barter_becomes_a_route_once_the_loyalty_is_there()
    {
        var barters = new[] { Barter("locked", [("cheap", 1)], trader: "prapor", level: 3) };

        Assert.Null(BarterRouting.Cheapest("wanted", barters, Levels(("prapor", 2)), Price));
        Assert.NotNull(BarterRouting.Cheapest("wanted", barters, Levels(("prapor", 3)), Price));
    }

    [Fact]
    public void A_barter_the_feed_states_no_requirement_for_is_open_to_anybody()
    {
        // The feed omits the field on trades that need nothing. Refusing everything it did not
        // describe would hide most of the catalog to guard against a case it does not produce.
        Assert.NotNull(BarterRouting.Cheapest(
            "wanted",
            [Barter("open", [("cheap", 1)], trader: "prapor", level: null)],
            Levels(),
            Price));
    }

    [Fact]
    public void A_barter_with_an_unpriced_input_is_left_out_rather_than_counted_as_free()
    {
        // The arithmetic that would otherwise make every unpriced barter the cheapest route
        // available: a partial sum is not an uncertain answer, it is a confidently wrong one,
        // always in the same direction.
        Assert.Null(BarterRouting.Cost(Barter("a", [("cheap", 1), ("unpriced", 1)]), Price));
    }

    [Fact]
    public void An_unpriced_barter_does_not_beat_a_priced_one()
    {
        var route = BarterRouting.Cheapest(
            "wanted",
            [Barter("unpriced-route", [("unpriced", 1)]), Barter("priced", [("dear", 1)])],
            Levels(),
            Price);

        Assert.Equal("priced", route!.BarterId);
    }

    [Fact]
    public void A_barter_that_hands_over_several_is_costed_per_item()
    {
        // Two barters at the same total are not the same price if one hands over three.
        var route = BarterRouting.Cheapest(
            "wanted",
            [Barter("one", [("dear", 1)]), Barter("three", [("dear", 1)], gives: 3)],
            Levels(),
            Price);

        Assert.Equal("three", route!.BarterId);
        Assert.Equal(30_000, route.PerItem);
    }

    [Fact]
    public void A_barter_for_something_else_is_not_a_route_to_this()
    {
        Assert.Null(BarterRouting.Cheapest(
            "wanted",
            [Barter("other", [("cheap", 1)], gives: 1, givesItem: "something-else")],
            Levels(),
            Price));
    }

    [Fact]
    public void Counting_more_than_one_of_an_input_costs_more_than_one()
    {
        Assert.Equal(30_000, BarterRouting.Cost(Barter("a", [("cheap", 3)]), Price));
    }

    [Fact]
    public void A_catalog_with_nothing_in_it_is_not_a_failure()
    {
        Assert.Null(BarterRouting.Cheapest("wanted", [], Levels(), Price));
    }

    [Fact]
    public void A_barter_wanting_nothing_costs_nothing_rather_than_failing()
    {
        // The feed does not produce these, but a row that failed to store its requirements
        // would look like one, and the answer has to be a number rather than an exception.
        Assert.Equal(0, BarterRouting.Cost(Barter("a", []), Price));
    }

    private static long? Price(string itemId) => Prices.GetValueOrDefault(itemId);

    private static Dictionary<string, int> Levels(params (string Trader, int Level)[] levels) =>
        levels.ToDictionary(entry => entry.Trader, entry => entry.Level, StringComparer.Ordinal);

    private static BarterOffer Barter(
        string id,
        (string ItemId, int Count)[] wants,
        string? trader = "prapor",
        int? level = null,
        int gives = 1,
        string givesItem = "wanted") => new(
        id,
        trader,
        level,
        null,
        new(givesItem, gives),
        [.. wants.Select(want => new BarterItem(want.ItemId, want.Count))]);
}
