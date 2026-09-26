using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Situations;
using static TarkovCompanion.UnitTests.Ask.AskFixtures;

namespace TarkovCompanion.UnitTests.Ask;

public sealed class RulesAnswerSourceTests
{
    private static Task<AskAnswer> Ask(string question, RulesAnswerSource? source = null) =>
        (source ?? Source()).AnswerAsync(question, CancellationToken.None);

    private static IEnumerable<Enum> Codes(AskAnswer answer) => answer.Lines.Select(line => line.Code);

    private static Phrase Line(AskAnswer answer, AskLine code) => answer.Lines.First(line => Equals(line.Code, code));

    [Fact]
    public async Task Gunsmith_5_lists_the_quest_its_giver_prerequisite_and_objective_from_the_catalog()
    {
        var answer = await Ask("what do I need for Gunsmith 5");

        Assert.True(answer.IsAnswered);
        Assert.Equal(AskIntent.Needs, answer.Intent);
        Assert.Equal("Gunsmith Master - Part 5", answer.Heading);
        Assert.Equal<object?>(["Mechanic", 40], Line(answer, AskLine.QuestGiverLevel).Arguments);
        Assert.Equal<object?>(["Gunsmith Master - Part 1"], Line(answer, AskLine.QuestAfter).Arguments);
        Assert.Equal<object?>(["Modify an AKMN to comply with the given specifications"], Line(answer, AskLine.QuestObjective).Arguments);
        Assert.Equal(new AskSource(AskSourceKind.Catalog, CatalogTime), answer.Sources[0]);
        Assert.Equal(new AskLink(AskLinkKind.PlanQuest, "Gunsmith Master - Part 5", "Gunsmith Master - Part 5"), Assert.Single(answer.Links));
    }

    [Fact]
    public async Task A_hand_over_objective_says_how_many_and_found_in_raid()
    {
        var answer = await Ask("farming 1 requirements");

        Assert.Equal("Farming - Part 1", answer.Heading);
        Assert.Equal<object?>(["Hand over the found in raid item: Metal fuel tank", 2L], Line(answer, AskLine.QuestObjectiveCountFir).Arguments);
        Assert.Equal<object?>(["Hand over the item: Gas analyzer", 3L], Line(answer, AskLine.QuestObjectiveCount).Arguments);
    }

    [Fact]
    public async Task Lavatory_2_lists_its_items_station_and_loyalty_needs()
    {
        var answer = await Ask("what do I need for lavatory 2");

        Assert.Equal("Lavatory", answer.Heading);
        Assert.Equal<object?>([2], Line(answer, AskLine.HideoutLevel).Arguments);
        var items = answer.Lines.Where(line => Equals(line.Code, AskLine.HideoutItem)).Select(line => line.Arguments[0]).ToArray();
        Assert.Equal<object?>(["Metal fuel tank", "Water filter"], items);
        Assert.Contains(AskLine.HideoutStation, Codes(answer));
        Assert.Equal<object?>(["Loyalty level 1 with Mechanic"], Line(answer, AskLine.HideoutOther).Arguments);
        Assert.Equal(AskLinkKind.PlanHideout, answer.Links[0].Kind);
    }

    [Fact]
    public async Task A_station_without_a_level_means_the_next_one_to_build()
    {
        var answer = await Ask("lavatory needs", Source(hideout: new Dictionary<string, int> { [Toilet] = 2 }));

        Assert.Equal<object?>([3], Line(answer, AskLine.HideoutLevel).Arguments);
        Assert.Equal<object?>(["Expeditionary fuel tank", 2], Line(answer, AskLine.HideoutItem).Arguments);
        Assert.Contains(answer.Sources, source => source.Kind == AskSourceKind.Profile);
    }

    [Fact]
    public async Task Dorms_314_key_says_what_it_opens_and_the_quest_it_is_used_in()
    {
        var card = new V2ItemIntelResult(
            V2IntelKind.Key, Dorm314, "Dorm room 314 marked key", "Dorm 314", null, ItemCategory.Key, 1, 1, true,
            Key: new(null, ["Dorm room 314"], 10, 60_000, "Customs"),
            Keep: new([], []));
        var answer = await Ask("where is Dorms 314 key used", Source(cards: new Dictionary<string, V2ItemIntelResult> { [Dorm314] = card }));

        Assert.True(answer.IsAnswered);
        Assert.Equal("Dorm room 314 marked key", answer.Heading);
        Assert.Equal<object?>(["Dorm room 314", "Customs"], Line(answer, AskLine.KeyOpensOnMap).Arguments);
        Assert.Equal<object?>(["Farming - Part 1"], Line(answer, AskLine.UseKeyQuest).Arguments);
        Assert.DoesNotContain(AskLine.NotNeeded, Codes(answer));
        Assert.Equal(new AskLink(AskLinkKind.IntelItem, Dorm314, "Dorm room 314 marked key"), answer.Links[0]);
    }

