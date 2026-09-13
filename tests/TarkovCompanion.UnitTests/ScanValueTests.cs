using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What an item is worth, and whether to take it, are different questions.
/// </summary>
/// <remarks>
/// They used to share one field. The value was read off the recommendation, and the
/// recommendation is withheld whenever the raid context it needs is missing, so the
/// application reported "value unavailable" for an item whose price it had already fetched.
/// Reported from a real scan of a piece of armour.
/// </remarks>
public sealed class ScanValueTests
{
    [Fact]
    public void AnItemWithNoAdviceStillReportsItsValue()
    {
        var result = ScanExecutionResult.FromOutcome(Outcome(value: 44_000, perSlot: 11_000), "game screenshot");

        Assert.True(result.Succeeded);
        Assert.Equal(44_000, result.ValueRoubles);
        Assert.Equal(11_000, result.ValuePerSlotRoubles);
        Assert.Null(result.Recommendation);
    }

    [Fact]
    public void TheDetailSaysWhatItIsWorthRatherThanApologising()
    {
        var result = ScanExecutionResult.FromOutcome(Outcome(value: 44_000, perSlot: 11_000), "game screenshot");

        Assert.Contains("44,000", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWithNoPriceAtAllStillReportsWhatItIs()
    {
        // A price genuinely missing is different from one hidden behind withheld advice, and
        // the item's name is still worth reporting.
        var result = ScanExecutionResult.FromOutcome(Outcome(value: null, perSlot: null), "game screenshot");

        Assert.True(result.Succeeded);
        Assert.Null(result.ValueRoubles);
        Assert.Equal("PACA Soft Armor", result.ItemName);
    }

    private static ScanOutcome Outcome(long? value, long? perSlot)
    {
        var candidate = new RecognitionCandidate("paca", "PACA Soft Armor", new Confidence(0.95), "test");
        return new ScanOutcome(
            Guid.Empty,
            ScanCompletionStatus.Partial,
            ScanContext.SingleItem,
            DateTimeOffset.UnixEpoch,
            new RecognitionResult(ScanContext.SingleItem, [candidate], DateTimeOffset.UnixEpoch),
            null,
            null,
            null,
            // No recommendation, which is the case this exists for.
            null,
            [],
            "recommendation_context_unavailable")
        {
            EconomicValue = value,
            ValuePerSlot = perSlot,
        };
    }
}
