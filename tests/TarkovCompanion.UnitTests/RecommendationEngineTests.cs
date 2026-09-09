using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.UnitTests;

public sealed class RecommendationEngineTests
{
    [Fact]
    public void KnownAllergyOverridesEconomy()
    {
        var result = Recommend(EventItemState.Allergic, questCount: 0, flea: 2_000_000);

        Assert.Equal(RecommendationAction.AvoidConsume, result.Action);
        Assert.Equal(RecommendationReasonCode.KnownAllergy, Assert.Single(result.Reasons).Code);
    }

    [Fact]
    public void FoundInRaidQuestNeedOverridesHighFleaSale()
    {
        var result = Recommend(EventItemState.Unknown, questCount: 1, flea: 2_000_000);

        Assert.Equal(RecommendationAction.EssentialKeep, result.Action);
        Assert.Equal(1_000_000, result.ValuePerSlot);
    }

    [Fact]
    public void LowValueItemBecomesDropCandidate()
    {
        var result = Recommend(EventItemState.Unknown, questCount: 0, flea: 10_000);

        Assert.Equal(RecommendationAction.DropFirst, result.Action);
        Assert.Equal("D", result.Rating);
    }

    private static RecommendationResult Recommend(EventItemState eventState, int questCount, long flea)
    {
        var now = DateTimeOffset.UtcNow;
        var provenance = new DataProvenance("fixture", now, now);
        var item = new ItemDefinition(
            "fixture",
            "Fixture item",
            "Fixture",
            string.Empty,
            ItemCategory.Provision,
            new ItemDimensions(2, 1),
            true,
            null,
            null,
            null,
            null,
            null,
            new HashSet<string>(),
            provenance);
        var price = new ItemPriceSnapshot(flea, [], null, null, null, provenance);
        var context = new RecommendationContext(
            true,
            questCount,
            questCount,
            0,
            false,
            eventState,
            null,
            null,
            Confidence.Certain);
        return new RecommendationEngine().Recommend(item, price, context, ValueTierThresholds.Default);
    }
}
