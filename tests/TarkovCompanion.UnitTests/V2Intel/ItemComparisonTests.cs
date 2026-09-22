using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Gear;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.V2Intel;

public sealed class ItemComparisonTests
{
    [Fact]
    public void AmmoMarksTheHardestHittingAndTheCheapestRound()
    {
        var table = ItemComparisonBuilder.Build(
        [
            Ammo("m855", damage: 54, penetration: 31, flea: 300, ratings: new() { [3] = ArmorEffectiveness.Good, [4] = ArmorEffectiveness.Limited }),
            Ammo("m995", damage: 42, penetration: 53, flea: 1_200, ratings: new() { [5] = ArmorEffectiveness.Excellent }),
        ]);

        Assert.Equal(ItemComparisonKind.Ammo, table.Kind);
        Assert.Equal([true, false], Best(table, "Damage"));
        Assert.Equal([false, true], Best(table, "Penetration"));
        Assert.Equal([true, false], Best(table, "Flea price"));
        Assert.Equal(["5.56x45mm NATO", "5.56x45mm NATO"], Texts(table, "Caliber"));
        Assert.Equal(["Class 3", "Class 5"], Texts(table, "Beats armor"));
        Assert.Equal([false, true], Best(table, "Beats armor"));
    }

    [Fact]
    public void AnUnknownValueIsADashAndNeverBest()
    {
        var table = ItemComparisonBuilder.Build(
        [
            Ammo("a", damage: 40, penetration: 20, flea: null),
            Ammo("b", damage: 50, penetration: 30, flea: 500),
        ]);

        Assert.Equal(["—", "₽500"], Texts(table, "Flea price"));
        // One known price is not a comparison, so it is not marked best.
        Assert.Equal([false, false], Best(table, "Flea price"));
    }

    [Fact]
    public void EqualValuesHaveNoBestAndTiedLeadersShareIt()
    {
        var equal = ItemComparisonBuilder.Build([Ammo("a", 50, 30, 100), Ammo("b", 50, 20, 200)]);
        Assert.Equal([false, false], Best(equal, "Damage"));

        var tied = ItemComparisonBuilder.Build([Ammo("a", 60, 30, 100), Ammo("b", 60, 20, 200), Ammo("c", 40, 10, 50)]);
        Assert.Equal([true, true, false], Best(tied, "Damage"));
    }

    [Fact]
    public void ArmorPrefersHigherClassAndLighterWeight()
    {
        var table = ItemComparisonBuilder.Build(
        [
            Armor("paca", armorClass: 2, durability: 100, weight: 3.5, "Stomach", "Thorax, Upper back"),
            Armor("zabralo", armorClass: 6, durability: 510, weight: 10.8, "Thorax", "Stomach", "Head"),
        ]);

        Assert.Equal(ItemComparisonKind.Armor, table.Kind);
        Assert.Equal([false, true], Best(table, "Armor class"));
        Assert.Equal([true, false], Best(table, "Weight"));
        Assert.Equal(["Stomach, Thorax", "Thorax, Stomach, Head"], Texts(table, "Covers"));
    }

    [Fact]
    public void KeysPreferMoreUsesHigherValueAndCheaperToBuy()
    {
        var table = ItemComparisonBuilder.Build(
        [
            Key("marked", uses: 10, value: 90_000_000, cost: null),
            Key("dorm", uses: 40, value: 1_000_000, cost: 2_000_000),
        ]);

        Assert.Equal(ItemComparisonKind.Key, table.Kind);
        Assert.Equal([false, true], Best(table, "Uses"));
        Assert.Equal([true, false], Best(table, "Value"));
    }

    [Fact]
    public void LocksKnownOnlyByIdAreCountedNotPrinted()
    {
        var table = ItemComparisonBuilder.Build(
        [
            Key("a", 10, 1, null, "56f40101d2720b2a4d8b45d6:449af4c24c447ce4e49fb3e1"),
            Key("b", 10, 1, null),
        ]);

        Assert.Equal(["1 lock", "Room 314"], Texts(table, "Opens"));
    }

    [Fact]
    public void NeededUsesTheSameNamedQuestRowsAsTheIntelHeadline()
    {
        var overReported = Key("dorm", 10, 1, null) with
        {
            Intel = Key("dorm", 10, 1, null).Intel with
            {
                Value = new V2IntelValueFacts(1, "Flea", 225, 0, 0, OutstandingItems: 225),
                Keep = new V2IntelKeepFacts([], []),
            },
        };

        var table = ItemComparisonBuilder.Build([overReported]);

        Assert.Equal(["Not needed"], Texts(table, "Needed"));
        Assert.Equal(0, V2IntelNeed.Remaining(overReported.Intel));
    }

