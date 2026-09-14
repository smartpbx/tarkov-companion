using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// The raid clock the extract screen draws, all the way from the picture.
/// </summary>
/// <remarks>
/// The clock rode in <c>UnmatchedLines</c> and <c>AmbiguousLines</c> — the diagnostics for rows
/// that failed to match an exit — on the reasoning that a clock is not an extract name so it
/// must land there. The real row is <c>Find an extraction point 0:12:28</c>: the clock is
/// stripped as a trailing measure, the remainder is recognised as the panel header, and the
/// line is dropped before either diagnostic list sees it.
///
/// <c>RaidTimerTests</c> missed it by supplying "Find an extraction point" and "0:28:10" as two
/// separate strings, which is what OCR does only when it happens to break the row in two. These
/// tests start from lines a screen actually produces.
/// </remarks>
public sealed class ExtractClockTests
{
    [Fact]
    public async Task TheClockSurvivesBeingOnTheHeaderRow()
    {
        // The regression. One line, exactly as the panel draws it.
        var result = await ReadAsync("Find an extraction point 0:28:10", "Road to Customs ACTIVE");

        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidTimer.Read(result.RawLines));
    }

    [Fact]
    public async Task TheClockStillWorksOnALineOfItsOwn()
    {
        // What OCR does when it breaks the row in two, which is the only case that ever worked.
        var result = await ReadAsync("Find an extraction point", "0:28:10", "Road to Customs ACTIVE");

        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidTimer.Read(result.RawLines));
    }

    [Fact]
    public async Task AClockOnAMatchedRowIsNotLost()
    {
        // A row that matched an exit never reached the diagnostics either, so a clock drawn
        // beside one was gone for the same reason.
        var result = await ReadAsync("Road to Customs ACTIVE 0:28:10");

        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidTimer.Read(result.RawLines));
    }

    [Fact]
    public async Task AShorterExitCountdownDoesNotReplaceTheRaidClock()
    {
        // Some exits carry their own countdown. The raid is the longer of the two by
        // construction, and taking the shorter one would end the raid early on screen.
        var result = await ReadAsync(
            "Find an extraction point 0:28:10",
            "Road to Customs ACTIVE 0:02:15");

        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidTimer.Read(result.RawLines));
    }

    [Fact]
    public async Task TheGameSayingItDoesNotKnowIsNotAClock()
    {
        // An undecided exit is drawn "??:??:??" and OCR renders the question marks as digits.
        var result = await ReadAsync("Road to Customs PENDING ??:??:??");

        Assert.Null(RaidTimer.Read(result.RawLines));
    }

    [Fact]
    public async Task AScanIsNotPartialJustBecauseTheHeaderCarriedTheClock()
    {
        // The header is skipped as a header, not filed as a line that failed to match, so a
        // clean read stays clean.
        var result = await ReadAsync("Find an extraction point 0:28:10", "Road to Customs ACTIVE");

        Assert.Empty(result.UnmatchedLines);
        Assert.Empty(result.AmbiguousLines);
        Assert.Null(result.DiagnosticCode);
    }

    [Fact]
    public async Task TheRawLinesAreWhatTheScreenSaidRatherThanWhatMatchingLeftOver()
    {
        var result = await ReadAsync("Find an extraction point 0:28:10", "Road to Customs ACTIVE");

        Assert.Contains("Find an extraction point 0:28:10", result.RawLines);
        Assert.Contains("Road to Customs ACTIVE", result.RawLines);
    }

    [Fact]
    public async Task TheOldTransportCouldNotHaveCarriedIt()
    {
        // The diagnosis, pinned. This is what the clock used to travel on, and on the real row
        // it is empty — so RaidTimer.Read was handed nothing and the panel fell back to a
        // duration counted from the map. Reading it off the raw lines is not a better route to
        // the same place; it is the only route.
        var result = await ReadAsync("Find an extraction point 0:28:10", "Road to Customs ACTIVE");

        var oldTransport = result.UnmatchedLines.Concat(result.AmbiguousLines).ToArray();

        Assert.Empty(oldTransport);
        Assert.Null(RaidTimer.Read(oldTransport));
        Assert.NotNull(RaidTimer.Read(result.RawLines));
    }

    private static async Task<TarkovCompanion.Core.Abstractions.ExtractRecognitionResult> ReadAsync(
        params string[] texts)
    {
        var image = new CapturedImage(
            new byte[checked(1000 * 700)],
            1000,
            700,
            1000,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 14, 4, 0, 0, TimeSpan.Zero),
            "fixture://extract-clock");
        var lines = texts
            .Select((text, index) => new OcrLine(text, new(500, 100 + (index * 40), 400, 20), new Confidence(0.96)))
            .ToArray();
        var provenance = new DataProvenance("fixture", image.CapturedUtc);
        var map = new MapDefinition(
            "customs",
            "Customs",
            null,
            null,
            [],
            [new MapExtract("road-to-customs", "customs", "Road to Customs", null, null, provenance)],
            null,
            provenance);

        return await new ExtractRecognitionService(new FixtureOcrEngine([new(image.Source, lines)]))
            .RecognizeAsync(image, map, CancellationToken.None);
    }
}
