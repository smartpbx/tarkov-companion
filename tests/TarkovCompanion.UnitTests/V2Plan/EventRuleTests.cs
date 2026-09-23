using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Events;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.UnitTests.V2Plan;

public sealed class EventRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EverySupportedEffectIsTypedAndExtraFieldsAreTolerated()
    {
        var result = EventRuleParser.Parse("""
            {
              "futureRoot": true,
              "effects": [
                { "type": "trader-price-multiplier", "traderId": "prapor", "traderName": "Prapor", "multiplier": 0.8, "future": 1 },
                { "type": "flea-availability", "enabled": false },
                { "type": "map-availability", "mapId": "laboratory", "mapName": "Labs", "available": false },
                { "type": "boss-spawn-multiplier", "bossId": "killa", "bossName": "Killa", "mapId": "interchange", "multiplier": 2 },
                { "type": "quest-availability-window", "questId": "quest-1", "questName": "The quest", "startUtc": "2026-10-01T00:00:00Z", "endUtc": "2026-11-01T00:00:00Z" }
              ]
            }
            """);

        Assert.True(result.IsValid);
        Assert.Collection(
            result.Rules.Effects,
            effect => Assert.Equal(0.8m, Assert.IsType<TraderPriceMultiplierRule>(effect).Multiplier),
            effect => Assert.False(Assert.IsType<FleaAvailabilityRule>(effect).Enabled),
            effect => Assert.False(Assert.IsType<MapAvailabilityRule>(effect).Available),
            effect => Assert.Equal(2m, Assert.IsType<BossSpawnMultiplierRule>(effect).Multiplier),
            effect => Assert.Equal("quest-1", Assert.IsType<QuestAvailabilityWindowRule>(effect).QuestId));
    }

    [Theory]
    [InlineData("{\"effects\":[{\"type\":\"trader-price-multiplier\",\"multiplier\":0.8}]}", "$.effects[0].traderId")]
    [InlineData("{\"effects\":[{\"type\":\"weather-magic\"}]}", "$.effects[0].type")]
    [InlineData("{\"effects\":{}}", "$.effects")]
    public void InvalidRulesNameTheExactField(string json, string path)
    {
        var result = EventRuleParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Path == path);
    }

    [Fact]
    public void OnlyScheduledActiveValidDefinitionsAreEvaluated()
    {
        EventDefinition[] definitions =
        [
            Definition("running", true, Now.AddDays(-1), Now.AddDays(1), MapClosed("factory")),
            Definition("future", true, Now.AddDays(1), null, MapClosed("woods")),
            Definition("archived", false, null, null, MapClosed("customs")),
            Definition("invalid", true, null, null, "{\"effects\":[{\"type\":\"unknown\"}]}"),
        ];

        var result = EventRuleService.Evaluate(definitions, Now);

        Assert.False(result.Active.IsMapAvailable("factory"));
        Assert.True(result.Active.IsMapAvailable("woods"));
        Assert.True(result.Active.IsMapAvailable("customs"));
        Assert.Single(result.InvalidDefinitions);
        Assert.Contains("invalid", result.InvalidDefinitions.Keys);
    }

    [Fact]
    public void TraderMultiplierAndFleaClosureChangeTheRecommendation()
    {
        var provenance = new DataProvenance("fixture", Now);
        var item = new ItemDefinition(
            "item", "Item", "Item", string.Empty, ItemCategory.Barter, new ItemDimensions(1, 1), true,
            null, null, null, null, null, new HashSet<string>(), provenance);
        var price = new ItemPriceSnapshot(
            90_000,
            [new TraderOffer("prapor", "Prapor", 100_000, provenance)],
            90_000,
            90_000,
            90_000,
            provenance);
        var context = new RecommendationContext(false, 0, 0, 0, false, EventItemState.Unknown, null, null, Confidence.Certain);
        var rules = EventRuleService.Evaluate(
            [Definition("prices", true, null, null, """
                {"effects":[
                  {"type":"trader-price-multiplier","traderId":"prapor","multiplier":0.8},
                  {"type":"flea-availability","enabled":false}
                ]}
                """)],
            Now).Active;

        var result = new RecommendationEngine().Recommend(item, price, context, ValueTierThresholds.Default, rules);

        Assert.Equal(80_000, result.SelectedEconomicValue);
        Assert.Equal(SaleChannel.Trader, result.SaleChannel);
        Assert.Equal(RecommendationAction.SellTrader, result.Action);
        Assert.Contains(result.Reasons, reason =>
            reason.Code == RecommendationReasonCode.ActiveEventRule &&
            reason.Explanation.Contains("Prapor prices are x0.8", StringComparison.Ordinal));
    }

    [Fact]
    public void AClosedMapCannotBeTheSuggestedNextRaid()
    {
        NextRaidCandidate[] candidates =
        [
            new("laboratory", "Labs", 4, 5),
            new("customs", "Customs", 2, 3),
        ];
        var rules = EventRuleService.Evaluate(
            [Definition("closure", true, null, null, MapClosed("laboratory"))],
            Now).Active;

        var suggested = NextRaidPlanner.Suggest(candidates, rules);

        Assert.Equal("customs", suggested?.MapKey);
    }

    private static string MapClosed(string mapId) =>
        $$"""{"effects":[{"type":"map-availability","mapId":"{{mapId}}","available":false}]}""";

    private static EventDefinition Definition(
        string id,
        bool active,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string rulesJson) =>
        new(
            id,
            id,
            start,
            end,
            active,
            new HashSet<string>(StringComparer.Ordinal),
            rulesJson,
            new DataProvenance("local event definition", Now));
}