    [Fact]
    public void MixedKindsCompareAsItems()
    {
        var table = ItemComparisonBuilder.Build([Ammo("a", 50, 30, 100), Key("k", 10, 5_000, null)]);

        Assert.Equal(ItemComparisonKind.Item, table.Kind);
        Assert.Contains(table.Rows, row => row.Label == "Per slot");
    }

    [Fact]
    public async Task TheTrayTakesThreeAndOpensFromTwo()
    {
        var facts = new Dictionary<string, ItemComparisonFacts>
        {
            ["a"] = Ammo("a", 50, 30, 100),
            ["b"] = Ammo("b", 60, 20, 200),
            ["c"] = Ammo("c", 40, 40, 50),
            ["d"] = Ammo("d", 30, 10, 10),
        };
        var compare = new IntelCompareViewModel((id, _) => Task.FromResult(facts[id]));

        compare.SetCurrent("a", "A");
        compare.ToggleCurrentCommand.Execute(null);
        Assert.True(compare.CurrentIsInTray);
        Assert.False(compare.CanOpen);

        Assert.True(compare.Add("b", "B"));
        Assert.True(compare.Add("c", "C"));
        Assert.False(compare.Add("d", "D"));
        compare.SetCurrent("d", "D");
        Assert.False(compare.CanToggleCurrent);

        await compare.OpenAsync();
        Assert.True(compare.ShowsTable);
        Assert.Equal(3, compare.ColumnCount);
        Assert.Equal("Ammo side by side", compare.Heading);

        // Removing down to one closes the table rather than comparing a single item.
        compare.Remove("b");
        compare.Remove("c");
        Assert.False(compare.IsOpen);
    }

    [Fact]
    public async Task OpeningAnotherItemClosesTheTableButKeepsTheTray()
    {
        var compare = new IntelCompareViewModel((id, _) => Task.FromResult(Ammo(id, 50, 30, 100)));
        compare.SetCurrent("a", "A");
        compare.Add("a", "A");
        compare.Add("b", "B");
        await compare.OpenAsync();

        compare.SetCurrent("a", "A");
        Assert.True(compare.IsOpen);

        compare.SetCurrent("z", "Z");
        Assert.False(compare.IsOpen);
        Assert.Equal(2, compare.Entries.Count);
    }

    private static bool[] Best(ItemComparisonTable table, string label) =>
        [.. table.Rows.Single(row => row.Label == label).Cells.Select(cell => cell.IsBest)];

    private static string[] Texts(ItemComparisonTable table, string label) =>
        [.. table.Rows.Single(row => row.Label == label).Cells.Select(cell => cell.Text)];

    private static readonly DataProvenance Provenance = new("test", DateTimeOffset.UnixEpoch);

    private static V2IntelPriceFacts Prices(long? flea) =>
        new(flea, null, null, null, [], DateTimeOffset.UnixEpoch);

    private static ItemComparisonFacts Ammo(
        string id,
        int damage,
        int penetration,
        long? flea,
        Dictionary<int, ArmorEffectiveness>? ratings = null) =>
        new(
            new V2ItemIntelResult(V2IntelKind.Ammo, id, id, id, null, ItemCategory.Ammunition, 1, 1, true,
                Ammo: new V2IntelAmmoFacts(damage, penetration, "B", ratings ?? new() { [2] = ArmorEffectiveness.Good }, string.Empty,
                    Caliber: "5.56x45mm NATO"),
                Prices: Prices(flea)),
            Gear: null,
            WeightKg: 0.01);

    private static ItemComparisonFacts Armor(string id, int armorClass, double durability, double weight, params string[] zones) =>
        new(
            new V2ItemIntelResult(V2IntelKind.Item, id, id, id, null, ItemCategory.Armor, 3, 3, true, Prices: Prices(10_000)),
            new GearFacts(id, ItemCategory.Armor, armorClass, durability, "Aramid", "Light", zones, null, null,
                -0.01, 0, -0.01, null, null, null, [], null, [], Provenance),
            weight);

    private static ItemComparisonFacts Key(string id, int uses, long value, long? cost, string lockName = "Room 314") =>
        new(
            new V2ItemIntelResult(V2IntelKind.Key, id, id, id, null, ItemCategory.Key, 1, 1, true,
                Value: new V2IntelValueFacts(value, "Flea", 0, 0, 0),
                Key: new V2IntelKeyFacts("customs", [lockName], uses, cost, "Customs")),
            Gear: null,
            WeightKg: 0.01);
}
