using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Reading the notification the game posts when a flea offer sells.
/// </summary>
/// <remarks>
/// This existed in the logs all along and was recorded as absent, because a search for the
/// phrase "offer sold" misses a type name that is one word. These tests pin the type literal
/// exactly so the same miss cannot happen twice.
/// </remarks>
public sealed class FleaSaleParserTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

    private const string LineHeader =
        "2026-09-11 23:00:00.000|1.1.5.0.47242|Info|backend|NOTIFICATION [EVENTID] RagfairOfferSold ";

    [Fact]
    public void ReadsTheOfferTheItemAndTheQuantity()
    {
        var line = LineHeader
            + """[{"type":"RagfairOfferSold","eventId":"ID_1","offerId":"OFFER_1","handbookId":"ITEM_1","count":3}]""";

        var sale = FleaSaleParser.ParseLine(line, Observed);

        Assert.NotNull(sale);
        Assert.Equal("OFFER_1", sale.OfferId);
        Assert.Equal("ITEM_1", sale.HandbookItemId);
        Assert.Equal(3, sale.Count);
        Assert.Equal(Observed, sale.ObservedUtc);
    }

    /// <summary>
    /// The bracketed event id sits ahead of the payload and is not the payload.
    /// </summary>
    [Fact]
    public void FindsThePayloadPastTheBracketedEventId()
    {
        var line = LineHeader
            + """[{"type":"RagfairOfferSold","offerId":"OFFER_1","handbookId":"ITEM_1"}]""";

        var sale = FleaSaleParser.ParseLine(line, Observed);

        Assert.NotNull(sale);
        // A sale with no stated quantity is one item.
        Assert.Equal(1, sale.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-09-11 23:00:00.000|Info|backend|something entirely unrelated")]
    public void IgnoresAnythingThatIsNotASale(string line) =>
        Assert.Null(FleaSaleParser.ParseLine(line, Observed));

    /// <summary>
    /// A truncated notification must not interrupt observation.
    /// </summary>
    [Fact]
    public void IgnoresATruncatedPayload()
    {
        var line = LineHeader + """[{"type":"RagfairOfferSold","offerId":"OFFER_1",""";

        Assert.Null(FleaSaleParser.ParseLine(line, Observed));
    }

    /// <summary>
    /// Without an offer id the same sale cannot be recognised when it is redelivered.
    /// </summary>
    [Fact]
    public void IgnoresASaleWithNoOfferId()
    {
        var line = LineHeader + """[{"type":"RagfairOfferSold","handbookId":"ITEM_1","count":1}]""";

        Assert.Null(FleaSaleParser.ParseLine(line, Observed));
    }

    [Fact]
    public void KeepsOneRowPerOfferWhenANotificationIsRedelivered()
    {
        var service = new FleaSaleStateService();
        var line = LineHeader
            + """[{"type":"RagfairOfferSold","offerId":"OFFER_1","handbookId":"ITEM_1","count":2}]""";

        service.Apply(FleaSaleParser.ParseLine(line, Observed)!);
        var snapshot = service.Apply(FleaSaleParser.ParseLine(line, Observed.AddMinutes(1))!);

        var sale = Assert.Single(snapshot.Sales);
        Assert.Equal(2, sale.Count);
        Assert.Equal(Observed, sale.ObservedUtc);
    }

    [Fact]
    public void ListsTheNewestSaleFirst()
    {
        var service = new FleaSaleStateService();
        service.Apply(new(OfferId: "OFFER_1", HandbookItemId: "ITEM_1", Count: 1, ObservedUtc: Observed));
        var snapshot = service.Apply(new(
            OfferId: "OFFER_2",
            HandbookItemId: "ITEM_2",
            Count: 1,
            ObservedUtc: Observed.AddMinutes(5)));

        Assert.Equal("OFFER_2", snapshot.Sales[0].OfferId);
    }
}
