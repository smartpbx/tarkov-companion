using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.UnitTests;

public sealed class PriceHistorySummaryTests
{
    [Fact]
    public void SevenDaySummaryUsesOnlyPositiveFleaObservations()
    {
        var summary = PriceHistorySummary.From(
        [
            new(DateTimeOffset.UnixEpoch, 90_000, 10_000, "sync"),
            new(DateTimeOffset.UnixEpoch, null, 11_000, "sync"),
            new(DateTimeOffset.UnixEpoch, 0, 12_000, "sync"),
            new(DateTimeOffset.UnixEpoch, 120_001, 13_000, "sync"),
            new(DateTimeOffset.UnixEpoch, 150_000, 14_000, "sync"),
        ]);

        Assert.Equal(new PriceHistorySummary(90_000, 120_000, 150_000, 3), summary);
    }

    [Fact]
    public void NoFleaObservationsHasNoSummary() =>
        Assert.Null(PriceHistorySummary.From(
        [
            new(DateTimeOffset.UnixEpoch, null, 10_000, "sync"),
        ]));
}
