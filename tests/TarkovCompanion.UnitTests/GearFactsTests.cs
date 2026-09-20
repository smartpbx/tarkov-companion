using System.Globalization;
using System.Text.Json;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Intelligence.Gear;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Gear;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loadouts;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The shapes below are the property blobs the synced json.tarkov.dev catalog holds for real gear
/// (public game data, trimmed): a plate, a body armor with plate slots, a rig, a headset, a
/// medical item. What the blob does not say has to stay unknown.
/// </summary>
public sealed class GearFactsTests
{
    private static readonly DataProvenance Source = new("json.tarkov.dev", DateTimeOffset.UnixEpoch);

    private const string PlateJson = """
        {"bluntThroughput":0.28,"class":6,"durability":60,"repairCost":289,"speedPenalty":-0.02,
         "turnPenalty":-0.015,"ergoPenalty":-0.015,"blindnessProtection":0,"armorType":"Heavy",
         "slots":[],"armorSlots":[],"propertiesType":"ItemPropertiesArmorAttachment",
         "zones":["Front plate","Back plate"],"material":"Ceramic"}
        """;

    private const string BodyArmorJson = """
        {"speedPenalty":-0.09,"class":6,"durability":510,"zones":["Front plate","Thorax"],"armorType":"Heavy",
         "material":"Aramid","propertiesType":"ItemPropertiesArmor",
         "armorSlots":[
           {"nameId":"Front_plate","zones":["Front plate"],"allowedPlates":["plate-a","plate-b"]},
           {"nameId":"Back_plate","zones":["Back plate"],"allowedPlates":["plate-a"]},
           {"nameId":"Collar","zones":["Neck"]}]}
        """;

    [Fact]
    public void A_plates_stated_figures_are_read_as_stated()
    {
        var facts = Read("plate-a", ItemCategory.Plate, PlateJson);

        Assert.Equal(6, facts.ArmorClass);
        Assert.Equal(60, facts.Durability);
        Assert.Equal("Ceramic", facts.Material);
        Assert.Equal(0.28, facts.BluntThroughput);
        Assert.Equal(-0.015, facts.ErgoPenalty);
        Assert.Equal(["Front plate", "Back plate"], facts.Zones);
        Assert.Empty(facts.PlateSlots);
        Assert.Null(facts.CarryCells);
        Assert.Null(facts.Audio);
    }

    [Fact]
    public void A_body_armors_plate_slots_keep_the_plates_the_source_says_fit_each()
    {
        var facts = Read("armor", ItemCategory.Armor, BodyArmorJson);

        // The collar has no plate list: it is a protected zone, not a place a plate goes.
        Assert.Equal(["Front_plate", "Back_plate"], facts.PlateSlots.Select(slot => slot.SlotId));
        Assert.Equal(new[] { "plate-a", "plate-b" }, facts.PlateSlots[0].AllowedPlateItemIds.Order());
        Assert.Equal(new[] { "plate-a" }, facts.PlateSlots[1].AllowedPlateItemIds.Order());
    }

