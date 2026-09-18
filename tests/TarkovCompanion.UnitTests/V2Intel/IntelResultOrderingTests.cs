using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Intel;

public sealed class IntelResultOrderingTests
{
    private static readonly ItemSearchResultViewModel[] Hits =
    [
        Hit("relevant", "Salewa first aid kit", price: 30_000, perSlot: 7_500),
        Hit("cheap", "Bandage", price: 1_000, perSlot: 1_000),
        Hit("dear", "Graphics card", price: 322_000, perSlot: 161_000),
        Hit("unpriced", "Screws", price: null, perSlot: null),
    ];

    [Fact]
    public void RelevanceIsTheSearchsOwnOrder() =>
        Assert.Equal(
            ["relevant", "cheap", "dear", "unpriced"],
            V2ShellViewModel.SortResults(Hits, V2IntelSort.Relevance).Select(hit => hit.Id));

    [Fact]
    public void PriceAndPerSlotPutTheDearestFirstAndTheUnpricedLast()
    {
        Assert.Equal(
            ["dear", "relevant", "cheap", "unpriced"],
            V2ShellViewModel.SortResults(Hits, V2IntelSort.Price).Select(hit => hit.Id));
        Assert.Equal(
            ["dear", "relevant", "cheap", "unpriced"],
            V2ShellViewModel.SortResults(Hits, V2IntelSort.PerSlot).Select(hit => hit.Id));
    }

    [Fact]
    public void ItemsWithTheSamePriceKeepTheirRelevanceOrder()
    {
        var tied = new[] { Hit("first", "First", 500, 500), Hit("second", "Second", 500, 500), Hit("third", "Third", 900, 900) };

        Assert.Equal(["third", "first", "second"], V2ShellViewModel.SortResults(tied, V2IntelSort.Price).Select(hit => hit.Id));
    }

    [Fact]
    public void NameOrdersAlphabeticallyWithoutRegardToCase() =>
        Assert.Equal(
            ["Bandage", "Graphics card", "Salewa first aid kit", "Screws"],
            V2ShellViewModel.SortResults(Hits, V2IntelSort.Name).Select(hit => hit.Name));

    [Fact]
    public void ARowSaysItsSizeAndOnlyOffersAPerSlotValueWhenThereIsOne()
    {
        var priced = new V2IntelResultRowViewModel("a", "Graphics card", "GPU", "Barter", "₽322,222", false, new DelegateCommand(() => { }), "2×1", "₽161,111 / slot", string.Empty);
        var unpriced = new V2IntelResultRowViewModel("b", "Screws", "SCR", "Barter", "No price", false, new DelegateCommand(() => { }));

        Assert.Equal("Barter · 2×1", priced.Subtitle);
        Assert.True(priced.HasPerSlot);
        Assert.False(priced.HasMatchLabel);
        Assert.Equal("Barter", unpriced.Subtitle);
        Assert.False(unpriced.HasPerSlot);
    }

    private static ItemSearchResultViewModel Hit(string id, string name, long? price, long? perSlot) => new(
        id,
        name,
        name,
        "Barter",
        "1 × 1 · 1 slot(s)",
        "100%",
        price is null ? "Price unavailable" : $"{price:N0} ₽",
        "Flea",
        "json.tarkov.dev",
        price,
        perSlot,
        1,
        name,
        "1×1");
}
