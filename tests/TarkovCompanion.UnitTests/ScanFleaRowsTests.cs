using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// A flea scan selects no item, so what it found has to be shown as rows. The parser joined rows by
/// geometry and the scan then reported them as a count in its evidence: the player was told
/// "FleaListings scan finished with Complete" and never saw a price. These are the words that reach
/// the interface, and whether a screenshot taken on the flea market is published at all.
/// </summary>
public sealed class ScanFleaRowsTests
{
    private static readonly DateTimeOffset Taken = new(2026, 9, 19, 16, 3, 41, TimeSpan.Zero);

    private static readonly TimeZoneInfo Minus4 =
        TimeZoneInfo.CreateCustomTimeZone("test-minus-4", TimeSpan.FromHours(-4), "test", "test");

    [Fact]
    public void The_rows_that_were_read_are_shown_with_the_time_of_the_screenshot_on_the_players_clock()
    {
        using var zone = LocalTime.UseZone(Minus4);
        using var culture = new CultureScope();

        var result = ScanExecutionResult.FromOutcome(
            Outcome(Row(189_999, 3), Row(45_000, null), Row(46_500, 2)), "game screenshot");

        Assert.Equal("Flea rows as of 12:03:41: 189,999 ₽ ×3 · 45,000 ₽ · 46,500 ₽ ×2.", result.Detail);
        Assert.False(result.Succeeded);
        Assert.Null(result.CanonicalItemId);
    }

    [Fact]
    public void A_long_page_shows_the_first_rows_and_how_many_more()
    {
        using var zone = LocalTime.UseZone(Minus4);
        using var culture = new CultureScope();

        var result = ScanExecutionResult.FromOutcome(
            Outcome(Enumerable.Range(1, 8).Select(index => Row(index * 1_000, null)).ToArray()), "game screenshot");

        Assert.Contains("1,000 ₽ · 2,000 ₽ · 3,000 ₽ · 4,000 ₽ · 5,000 ₽ · +3 more", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_read_says_so()
    {
        using var zone = LocalTime.UseZone(Minus4);
        using var culture = new CultureScope();

        var result = ScanExecutionResult.FromOutcome(
            Outcome(diagnostic: "ocr_tiles_partial", Row(5_000, null)), "game screenshot");

        Assert.EndsWith("5,000 ₽ · partial read.", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screenshot_that_read_rows_is_published_and_one_that_read_none_is_not()
    {
        // The game's screenshot key fires on everything a player photographs. Rows found are worth
        // showing; a flea screenshot with nothing readable is noise until somebody asks for it.
        var read = Outcome(Row(5_000, null));
        Assert.True(ScanExecutionResult.IsWorthPublishing(read, ScanExecutionResult.FromOutcome(read, "game screenshot")));

        var none = Outcome();
        var empty = ScanExecutionResult.FromOutcome(none, "game screenshot");
        Assert.False(ScanExecutionResult.IsWorthPublishing(none, empty));
        Assert.Contains("No flea rows could be read", empty.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unavailable_reader_is_still_reported_as_unavailable_and_not_as_no_rows()
    {
        var outcome = Outcome() with
        {
            Status = ScanCompletionStatus.Unavailable,
            Flea = new([], Taken, Confidence.Unknown, false, "ocr_provider_unavailable"),
            DiagnosticCode = "ocr_provider_unavailable",
        };

        var result = ScanExecutionResult.FromOutcome(outcome, "game screenshot");

        Assert.False(result.IsAvailable);
        Assert.Contains("Scan unavailable", result.Detail, StringComparison.Ordinal);
    }

    private static FleaListing Row(long price, int? quantity) =>
        new(price, quantity, new Confidence(0.9), new PixelRect(0, 0, 10, 10));

    private static ScanOutcome Outcome(params FleaListing[] rows) => Outcome(null, rows);

    private static ScanOutcome Outcome(string? diagnostic, params FleaListing[] rows) => new(
        Guid.Empty,
        rows.Length == 0 || diagnostic is not null ? ScanCompletionStatus.Partial : ScanCompletionStatus.Complete,
        ScanContext.FleaListings,
        Taken,
        new RecognitionResult(ScanContext.FleaListings, [], Taken),
        null,
        null,
        new FleaRecognitionResult(rows, Taken, rows.Length == 0 ? Confidence.Unknown : new Confidence(0.9), true, diagnostic),
        null,
        [],
        diagnostic);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