    [Fact]
    public void Rigs_headsets_and_medicine_read_their_own_keys()
    {
        Assert.Equal(20, Read("rig", ItemCategory.Rig, """{"capacity":20,"ergoPenalty":-0.01}""").CarryCells);

        var audio = Read(
            "headset",
            ItemCategory.Headset,
            """{"ambientVolume":-50,"compressorGain":3,"compressorAttack":35,"distanceModifier":1.03,"distortion":0.15}""").Audio;
        Assert.NotNull(audio);
        Assert.Equal(-50, audio.AmbientVolume);
        Assert.Equal(1.03, audio.DistanceModifier);
        Assert.Null(audio.DryVolume);

        var medicine = Read("bandage", ItemCategory.Medicine, """{"uses":1,"useTime":2,"cures":["LightBleeding"]}""");
        Assert.Equal(1, medicine.Uses);
        Assert.Equal(2, medicine.UseTimeSeconds);
        Assert.Equal(["LightBleeding"], medicine.Cures);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"class":"six","durability":"sixty","zones":"Front plate","capacity":null}""")]
    [InlineData("""{"class":99,"durability":-5,"ergoPenalty":7,"capacity":-1}""")]
    [InlineData("""[1,2,3]""")]
    public void What_the_blob_does_not_state_correctly_stays_unknown_not_zero(string json)
    {
        var facts = Read("armor", ItemCategory.Armor, json);

        Assert.Null(facts.ArmorClass);
        Assert.Null(facts.Durability);
        Assert.Null(facts.ErgoPenalty);
        Assert.Null(facts.CarryCells);
        Assert.Empty(facts.Zones);
        Assert.Null(GearFactsReader.Summarize(facts));
    }

    [Fact]
    public void Gear_without_a_blob_still_has_facts_and_every_figure_is_unknown()
    {
        var facts = GearFactsReader.Read("plate-x", ItemCategory.Plate, null, Source);

        Assert.NotNull(facts);
        Assert.Null(facts.ArmorClass);
        Assert.Null(facts.Durability);
    }

    [Theory]
    [InlineData(ItemCategory.Weapon)]
    [InlineData(ItemCategory.Ammunition)]
    [InlineData(ItemCategory.Barter)]
    [InlineData(ItemCategory.Key)]
    public void Things_that_are_not_gear_have_no_gear_facts(ItemCategory category) =>
        Assert.Null(GearFactsReader.Read("x", category, null, Source));

    [Fact]
    public void The_summary_is_the_known_figures_in_one_line()
    {
        Assert.Equal(
            "Class 6 · Ceramic · 60 durability · ergo -1.5% · speed -2%",
            GearFactsReader.Summarize(Read("plate-a", ItemCategory.Plate, PlateJson)));
        Assert.Equal("20 cells · ergo -1%", GearFactsReader.Summarize(
            Read("rig", ItemCategory.Rig, """{"capacity":20,"ergoPenalty":-0.01}""")));
        // A headset has audio settings and nothing else to say, and they are shown by the source's names.
        Assert.Equal("gain 3 · distance 1.03 · ambient -50", GearFactsReader.Summarize(Read(
            "headset",
            ItemCategory.Headset,
            """{"ambientVolume":-50,"compressorGain":3,"distanceModifier":1.03}""")));
    }

    private static GearFacts Read(string id, ItemCategory category, string json)
    {
        using var document = JsonDocument.Parse(json);
        return GearFactsReader.Read(id, category, document.RootElement.Clone(), Source)!;
    }
}

public sealed class LoadoutCoverageTests
{
    private static LoadoutItemFacts Item(string id, ItemCategory category, long? cost, double? weight) =>
        new(id, id, category, cost, weight, null, new HashSet<string>(), new HashSet<string>());

    private static LoadoutSelection Kit(params string[] medicalIds) =>
        new(null, null, [], null, [], null, null, null, null, medicalIds);

    private static LoadoutIntelligenceService Service(
        IEnumerable<LoadoutItemFacts> catalog,
        AmmoKitWarningPolicy? policy = null) =>
        new(catalog, new AmmoIntelligenceService([]), policy);

    [Fact]
    public async Task A_kit_missing_one_price_is_a_floor_over_the_items_that_have_one_and_says_so()
    {
        var catalog = Enumerable.Range(1, 9)
            .Select(index => Item($"med-{index}", ItemCategory.Medicine, index == 9 ? null : 1_000 * index, 0.1))
            .ToArray();

        var result = await Service(catalog).EvaluateAsync(
            Kit(catalog.Select(item => item.ItemId).ToArray()), null, CancellationToken.None);

        Assert.Null(result.ApproximateCostRoubles);
        Assert.Equal(36_000, result.KnownCostRoubles);
        Assert.Equal(new LoadoutCoverage(8, 9), result.CostCoverage);
        Assert.False(result.CostCoverage.IsComplete);
        Assert.Equal(0.9, result.ApproximateWeightKg!.Value, 6);
        Assert.True(result.WeightCoverage.IsComplete);
    }

    [Fact]
    public async Task Nothing_priced_is_no_figure_and_never_a_zero()
    {
        var result = await Service([Item("med", ItemCategory.Medicine, null, null)])
            .EvaluateAsync(Kit("med"), null, CancellationToken.None);

        Assert.Null(result.KnownCostRoubles);
        Assert.Null(result.KnownWeightKg);
        Assert.Equal(new LoadoutCoverage(0, 1), result.CostCoverage);
    }

    [Fact]
    public async Task An_item_the_catalog_does_not_know_lowers_coverage_and_the_rest_still_add_up()
    {
        var result = await Service([Item("med", ItemCategory.Medicine, 5_000, 0.2)])
            .EvaluateAsync(Kit("med", "unknown-med"), null, CancellationToken.None);

        Assert.Null(result.ApproximateCostRoubles);
        Assert.Equal(5_000, result.KnownCostRoubles);
        Assert.Equal(new LoadoutCoverage(1, 2), result.CostCoverage);
        Assert.Contains(result.CompatibilityIssues, issue => issue.Contains("unknown-med", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_item_listed_three_times_is_three_items_of_price_weight_and_coverage()
    {
        var result = await Service([Item("stim", ItemCategory.Medicine, 7_000, 0.05)])
            .EvaluateAsync(Kit("stim", "stim", "stim"), null, CancellationToken.None);

        Assert.Equal(21_000, result.ApproximateCostRoubles);
        Assert.Equal(0.15, result.ApproximateWeightKg!.Value, 6);
        Assert.Equal(new LoadoutCoverage(3, 3), result.CostCoverage);
    }

    [Fact]
    public async Task The_ammo_warning_uses_the_policy_it_is_given_and_never_a_partial_total()
    {
        var ammo = AmmoIntelligenceTests.AmmoFixtures();
        var catalog = new[]
        {
            new LoadoutItemFacts("ammo-5", "Round", ItemCategory.Ammunition, 60_000, 0.1, null, new HashSet<string>(), new HashSet<string>()),
            Item("med", ItemCategory.Medicine, 60_000, 0.1),
            Item("unpriced", ItemCategory.Medicine, null, 0.1),
        };
        var selection = new LoadoutSelection(null, "ammo-5", [], null, [], null, null, null, null, ["med"]);

        // 120,000 is under the default 150,000, so the default says nothing.
        var quiet = await new LoadoutIntelligenceService(catalog, new AmmoIntelligenceService(ammo))
            .EvaluateAsync(selection, null, CancellationToken.None);
        Assert.DoesNotContain(quiet.Warnings, warning => warning.Contains("weak relative", StringComparison.Ordinal));

        // A player who thinks 100,000 is a lot for weak ammunition can say so.
        var strict = new AmmoKitWarningPolicy(new HashSet<string>(StringComparer.Ordinal) { "C", "D" }, 100_000);
        var warned = await new LoadoutIntelligenceService(catalog, new AmmoIntelligenceService(ammo), strict)
            .EvaluateAsync(selection, null, CancellationToken.None);
        Assert.Contains(warned.Warnings, warning => warning.Contains("weak relative", StringComparison.Ordinal));

        // With one price missing there is no complete total, so there is no warning built on a partial one.
        var partial = await new LoadoutIntelligenceService(catalog, new AmmoIntelligenceService(ammo), strict)
            .EvaluateAsync(selection with { MedicalItemIds = ["med", "unpriced"] }, null, CancellationToken.None);
        Assert.DoesNotContain(partial.Warnings, warning => warning.Contains("weak relative", StringComparison.Ordinal));
    }
}

/// <summary>What the Loadout page says about a total that is missing some of its figures.</summary>
public sealed class LoadoutTotalsWordingTests
{
    private static LoadoutEvaluation Evaluation(
        long? total, long? known, LoadoutCoverage cost, double? weight, double? knownWeight, LoadoutCoverage weighed) =>
        new(total, weight, true, [], [], "Unknown", cost, weighed, known, knownWeight);

    [Fact]
    public void A_complete_total_is_just_the_figure()
    {
        using var culture = new CultureScope();
        var kit = Evaluation(227_000, 227_000, new(9, 9), 13, 13, new(9, 9));

        Assert.Equal("227,000 ₽", LoadoutPageViewModel.DescribeCost(kit));
        Assert.Equal("13.00 kg", LoadoutPageViewModel.DescribeWeight(kit));
    }

    [Fact]
    public void A_total_missing_some_figures_is_a_floor_that_says_how_many_it_covers()
    {
        using var culture = new CultureScope();
        var kit = Evaluation(null, 36_000, new(8, 9), null, 0.9, new(7, 9));

        Assert.Equal("At least 36,000 ₽ · 8 of 9 priced", LoadoutPageViewModel.DescribeCost(kit));
        Assert.Equal("At least 0.90 kg · 7 of 9 weighed", LoadoutPageViewModel.DescribeWeight(kit));
    }

    [Fact]
    public void A_kit_with_no_figures_at_all_is_not_a_zero()
    {
        using var culture = new CultureScope();
        var kit = Evaluation(null, null, new(0, 3), null, null, new(0, 3));

        Assert.Equal("No total · 0 of 3 priced", LoadoutPageViewModel.DescribeCost(kit));
        Assert.Equal("No total · 0 of 3 weighed", LoadoutPageViewModel.DescribeWeight(kit));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}

/// <summary>
/// Every compatibility issue and warning says why it was raised, in fixed offline wording (the
/// ammunition page already does this as Learn Mode). A rule that fires with no reason is a verdict
/// the player cannot check, and the numbers in the ammunition warning are the policy's own.
/// </summary>
public sealed class LoadoutExplanationTests
{
    private static readonly HashSet<string> None = [];

    private static LoadoutItemFacts Item(
        string id, ItemCategory category, long? cost = 1_000, string? caliber = null,
        HashSet<string>? weapons = null, HashSet<string>? parents = null, GearFacts? gear = null) =>
        new(id, id, category, cost, 1, caliber, weapons ?? None, parents ?? None, gear);

    private static GearFacts Armor(string id, int? armorClass, params PlateSlot[] slots) =>
        new(id, ItemCategory.Armor, armorClass, null, null, null, [], null, null, null, null, null, null, null, null,
            [], null, slots, new DataProvenance("test", DateTimeOffset.UnixEpoch));

    private static LoadoutIntelligenceService Service(IEnumerable<LoadoutItemFacts> catalog, AmmoKitWarningPolicy? policy = null) =>
        new(catalog, new AmmoIntelligenceService(AmmoIntelligenceTests.AmmoFixtures()), policy);

    [Fact]
    public async Task Every_issue_and_warning_a_kit_raises_carries_a_reason()
    {
        var catalog = new[]
        {
            Item("weapon", ItemCategory.Weapon, caliber: "A"),
            Item("ammo-5", ItemCategory.Ammunition, caliber: "B"),
            Item("mag", ItemCategory.Attachment, caliber: "C", weapons: ["other-weapon"]),
            Item("armor", ItemCategory.Armor),
            Item("plate", ItemCategory.Plate, parents: ["other-armor"]),
            Item("med", ItemCategory.Medicine),
        };
        var selection = new LoadoutSelection(
            "weapon", "ammo-5", ["mag"], "armor", ["plate"], "med", null, null, null, ["not-in-catalog"]);

        var result = await Service(catalog).EvaluateAsync(selection, null, CancellationToken.None);

        var raised = result.CompatibilityIssues.Concat(result.Warnings).ToArray();
        Assert.True(raised.Length >= 6, string.Join(" | ", raised));
        Assert.NotNull(result.Explanations);
        foreach (var message in raised)
        {
            Assert.True(
                result.Explanations.TryGetValue(message, out var why) && why.Length > 20,
                $"No reason for: {message}");
        }

    }

    [Fact]
    public async Task The_ammunition_warning_explains_itself_with_the_policys_own_numbers()
    {
        var catalog = new[]
        {
            Item("ammo-5", ItemCategory.Ammunition, cost: 60_000),
            Item("med", ItemCategory.Medicine, cost: 60_000),
        };
        var policy = new AmmoKitWarningPolicy(new HashSet<string>(StringComparer.Ordinal) { "C", "D" }, 100_000);

        var result = await Service(catalog, policy).EvaluateAsync(
            new(null, "ammo-5", [], null, [], null, null, null, null, ["med"]), null, CancellationToken.None);

        var warning = Assert.Single(result.Warnings, message => message.Contains("weak relative", StringComparison.Ordinal));
        var why = result.Explanations![warning];
        Assert.Contains("C or D", why, StringComparison.Ordinal);
        Assert.Contains("100,000", why, StringComparison.Ordinal);
        Assert.Contains("rule of thumb", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 0, false)] // a soft vest: a stated class and nowhere to put a plate
    [InlineData(6, 1, true)]  // plate carrier with a plate slot
    [InlineData(null, 0, true)] // nothing stated about the armor: no facts is not "no plate slots"
    public async Task Armor_without_plates_warns_only_when_the_catalog_says_it_takes_plates(
        int? armorClass, int plateSlots, bool warns)
    {
        var slots = Enumerable.Range(0, plateSlots)
            .Select(index => new PlateSlot($"slot-{index}", [], new HashSet<string> { "plate" }))
            .ToArray();
        var catalog = new[] { Item("armor", ItemCategory.Armor, gear: Armor("armor", armorClass, slots)) };

        var result = await Service(catalog).EvaluateAsync(
            new(null, null, [], "armor", [], null, null, null, null, []), null, CancellationToken.None);

        var warning = result.Warnings.SingleOrDefault(w => w.Contains("without any known plate", StringComparison.Ordinal));
        Assert.Equal(warns, warning is not null);
        if (warning is not null)
        {
            Assert.Contains("plate slots", result.Explanations![warning], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_page_attaches_the_reason_to_the_finding_and_leaves_it_empty_when_there_is_none()
    {
        var evaluation = new LoadoutEvaluation(
            null, null, false, ["bad"], [], "Unknown", Explanations: new Dictionary<string, string> { ["bad"] = "because" });

        Assert.Equal("because", LoadoutPageViewModel.Finding(evaluation, "bad").Explanation);
        Assert.Equal(string.Empty, LoadoutPageViewModel.Finding(evaluation, "other").Explanation);
        Assert.Equal(string.Empty, LoadoutPageViewModel.Finding(evaluation with { Explanations = null }, "bad").Explanation);
    }
}
