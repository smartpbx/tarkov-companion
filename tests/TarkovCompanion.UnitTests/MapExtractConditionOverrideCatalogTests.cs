using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests;

/// <summary>Reviewed conditions the primary catalog has no field capable of carrying.</summary>
public sealed class MapExtractConditionOverrideCatalogTests
{
    [Theory]
    [InlineData("customs", "Railroad Passage (Flare)", "Green flare")]
    [InlineData("factory", "Med Tent Gate", "Factory emergency exit key")]
    [InlineData("night-factory", "Cellars", "Factory emergency exit key")]
    [InlineData("woods", "ZB-014", "ZB-014 key")]
    [InlineData("woods", "Power Line Passage (Flare)", "Green flare")]
    [InlineData("streets-of-tarkov", "Klimov Street (Flare)", "Green flare")]
    [InlineData("ground-zero", "Mira Ave (Flare)", "Green flare")]
    [InlineData("ground-zero-21", "Mira Ave (Flare)", "Green flare")]
    public void ReviewedItemConditionsCoverEveryCatalogVariant(
        string map,
        string extract,
        string expectedItem)
    {
        Assert.True(MapExtractConditionOverrideCatalog.TryGet(map, extract, out var conditions));

        var items = Assert.Single(conditions, item => item.Kind == MapExtractConditionKind.Items);
        Assert.Contains(expectedItem, items.Items);
    }

    [Theory]
    [InlineData("shoreline", "Climber's Trail")]
    [InlineData("shoreline", "Cliff Descent")]
    [InlineData("lighthouse", "Mountain Pass")]
    public void ReviewedClimberConditionsRequireToolsAndNoArmor(string map, string extract)
    {
        Assert.True(MapExtractConditionOverrideCatalog.TryGet(map, extract, out var conditions));

        Assert.Contains(conditions, item => item.Kind == MapExtractConditionKind.NoArmor);
        Assert.Equal(
            ["Red Rebel ice pick", "Paracord"],
            Assert.Single(conditions, item => item.Kind == MapExtractConditionKind.Items).Items);
    }

    [Fact]
    public void LighthouseTrainUsesItsReviewedWindow()
    {
        Assert.True(MapExtractConditionOverrideCatalog.TryGet("lighthouse", "Armored Train", out var conditions));

        var window = Assert.Single(conditions).TimedWindow;
        Assert.Equal(TimeSpan.FromMinutes(20), window!.ArrivalStartsAtTimeLeft);
        Assert.Equal(TimeSpan.FromMinutes(15), window.ArrivalEndsAtTimeLeft);
        Assert.Equal(TimeSpan.FromMinutes(7), window.Duration);
    }
}
