using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.UnitTests.Localization;

/// <summary>
/// The Application's codes for loadout findings, event rules, traffic hotspots and objective route
/// steps (#314), said in English: each expected string is the sentence the Application built before
/// the words moved to the string table, byte for byte.
/// </summary>
public sealed class ReasonTextTests : IDisposable
{
    private readonly IDisposable _table = UiText.Scope(UiText.Create("en", _ => { }));
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ReasonTextTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        _table.Dispose();
    }

    [Fact]
    public void Every_loadout_finding_has_a_sentence_and_a_reason()
    {
        foreach (var kind in Enum.GetValues<LoadoutFindingKind>())
        {
            var finding = new LoadoutFinding(kind, "Item", "cal", "Other", "cal2", LoadoutSlotWord.Rig, "Rig", "D", 200_000, ["C", "D"], 150_000);
            Assert.False(string.IsNullOrWhiteSpace(PlanText.LoadoutFinding(finding)), kind.ToString());
            Assert.True(PlanText.LoadoutFindingWhy(finding).Length > 20, kind.ToString());
        }
    }

    [Fact]
    public void Loadout_findings_read_as_they_did()
    {
        Assert.Equal(
            "No compatibility data is available for 'unknown-med'.",
            PlanText.LoadoutFinding(new(LoadoutFindingKind.NotInCatalog, Item: "unknown-med")));
        var wrongSlot = new LoadoutFinding(LoadoutFindingKind.WrongSlot, Item: "M4A1", Slot: LoadoutSlotWord.Helmet, Category: "Weapon");
        Assert.Equal("M4A1 is not valid for the helmet slot.", PlanText.LoadoutFinding(wrongSlot));
        Assert.Equal(
            "Each slot takes one kind of item. The catalog files this item under Weapon, which is not what the helmet slot takes, so it belongs in a different slot.",
            PlanText.LoadoutFindingWhy(wrongSlot));
        Assert.Equal(
            "9x19mm PSO gzh (9x19mm Parabellum) does not match AK-74N (5.45x39mm).",
            PlanText.LoadoutFinding(new(LoadoutFindingKind.CaliberMismatch, "9x19mm PSO gzh", "9x19mm Parabellum", "AK-74N", "5.45x39mm")));
        Assert.Equal(
            "PMAG does not accept 9x19mm Parabellum ammunition.",
            PlanText.LoadoutFinding(new(LoadoutFindingKind.MagazineCaliberMismatch, Item: "PMAG", OtherCaliber: "9x19mm Parabellum")));
        var weak = new LoadoutFinding(LoadoutFindingKind.WeakAmmunitionForKit, Tier: "D", Roubles: 1_234_567, WeakTiers: ["C", "D", "E"], ThresholdRoubles: 150_000);
        Assert.Equal("D-tier ammunition is weak relative to this 1,234,567-rouble kit.", PlanText.LoadoutFinding(weak));
        Assert.Equal(
            "A rule of thumb, not a ballistic result: ammunition in tier C or D or E is treated as weak, and a kit costing 150,000 roubles or more is a costly one to load with it. It is shown only when every item in the kit has a price.",
            PlanText.LoadoutFindingWhy(weak));
        Assert.Equal(
            "Armor is selected without any known plate selection.",
            PlanText.LoadoutFinding(new(LoadoutFindingKind.ArmorWithoutPlates)));
    }

    [Fact]
    public void Event_rules_read_as_they_did_and_stop_at_four()
    {
        EventRuleEffect[] effects =
        [
            new TraderPriceMultiplierRule("prapor", "Prapor", 1.25m),
            new FleaAvailabilityRule(false),
            new MapAvailabilityRule("customs", "Customs", true),
            new BossSpawnMultiplierRule("reshala", "Reshala", "customs", 2m),
            new MapAvailabilityRule("lab", "The Lab", false),
        ];

        Assert.Equal(
            "Prapor prices x1.25; Flea closed; Customs open; Reshala spawns x2; +1 more",
            PlanText.EventRulePreview(new EventRuleSet(effects)));
        Assert.Equal("Flea open", PlanText.EventRulePreview(new EventRuleSet([new FleaAvailabilityRule(true)])));
        Assert.Equal(
            "Debut availability unchanged",
            PlanText.EventRulePreview(new EventRuleSet([new QuestAvailabilityWindowRule("debut", "Debut", null, null)])));
    }

    [Fact]
    public void Hotspot_drivers_and_names_read_as_they_did()
    {
        var named = new TrafficHotspot(new MapPoint(0, 0), 0.8, "Dorms", [TrafficDriver.HighValueLoot, TrafficDriver.LinesFromSpawns, TrafficDriver.YourRaids], 10);
        Assert.Equal("high-value loot, lines from spawns, your raids", RaidText.HotspotDrivers(named));
        Assert.Equal("Dorms", RaidText.HotspotName(named));
        Assert.Equal("Unnamed area", RaidText.HotspotName(named with { Name = null }));
        Assert.Equal("Prior from map structure, not recorded raids", RaidText.TrafficSourceClass);
        Assert.Equal(
            "Avoids Unnamed area convergence · peak 90% → 20%",
            RaidText.RouteReason(new(TrafficRouteReasonKind.AvoidsPeak, string.Empty, Share: 0.2, OtherShare: 0.9)));
    }

    [Fact]
    public void Objective_route_reasons_read_as_they_did()
    {
        Assert.Equal(
            "Nearest unvisited objective from player position",
            PlanText.ObjectiveRouteReason(new(ObjectiveRouteReasonKind.NearestFrom, Previous: "player position")));
        Assert.Equal(
            "2-opt moved it from step 3 to shorten the whole route",
            PlanText.ObjectiveRouteReason(new(ObjectiveRouteReasonKind.MovedByTwoOpt, Step: 3)));
        Assert.Equal(
            "Still step 4; 2-opt reordered the stops before it",
            PlanText.ObjectiveRouteReason(new(ObjectiveRouteReasonKind.KeptByTwoOpt, Step: 4)));
        foreach (var kind in Enum.GetValues<ObjectiveRouteReasonKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PlanText.ObjectiveRouteReason(new(kind, "x", 1))));
        }
    }
}
