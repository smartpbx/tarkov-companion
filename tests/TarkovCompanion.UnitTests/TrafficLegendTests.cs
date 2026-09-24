using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Strategy.Prior;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#266] The heat legend as ordered text, so colour is not the only way to read it.
/// </summary>
public sealed class TrafficLegendTests
{
    [Fact]
    public void The_bands_read_low_to_high_with_the_numbers_they_start_at()
    {
        var bands = TrafficLegend.Bands();

        Assert.Equal(["1", "2", "3", "4"], bands.Select(band => band.Rank));
        Assert.Equal(["No tint", "Amber", "Orange", "Red"], bands.Select(band => band.Label));
        Assert.StartsWith("low", bands[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("highest", bands[3].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Places_are_ranked_highest_first_with_the_plan_cards_own_levels()
    {
        var places = TrafficLegend.Places(
        [
            Hotspot("Warehouse 7", 0.70),
            Hotspot("Dorms", 0.55),
            Hotspot("Warehouse 17", 0.86),
        ]);

        Assert.Equal(["Warehouse 17", "Warehouse 7", "Dorms"], places.Select(place => place.Label));
        Assert.Equal(["1", "2", "3"], places.Select(place => place.Rank));
        Assert.StartsWith("Highest", places[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("High ", places[1].Value, StringComparison.Ordinal);
        Assert.StartsWith("Raised", places[2].Value, StringComparison.Ordinal);
        Assert.Equal("1. Warehouse 17, " + places[0].Value, places[0].AccessibleName);
    }

    [Fact]
    public void Ties_keep_a_stable_order_and_the_list_stops_at_five()
    {
        var places = TrafficLegend.Places(
            [.. Enumerable.Range(0, 8).Select(index => Hotspot($"Place {(char)('H' - index)}", 0.7))]);

        Assert.Equal(TrafficLegend.MaximumPlaces, places.Count);
        Assert.Equal("Place A", places[0].Label);
    }

    [Fact]
    public void A_place_just_under_a_threshold_does_not_round_up_into_the_next_band()
    {
        // Customs' Big Red read "Raised · 65%" beside "high · 65%+".
        var places = TrafficLegend.Places([Hotspot("Big Red", 0.6496)]);

        Assert.StartsWith("Raised", places[0].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("65", places[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nameless_or_unmeasured_hotspot_is_left_out_rather_than_ranked()
    {
        var places = TrafficLegend.Places([Hotspot("", 0.9), Hotspot("Crossroads", double.NaN), Hotspot("Gas", 0.6)]);

        Assert.Equal(["Gas"], places.Select(place => place.Label));
    }

    private static TrafficHotspot Hotspot(string name, double intensity) =>
        new(new(0, 0), intensity, name, [], 10);
}