    [Fact]
    public async Task LEDX_lists_open_quest_and_hideout_needs_and_trade_uses()
    {
        var card = new V2ItemIntelResult(
            V2IntelKind.Item, Ledx, "LEDX Skin Transilluminator", "LEDX", null, ItemCategory.Barter, 1, 2, true,
            Keep: new([new("Private Clinic", 2, true)], [new("Medstation", 3, 1)]),
            Prices: new(1_000_000, null, null, null, [], CatalogTime));
        var trades = new[]
        {
            new IntelTradeRow("b1", IntelTradeKind.Barter, [new(Ledx, "LEDX Skin Transilluminator", 1)], new("x", "Ophthalmoscope", 1), "Therapist", "Loyalty 3", null, null, null, null, IntelTradeReadiness.Unknown),
        };
        var answer = await Ask("is LEDX needed for anything", Source(cards: new Dictionary<string, V2ItemIntelResult> { [Ledx] = card }, trades: trades));

        Assert.Equal("LEDX Skin Transilluminator", answer.Heading);
        Assert.Equal<object?>(["Private Clinic", 2L], Line(answer, AskLine.UseQuestFir).Arguments);
        Assert.Equal<object?>(["Medstation", 3, 1], Line(answer, AskLine.UseHideout).Arguments);
        Assert.Equal<object?>(["Therapist", "Ophthalmoscope"], Line(answer, AskLine.UseBarter).Arguments);
        Assert.Equal(new AskSource(AskSourceKind.Catalog, CatalogTime), answer.Sources[0]);
        Assert.Contains(answer.Sources, source => source.Kind == AskSourceKind.Profile);
    }

    [Fact]
    public async Task An_item_nothing_needs_says_so_rather_than_saying_nothing()
    {
        var answer = await Ask("is gas analyzer needed for anything");

        Assert.Equal("Gas analyzer", answer.Heading);
        Assert.Contains(AskLine.NotNeeded, Codes(answer));
    }

    [Fact]
    public async Task Where_to_get_an_item_lists_traders_barters_and_the_flea()
    {
        var card = new V2ItemIntelResult(
            V2IntelKind.Item, GasAnalyzer, "Gas analyzer", "GasAn", null, ItemCategory.Barter, 1, 2, true,
            Prices: new(21_000, null, null, null, [], CatalogTime),
            Acquisitions:
            [
                new(new(GasAnalyzer, ItemAcquisitionKind.Cash, "t1", "Therapist", 2, null, null, 24_000, []), new(false, "LL2 Therapist")),
                new(new(GasAnalyzer, ItemAcquisitionKind.Barter, "t2", "Mechanic", 1, null, null, null, [new("b", "Bolts", 2)]), new(true, "Available now")),
            ]);
        var answer = await Ask("where can I buy a Gas analyzer", Source(cards: new Dictionary<string, V2ItemIntelResult> { [GasAnalyzer] = card }));

        Assert.Equal(AskIntent.ItemSources, answer.Intent);
        Assert.Equal<object?>(["Mechanic", " LL1", "2 × Bolts"], answer.Lines[0].Arguments);
        Assert.Equal(AskLine.SourceBarter, answer.Lines[0].Code);
        Assert.Equal<object?>(["Therapist", " LL2", 24_000L], Line(answer, AskLine.SourceCashLocked).Arguments);
        Assert.Equal<object?>([21_000L], Line(answer, AskLine.SourceFlea).Arguments);
    }

    [Fact]
    public async Task Best_extract_prefers_an_offered_exit_then_the_nearest_and_says_its_needs()
    {
        var taken = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var raid = new FakeRaid(new AskRaidSnapshot(
            "Customs",
            SituationSide.Pmc,
            taken,
            "Dorms",
            [
                new("ZB-1011", 180, "NE", WasOffered: false, IsTransit: false, null),
                new("Crossroads", 620, "W", WasOffered: true, IsTransit: false, null),
                new("Smuggler's Boat", 300, "S", WasOffered: false, IsTransit: false,
                    new MapExtractRequirements([], null, false, false) { Conditions = [new(MapExtractConditionKind.NoBackpack, [], null)] }),
                new("Transit to Factory", 50, "N", WasOffered: false, IsTransit: true, null),
            ]));
        var answer = await Ask("best extract from here", Source(raid: raid));

        Assert.Equal("Crossroads", answer.Heading);
        Assert.Equal<object?>(["Customs", AskSideWord.Pmc], Line(answer, AskLine.ExtractOnMap).Arguments);
        Assert.Equal<object?>(["Dorms"], Line(answer, AskLine.ExtractYouAt).Arguments);
        var exits = answer.Lines.Where(line => line.Code is AskLine.ExtractAt or AskLine.ExtractAtOffered).Select(line => line.Arguments[0]).ToArray();
        Assert.Equal<object?>(["Crossroads", "ZB-1011", "Smuggler's Boat"], exits);
        Assert.Contains(AskLine.ExtractNeedsNoBackpack, Codes(answer));
        Assert.Contains(AskLine.ExtractStraightLines, Codes(answer));
        Assert.Contains(new AskSource(AskSourceKind.Screenshot, taken), answer.Sources);
        Assert.Contains(answer.Sources, source => source.Kind == AskSourceKind.Modelled);
        Assert.Equal(AskLinkKind.Raid, answer.Links[0].Kind);
    }

