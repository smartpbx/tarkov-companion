using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// Flea rows are joined by where their text is on the page, not by which line the engine listed it
/// on. The parser used to need a price and a quantity on the same OCR line, so a row that had them
/// apart (the ordinary case) lost its quantity, a glyph the engine read as its own line lost the
/// row, and a row's quantity could not be told from its neighbour's.
/// </summary>
/// <remarks>
/// The fixtures are laid out the way a list of rows is (a name above, a quantity below, the price
/// to the right). A real 3840x1080 browse screenshot confirmed that shape and a EUR row, but OCR
/// line boxes remain synthetic here, so only the relations between them are asserted.
/// </remarks>
public sealed class FleaRowGeometryTests
{
    private static readonly FleaListingParser Parser = new();

    [Fact]
    public void A_quantity_on_its_own_line_below_the_price_belongs_to_that_row()
    {
        var listings = Parse(
            Line("Salewa first aid kit", 300, 300, 400),
            Line("189 999 ₽", 1200, 310, 220),
            Line("x3", 300, 335, 60));

        var row = Assert.Single(listings);
        Assert.Equal(189_999, row.PriceRoubles);
        Assert.Equal(3, row.Quantity);
    }

    [Fact]
    public void The_order_the_engine_listed_lines_in_does_not_matter()
    {
        var lines = new[]
        {
            Line("x2", 300, 135, 60),
            Line("x5", 300, 235, 60),
            Line("45 000 ₽", 1200, 110, 200),
            Line("46 500 ₽", 1200, 210, 200),
            Line("AI-2 medkit", 300, 100, 300),
            Line("Grizzly", 300, 200, 300),
        };

        var forward = Parse(lines);
        var backward = Parse(Enumerable.Reverse(lines).ToArray());

        Assert.Equal(forward.Select(Shape), backward.Select(Shape));
        Assert.Equal([(45_000L, (int?)2), (46_500L, (int?)5)], forward.Select(Shape));
    }

    [Fact]
    public void Similar_prices_stay_separate_rows_and_each_keeps_its_own_quantity()
    {
        var listings = Parse(
            Line("189 999 ₽", 1200, 100, 220),
            Line("x4", 300, 125, 60),
            Line("189 990 ₽", 1200, 200, 220),
            Line("x1", 300, 225, 60));

        Assert.Equal([(189_999L, (int?)4), (189_990L, (int?)1)], listings.Select(Shape));
    }

    [Fact]
    public void A_glyph_the_engine_read_as_its_own_line_is_put_back_with_its_digits()
    {
        var listings = Parse(
            Line("189 999", 1200, 300, 150),
            Line("₽", 1355, 300, 18));

        var row = Assert.Single(listings);
        Assert.Equal(189_999, row.PriceRoubles);
        Assert.Equal(1200, row.Bounds.X);
        Assert.Equal(1373, row.Bounds.X + row.Bounds.Width);
    }

    [Fact]
    public void A_glyph_on_another_visual_line_is_not_joined_to_a_price_above_it()
    {
        // The rouble sign under a number is somebody else's text, not the number's currency.
        var listings = Parse(
            Line("189 999", 1200, 300, 150),
            Line("₽", 1355, 400, 18));

        Assert.Empty(listings);
    }

    [Fact]
    public void A_bare_number_is_not_a_price()
    {
        // With the glyph lost altogether a number could as well be a count or a stat. How the engine
        // renders a lost glyph is unmeasured, so this refuses rather than guesses.
        Assert.Empty(Parse(Line("189 999", 1200, 300, 150), Line("x3", 300, 325, 60)));
    }

    [Fact]
    public void A_line_midway_between_two_rows_belongs_to_neither()
    {
        var listings = Parse(
            Line("100 000 ₽", 1200, 100, 220),
            Line("x7", 300, 148, 60),
            Line("110 000 ₽", 1200, 196, 220));

        Assert.All(listings, row => Assert.Null(row.Quantity));
        Assert.Equal(2, listings.Count);
    }

    [Fact]
    public void Two_different_quantities_on_one_row_are_not_read_as_either()
    {
        var listings = Parse(
            Line("50 000 ₽", 1200, 300, 200),
            Line("x2", 300, 285, 60),
            Line("x9", 300, 320, 60));

        Assert.Null(Assert.Single(listings).Quantity);
    }

