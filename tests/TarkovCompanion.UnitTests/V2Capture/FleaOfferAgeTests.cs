using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>#284: a flea ranking says when its offers were seen, and that they may be gone once old.</summary>
public sealed class FleaOfferAgeTests
{
    private static readonly DateTimeOffset Seen = new(2026, 9, 24, 14, 32, 10, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Offers as seen at 14:32")]
    [InlineData(299, "Offers as seen at 14:32")]
    [InlineData(300, "Offers as seen at 14:32 · 5 min ago, may be gone")]
    [InlineData(59 * 60 + 59, "Offers as seen at 14:32 · 59 min ago, may be gone")]
    [InlineData(3 * 3600 + 120, "Offers as seen at 14:32 · 3 h ago, may be gone")]
    public void TheWordingAgesWithTheScreenshot(int secondsLater, string expected)
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);

        var label = FleaOfferAge.Describe(Seen, Seen.AddSeconds(secondsLater), CultureInfo.InvariantCulture);

        Assert.Equal(expected, label);
        Assert.Equal(secondsLater >= 300, FleaOfferAge.IsStale(Seen, Seen.AddSeconds(secondsLater)));
    }

    [Fact]
    public void ADayOldScreenshotIsDatedRatherThanCountedInHours()
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);

        var label = FleaOfferAge.Describe(Seen, Seen.AddDays(2), CultureInfo.InvariantCulture);

        Assert.EndsWith($"on {LocalTime.Date(Seen, CultureInfo.InvariantCulture)}, may be gone", label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimeShownIsThePlayersLocalTime()
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3"));

        Assert.Equal("Offers as seen at 17:32", FleaOfferAge.Describe(Seen, Seen, CultureInfo.InvariantCulture));
    }
}
