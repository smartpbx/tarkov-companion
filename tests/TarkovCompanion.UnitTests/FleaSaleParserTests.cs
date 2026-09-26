using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

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

    /// <summary>
    /// One offer sold in parts is several sales (#403 re-measure on 1.1.5.1.47510: 103 sale
    /// notifications named 72 offers, and each had its own event id and its own payment).
    /// </summary>
    [Fact]
    public void KeepsEachPartOfAnOfferThatSoldInParts()
    {
        var service = new FleaSaleStateService();

        service.Apply(FleaSaleParser.ParseLine(Sold("EVENT_1", "OFFER_1", 1), Observed)!);
        var snapshot = service.Apply(FleaSaleParser.ParseLine(Sold("EVENT_2", "OFFER_1", 1), Observed.AddSeconds(1))!);

        Assert.Equal(2, snapshot.Sales.Count);
        Assert.Equal(2, snapshot.Sales.Sum(sale => sale.Count));
    }

    /// <summary>backend and output both carry every sale, under the same event id.</summary>
    [Fact]
    public void KeepsOneRowForTheSameSaleReadFromTwoFiles()
    {
        var service = new FleaSaleStateService();
        var backend = Sold("EVENT_1", "OFFER_1", 3);
        var output = backend.Replace("|backend|", "|output|backend|", StringComparison.Ordinal);

        Assert.True(service.TryApply(FleaSaleParser.ParseLine(backend, Observed)!));
        Assert.False(service.TryApply(FleaSaleParser.ParseLine(output, Observed)!));

        var sale = Assert.Single(service.Current.Sales);
        Assert.Equal("EVENT_1", sale.EventId);
        Assert.Equal("EVENT_1", sale.SaleKey);
    }

    /// <summary>
    /// The raid record adds counts up per item, so the copy of a sale from the second file must
    /// not reach it, while the second part of an offer must.
    /// </summary>
    [Fact]
    public void RecordsEachSaleAgainstTheRaidOnce()
    {
        var recorder = new SaleRecorder();
        var observers = new EftLogObservers(new SquadStateService(), new FleaSaleStateService(), raid: recorder);
        var first = Sold("EVENT_1", "OFFER_1", 2);

        observers.Observe(FleaSaleParser.ParseLine(first, Observed)!);
        observers.Observe(FleaSaleParser.ParseLine(first.Replace("|backend|", "|output|backend|", StringComparison.Ordinal), Observed)!);
        observers.Observe(FleaSaleParser.ParseLine(Sold("EVENT_2", "OFFER_1", 1), Observed)!);

        Assert.Equal(["EVENT_1", "EVENT_2"], recorder.Sales.Select(sale => sale.EventId));
    }

    private static string Sold(string eventId, string offerId, int count) =>
        LineHeader
        + $$"""[{"type":"RagfairOfferSold","eventId":"{{eventId}}","offerId":"{{offerId}}","handbookId":"ITEM_1","count":{{count}}}]""";

    private sealed class SaleRecorder : IRaidActivityRecorder
    {
        public List<FleaSaleObservation> Sales { get; } = [];

        public RaidSnapshot Current => throw new NotSupportedException();

        public Task<RaidSnapshot> ApplyExtractsAsync(
            IReadOnlyList<ActiveExtract> extracts,
            DateTimeOffset observedUtc,
            CancellationToken cancellationToken,
            TimeSpan? raidClock = null,
            IReadOnlyList<string>? linesNotMatched = null,
            IReadOnlyList<string>? transits = null) => throw new NotSupportedException();

        public Task RecordSaleAsync(FleaSaleObservation sale, CancellationToken cancellationToken)
        {
            Sales.Add(sale);
            return Task.CompletedTask;
        }

        public Task RecordQuestAsync(QuestStatusObservation quest, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordLoadTimeAsync(LoadTimeObservation loadTime, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
