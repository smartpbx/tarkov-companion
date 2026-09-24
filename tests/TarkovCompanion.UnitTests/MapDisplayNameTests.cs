using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

public sealed class MapDisplayNameTests
{
    [Theory]
    [InlineData("streets-of-tarkov", "Streets of Tarkov")]
    [InlineData("the-lab", "The Lab")]
    [InlineData("laboratory", "The Lab")]
    [InlineData("ground-zero", "Ground Zero")]
    [InlineData("ground-zero-21", "Ground Zero 21+")]
    [InlineData("the-labyrinth", "The Labyrinth")]
    [InlineData("night-factory", "Night Factory")]
    [InlineData("customs", "Customs")]
    [InlineData("some-map-of-the-future", "Some Map of the Future")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void A_slug_reads_the_way_the_game_names_the_map(string? slug, string expected) =>
        Assert.Equal(expected, MapDisplayName.FromId(slug));
}

public sealed class GameModeLabelTests
{
    [Theory]
    [InlineData("Regular", "PvP")]
    [InlineData("Pvp", "PvP")]
    [InlineData("Pve", "PvE")]
    [InlineData("PvpSeason", "Seasonal")]
    [InlineData("Seasonal", "Seasonal")]
    [InlineData("", "")]
    public void The_legacy_and_profile_names_of_a_mode_read_the_same(string stored, string expected) =>
        Assert.Equal(expected, TarkovCompanion.App.Services.V2.Shell.GameModeLabel.OfStored(stored));
}
