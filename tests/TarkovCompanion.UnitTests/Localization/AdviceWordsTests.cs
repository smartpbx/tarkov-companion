using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;

using RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;
using RecommendationReason = TarkovCompanion.Core.Abstractions.V2.RecommendationReason;

namespace TarkovCompanion.UnitTests.Localization;

/// <summary>
/// The recommendation and loot-scan sentences as codes (#314). The engine and planner tests check
/// every result they build through <see cref="AdviceWordsCheck"/>; this covers the photographed
/// flea offer, which those tests do not evaluate, and the pieces around the words.
/// </summary>
public sealed class AdviceWordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(100_000L, 0.6, RecommendationAction.Take)]
    [InlineData(100_000L, null, RecommendationAction.Take)]
    [InlineData(120_000L, 1.0, RecommendationAction.Leave)]
    [InlineData(150_000L, null, RecommendationAction.Leave)]
    public void A_flea_offer_is_said_as_before_in_English(long offer, double? condition, RecommendationAction expected)
    {
        var result = new ExplainableRecommendationEngine().EvaluateFleaOffer(FleaOffer(offer, condition, fleaNet: 120_000));

        Assert.Equal(expected, result.Decision.Value!.Action);
        AdviceWordsCheck.SaidAsBefore(result);
    }

    [Fact]
    public void A_flea_offer_sold_to_a_trader_or_left_unread_is_said_as_before_in_English()
    {
        var trader = new ExplainableRecommendationEngine().EvaluateFleaOffer(FleaOffer(50_000, 0.25, fleaNet: null));
        var unread = new ExplainableRecommendationEngine().EvaluateFleaOffer(FleaOffer(50_000, null, fleaNet: 120_000, identity: null));

        Assert.Contains("best trader", trader.Decision.Value!.Reasons[0].Explanation, StringComparison.Ordinal);
        Assert.Equal(RecommendationAction.Review, unread.Decision.Value!.Action);
        AdviceWordsCheck.SaidAsBefore(trader);
        AdviceWordsCheck.SaidAsBefore(unread);
    }

    [Fact]
    public void Another_language_changes_the_words_and_not_the_stored_English()
    {
        var result = new ExplainableRecommendationEngine().EvaluateFleaOffer(FleaOffer(150_000, 0.6, fleaNet: 120_000));
        var reason = result.Decision.Value!.Reasons[0];

        using var scope = UiText.Scope(UiText.Create(PseudoLocale.Name, _ => { }));
        Assert.NotEqual(reason.Explanation, AdviceText.Reason(reason));
        Assert.StartsWith("The offer costs", reason.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_read_back_without_words_shows_its_English_and_still_equals_the_fresh_one()
    {
        var provenance = Provenance("reason");
        var stored = new RecommendationReason(RecommendationReasonCategory.PinOrWishlist, "profile.pinned", "You pinned this item.", 10, provenance);
        var fresh = stored.WithWords(new Phrase(AdviceSentence.Pinned));
        var loot = new LootScanReason("capacity.visible-fit", "The item fits in verified visible carried space.");

        using var scope = UiText.Scope(UiText.Create(PseudoLocale.Name, _ => { }));
        Assert.Equal(stored, fresh);
        Assert.Equal(stored.GetHashCode(), fresh.GetHashCode());
        Assert.Equal("You pinned this item.", AdviceText.Reason(stored));
        Assert.NotEqual("You pinned this item.", AdviceText.Reason(fresh));
        Assert.Equal(loot.Explanation, AdviceText.Reason(loot));
        Assert.Equal(loot, loot.WithWords(new Phrase(LootScanSentence.VisibleFit)));
    }

    [Theory]
    [InlineData(typeof(RecommendationAction), typeof(AdviceSummary))]
    [InlineData(typeof(RecommendationAction), typeof(AdviceActionWord))]
    [InlineData(typeof(EconomicValueBand), typeof(AdviceBandWord))]
    [InlineData(typeof(RecommendationObtainabilityBand), typeof(AdviceObtainabilityWord))]
    [InlineData(typeof(RecommendationRaidPhase), typeof(AdvicePhaseWord))]
    [InlineData(typeof(RecommendationRaidRisk), typeof(AdviceRiskWord))]
    public void Every_engine_value_has_a_word_of_the_same_name(Type engine, Type words)
    {
        Assert.Equal(Enum.GetNames(engine).Order(StringComparer.Ordinal), Enum.GetNames(words).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_summary_names_the_action_as_the_engine_always_has()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var reason = new Phrase(AdviceSentence.Pinned);
        foreach (var action in Enum.GetValues<RecommendationAction>())
        {
            Assert.Equal(
                $"{action}: You pinned this item.",
                PhraseText.Say(new Phrase(AdviceWords.Of<AdviceSummary>(action), reason)));
            Assert.Equal(
                $"Your explicit item rule says {action.ToString().ToLowerInvariant()}.",
                PhraseText.Say(new Phrase(AdviceSentence.OverrideExplicit, AdviceWords.Of<AdviceActionWord>(action))));
        }
    }

    private static FleaOfferRecommendationRequest FleaOffer(long offer, double? condition, long? fleaNet, string? identity = "item-a") => new(
        "flea-offer-test",
        new EvidencedValue<string>(
            "flea.identity",
            identity!,
            identity is null ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current) : Complete,
            Provenance("identity")),
        Now,
        new CaptureSessionId(Guid.Parse("74000000-0000-0000-0000-000000000314")),
        Value<long>("flea.offer", offer),
        new RecommendationEconomics(
            Value<long>("economics.flea-gross", fleaNet is null ? null : fleaNet + 20_000),
            Value<long>("economics.flea-fee", fleaNet is null ? null : 20_000),
            fleaNet is null
                ? new EvidencedValue<long?>("economics.flea-net", null, new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Current), Provenance("economics.flea-net"))
                : Value<long>("economics.flea-net", fleaNet),
            Value<long>("economics.trader", 80_000),
            Value<int>("economics.squares", 2),
            Value<double>("economics.condition", condition)));

    private static EvidencedValue<T?> Value<T>(string fieldId, T? value)
        where T : struct => new(
        fieldId,
        value,
        value is null ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current) : Complete,
        Provenance(fieldId));

    private static ResultStatus Complete { get; } = new(ResultCompleteness.Complete, FreshnessState.Current);

    private static EvidenceProvenance Provenance(string id) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://{id}",
        Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.98),
        new ProducerIdentity("advice-words-fixture", "1"));
}
