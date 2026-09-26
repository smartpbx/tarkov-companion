using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.Personal;

public sealed class PersonalExitsAndPatternsTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

    private static PersonalRaid Raid(int hour, string map = "customs", string? outcome = "Survived", string? exit = null, string? side = "pmc") =>
        new(Guid.NewGuid(), map, side, Day.AddHours(hour), outcome, exit);

    [Fact]
    public void UsesCountThisMapAndSideOnly()
    {
        var raids = new[]
        {
            Raid(1, exit: "Crossroads"), Raid(2, exit: "crossroads"), Raid(3, exit: "ZB-1011"),
            Raid(4, exit: "Crossroads", side: "scav"), Raid(5, map: "woods", exit: "Crossroads"), Raid(6, exit: null),
        };

        var uses = PersonalExits.Uses(raids, "customs", "pmc");

        Assert.Equal(2, uses["Crossroads"]);
        Assert.Equal(1, uses["ZB-1011"]);
        Assert.Equal(("Crossroads", 2), PersonalExits.Favourite(uses));
    }

    [Fact]
    public void AnExitUsedOftenWinsATieWithANearerOne()
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Crossroads"] = 4 };
        ExitChoice[] exits = [new("ZB-1011", 400, true), new("Crossroads", 450, true)];

        var chosen = PersonalExits.Choose(exits, uses);

        Assert.Equal("Crossroads", chosen!.Exit.Name);
        Assert.Equal(4, chosen.Uses);
        Assert.True(chosen.UsesDecided);
    }

    [Fact]
    public void HabitNeverBeatsAnExitMoreThanAThirdNearer()
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Crossroads"] = 50 };
        ExitChoice[] exits = [new("ZB-1011", 300, true), new("Crossroads", 450, true)];

        var chosen = PersonalExits.Choose(exits, uses);

        Assert.Equal("ZB-1011", chosen!.Exit.Name);
        Assert.False(chosen.UsesDecided);
    }

    [Fact]
    public void AnOfferedExitStillBeatsAUsedOneNotSeenOffered()
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Crossroads"] = 9 };
        ExitChoice[] exits = [new("ZB-1011", 500, true), new("Crossroads", 100, false)];

        Assert.Equal("ZB-1011", PersonalExits.Choose(exits, uses)!.Exit.Name);
    }

    [Fact]
    public void WithNoHistoryTheNearestIsNamed()
    {
        ExitChoice[] exits = [new("ZB-1011", 300, false), new("Crossroads", 200, false), new("Nowhere", double.NaN, false)];

        var chosen = PersonalExits.Choose(exits, new Dictionary<string, int>());

        Assert.Equal("Crossroads", chosen!.Exit.Name);
        Assert.Equal(0, chosen.Uses);
        Assert.Null(PersonalExits.Choose([], new Dictionary<string, int>()));
    }

    [Fact]
    public void PatternCountsTheLastFiveRaidsOnThisMapAndNamesASharedDeathPlace()
    {
        var dorms = new WorldPosition(10, 0, 10);
        var elsewhere = new WorldPosition(900, 0, 900);
        var raids = new[]
        {
            Raid(1, outcome: "Died"), // oldest: outside the last five
            Raid(2, outcome: "Died"), Raid(3, outcome: "Survived"), Raid(4, outcome: "Killed in action"),
            Raid(5, outcome: "Died"), Raid(6, outcome: "Run-through"), Raid(7, map: "woods", outcome: "Died"),
        };
        var last = new Dictionary<Guid, WorldPosition>
        {
            [raids[0].RaidId] = dorms, [raids[1].RaidId] = dorms, [raids[3].RaidId] = dorms, [raids[4].RaidId] = elsewhere,
        };

        var pattern = PersonalPatterns.Describe(
            raids, "customs", id => last.TryGetValue(id, out var at) ? at : null, at => at == dorms ? "Dorms" : null);

        Assert.NotNull(pattern);
        Assert.Equal(5, pattern!.Raids);
        Assert.Equal(3, pattern.Deaths);
        Assert.Equal("Dorms", pattern.DeathPlace);
        Assert.Equal(2, pattern.DeathsAtPlace);
        Assert.Equal(2, pattern.Extracted);
    }

    [Fact]
    public void OneDeathSomewhereIsNotAPlace()
    {
        var raids = new[] { Raid(1, outcome: "Died"), Raid(2) };

        var pattern = PersonalPatterns.Describe(raids, "customs", _ => new WorldPosition(0, 0, 0), _ => "Dorms");

        Assert.Equal(1, pattern!.Deaths);
        Assert.Null(pattern.DeathPlace);
        Assert.Null(PersonalPatterns.Describe(raids, "woods", _ => null, _ => null));
    }
}