    [Fact]
    public void A_row_cut_off_by_the_frame_leaves_its_quantity_out_of_the_row_above()
    {
        // The lower row's price is below the frame's bottom edge, so it is not a row; its "x9" is
        // still on the page and must not be taken for the row above's.
        var listings = Parse(
            height: 400,
            Line("75 500 ₽", 1200, 100, 200),
            Line("x2", 300, 125, 60),
            Line("x9", 300, 190, 60));

        var row = Assert.Single(listings);
        Assert.Equal(2, row.Quantity);
    }

    [Fact]
    public void A_quantity_ahead_of_the_price_does_not_become_part_of_it()
    {
        var listings = Parse(Line("x3 189 999 ₽", 1200, 300, 260));

        var row = Assert.Single(listings);
        Assert.Equal(189_999, row.PriceRoubles);
        Assert.Equal(3, row.Quantity);
    }

    [Fact]
    public void A_row_carries_what_it_was_read_from_and_the_least_sure_line_sets_its_confidence()
    {
        var listings = Parse(
            new OcrLine("189 999 ₽", new PixelRect(1200, 310, 220, 24), new Confidence(0.95)),
            new OcrLine("x3", new PixelRect(300, 335, 60, 24), new Confidence(0.60)));

        var row = Assert.Single(listings);
        Assert.Equal("189 999 ₽ | x3", row.SourceText);
        Assert.Equal(0.60 * 0.98, row.Confidence.Value, 3);
        Assert.Equal(new PixelRect(300, 310, 1120, 49), row.Bounds);
    }

    [Fact]
    public void Rows_come_back_top_to_bottom_whatever_order_they_were_found_in()
    {
        var listings = Parse(
            Line("3 000 ₽", 1200, 500, 150),
            Line("1 000 ₽", 1200, 100, 150),
            Line("2 000 ₽", 1200, 300, 150));

        Assert.Equal([1_000L, 2_000L, 3_000L], listings.Select(row => row.PriceRoubles));
    }

    [Fact]
    public void A_euro_price_uses_the_catalog_exchange_rate_and_keeps_the_original_quote()
    {
        var image = new CapturedImage(
            new byte[1920 * 1080],
            1920,
            1080,
            1920,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            "fixture://flea-eur");
        var rate = new CurrencyRoubleRate("EUR", 222, new DataProvenance("fixture catalog", image.CapturedUtc));

        var row = Assert.Single(Parser.ParseVisible(
            new OcrResult([Line("1 €", 1200, 300, 80)], TimeSpan.Zero, "fixture-ocr"),
            image,
            new Dictionary<string, CurrencyRoubleRate>(StringComparer.Ordinal) { ["EUR"] = rate }));

        Assert.Equal(222, row.PriceRoubles);
        Assert.Equal(1, row.OriginalPrice);
        Assert.Equal("EUR", row.CurrencyCode);
        Assert.Equal(222, row.CurrencyRateRoubles);
        Assert.Equal(rate.Provenance, row.CurrencyRateProvenance);
    }

    [Theory]
    [InlineData("durability 41.5/60", ItemConditionKind.Durability, 41.5, 60)]
    [InlineData("uses 3/5", ItemConditionKind.Uses, 3, 5)]
    public void A_labelled_condition_is_kept_with_its_kind(
        string text,
        ItemConditionKind kind,
        double current,
        double maximum)
    {
        var row = Assert.Single(Parse(
            Line("84 000 ₽", 1200, 300, 180),
            Line(text, 300, 325, 180)));

        Assert.NotNull(row.Condition);
        Assert.Equal(kind, row.Condition.Kind);
        Assert.Equal(current, row.Condition.Current);
        Assert.Equal(maximum, row.Condition.Maximum);
    }

    private static (long Price, int? Quantity) Shape(FleaListing listing) => (listing.PriceRoubles, listing.Quantity);

    private static OcrLine Line(string text, int x, int y, int width) =>
        new(text, new PixelRect(x, y, width, 24), new Confidence(0.95));

    private static IReadOnlyList<FleaListing> Parse(params OcrLine[] lines) => Parse(1080, lines);

    private static IReadOnlyList<FleaListing> Parse(int height, params OcrLine[] lines) =>
        Parser.ParseVisible(
            new OcrResult(lines, TimeSpan.Zero, "fixture-ocr"),
            new CapturedImage(
                new byte[1920 * height],
                1920,
                height,
                1920,
                PixelFormat.Gray8,
                new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
                "fixture://flea"));
}
