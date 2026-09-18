using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Reading the matchmaking line docs/research/EFT_LOG_FACTS.md names as present and unused:
/// "Queue time | application | MatchingCompleted:18.36 real:25.02 diff:6.66 — use real".
/// </summary>
public sealed class LoadTimeParserTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsTheRealFigureRatherThanTheEstimate()
    {
        var line = "2026-09-11 23:00:00.000|1.1.5.0.47242|Info|application|MatchingCompleted:18.36 real:25.02 diff:6.66";

        var loadTime = LoadTimeParser.ParseLine(line, Observed);

        Assert.NotNull(loadTime);
        Assert.Equal(25.02, loadTime.RealSeconds);
        Assert.Equal(Observed, loadTime.ObservedUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-09-11 23:00:00.000|Info|application|LocationLoaded:18 real:24.73 diff:6.72")]
    [InlineData("2026-09-11 23:00:00.000|Info|application|something entirely unrelated")]
    public void IgnoresAnythingThatIsNotAMatchingCompletedLine(string line) =>
        Assert.Null(LoadTimeParser.ParseLine(line, Observed));

    /// <summary>A line naming the marker with no `real` figure yet is not a partial answer.</summary>
    [Fact]
    public void IgnoresAMatchingCompletedLineWithNoRealFigure() =>
        Assert.Null(LoadTimeParser.ParseLine(
            "2026-09-11 23:00:00.000|Info|application|MatchingCompleted:18.36",
            Observed));
}