    [Fact]
    public async Task No_raid_map_means_no_extract_answer()
    {
        var answer = await Ask("best extract from here", Source(raid: new FakeRaid(null)));

        Assert.False(answer.IsAnswered);
        Assert.Equal(AskUnanswered.NoRaid, answer.Unanswered);
        Assert.Empty(answer.Lines);
    }

    [Fact]
    public async Task Best_545_for_class_4_ranks_by_penetration_and_labels_the_rule_modelled()
    {
        var answer = await Ask("best 5.45 for class 4");

        Assert.Equal(AskIntent.Ammo, answer.Intent);
        var picks = answer.Lines.Where(line => Equals(line.Code, AskLine.AmmoPick)).Select(line => line.Arguments[0]).ToArray();
        Assert.Equal<object?>(["5.45x39mm PPBS gs \"Igolnik\"", "5.45x39mm BS gs"], picks);
        Assert.Equal<object?>([40, 4], Line(answer, AskLine.AmmoRule).Arguments);
        Assert.Contains(answer.Sources, source => source.Kind == AskSourceKind.Modelled);
    }

    [Fact]
    public async Task A_price_cap_leaves_out_rounds_that_cost_more()
    {
        var answer = await Ask("best 7.62x39 under 1k");

        var picks = answer.Lines.Where(line => Equals(line.Code, AskLine.AmmoPickPriced)).ToArray();
        Assert.Equal<object?>(["7.62x39mm BP gzh", 47, 58, 900L], picks[0].Arguments);
        Assert.DoesNotContain(picks, line => (string)line.Arguments[0]! == "7.62x54mm R PS gzh");
    }

    [Fact]
    public async Task A_caliber_that_names_several_families_asks_which_one()
    {
        var answer = await Ask("best 7.62 for class 5");

        Assert.Equal(AskUnanswered.Ambiguous, answer.Unanswered);
        Assert.Equal(2, answer.Closest.Count);
        Assert.Empty(answer.Lines);
    }

    [Theory]
    [InlineData("what is the meaning of life")]
    [InlineData("how many raids did I do")]
    public async Task A_question_outside_the_grammar_gets_no_invented_answer(string question)
    {
        var answer = await Ask(question);

        Assert.False(answer.IsAnswered);
        Assert.Equal(AskUnanswered.NotUnderstood, answer.Unanswered);
        Assert.Empty(answer.Lines);
        Assert.Empty(answer.Links);
    }

    [Fact]
    public async Task A_name_nothing_is_called_offers_the_closest_names_and_no_answer()
    {
        var answer = await Ask("what do I need for Gunsmith 99");

        Assert.Equal(AskUnanswered.NoMatch, answer.Unanswered);
        Assert.Empty(answer.Lines);
        Assert.Contains(answer.Closest, name => name.StartsWith("Gunsmith Master", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_weak_item_match_is_not_answered_about()
    {
        var answer = await Ask("where is the zzq flux capacitor used");

        Assert.Equal(AskUnanswered.NoMatch, answer.Unanswered);
        Assert.Empty(answer.Lines);
    }

    [Fact]
    public async Task The_service_does_not_consult_a_local_model_that_is_switched_off()
    {
        var local = new CountingLocalModel();
        var service = new AskService([Source(), local], new Settings(false));

        var answer = await service.AskAsync("what is the meaning of life", CancellationToken.None);

        Assert.False(answer.IsAnswered);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task A_switched_on_local_model_is_asked_only_what_the_rules_did_not_understand_and_is_labelled()
    {
        var local = new CountingLocalModel();
        var service = new AskService([Source(), local], new Settings(true));

        var understood = await service.AskAsync("what do I need for Gunsmith 99", CancellationToken.None);
        var free = await service.AskAsync("what is the meaning of life", CancellationToken.None);

        Assert.False(understood.IsAnswered);
        Assert.Equal(1, local.Calls);
        Assert.True(free.IsAnswered);
        Assert.Equal(AskSourceKind.LocalModel, free.Sources[^1].Kind);
    }

    private sealed record Settings(bool LocalModelEnabled) : IAskSettings;

    private sealed class CountingLocalModel : IAnswerSource
    {
        public int Calls { get; private set; }

        public AnswerSourceKind Kind => AnswerSourceKind.LocalModel;

        public System.Threading.Tasks.Task<AskAnswer> AnswerAsync(string question, CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new AskAnswer(null, "42", [], [], []));
        }
    }
}
