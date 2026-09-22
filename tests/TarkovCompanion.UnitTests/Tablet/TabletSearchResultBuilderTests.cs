using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests;

/// <summary>#379: the tablet's search answers say what the desktop's Intel list says.</summary>
public sealed class TabletSearchResultBuilderTests
{
    private static readonly DataProvenance Provenance = new("fixture", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void AHitCarriesKindSizePerSlotValueTheTraderWhoPaysAndTheWikiPage()
    {
        var price = new ItemPriceSnapshot(
            30_000,
            [
                new TraderOffer("therapist", "Therapist", 24_000, Provenance),
                new TraderOffer("mechanic", "Mechanic", 26_000, Provenance),
            ],
            null, null, null, Provenance);

        var result = TabletSearchResultBuilder.From(
            new ItemSearchHit(Item("https://escapefromtarkov.fandom.com/wiki/Graphics_card", 2, 1), 1, "gpu"),
            price,
            isAllergic: true);

        Assert.Equal("Graphics card", result.Name);
        Assert.Equal("Barter", result.Category);
        Assert.Equal("2×1", result.Size);
        Assert.Equal(15_000, result.PerSlotRoubles); // the best of flea and trader, over two slots
        Assert.Equal("Mechanic", result.TraderName);
        Assert.Equal(26_000, result.TraderRoubles);
        Assert.Equal(30_000, result.FleaRoubles);
        Assert.True(result.IsAllergic);
        Assert.Equal("https://escapefromtarkov.fandom.com/wiki/Graphics_card", result.WikiUri);
    }

    [Theory]
    [InlineData("http://escapefromtarkov.fandom.com/wiki/Graphics_card")]
    [InlineData("https://example.com/wiki/Graphics_card")]
    [InlineData(null)]
    public void OnlyAnHttpsLinkToTheWikiItselfIsSent(string? wiki)
    {
        var result = TabletSearchResultBuilder.From(new ItemSearchHit(Item(wiki, 1, 1), 1, "gpu"), null, false);

        Assert.Null(result.WikiUri);
        Assert.Null(result.PerSlotRoubles); // no price, no per-slot value
        Assert.Null(result.TraderName);
    }

    private static ItemDefinition Item(string? wiki, int width, int height) => new(
        "gpu",
        "Graphics card",
        "GPU",
        string.Empty,
        ItemCategory.Barter,
        new ItemDimensions(width, height),
        true,
        null,
        null,
        wiki,
        null,
        null,
        new HashSet<string>(),
        Provenance);
}
