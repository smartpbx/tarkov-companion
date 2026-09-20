using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class AllergyWarningPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyAnAllergyRecordedInARunningEventCounts()
    {
        var profile = TestProfile.Create() with
        {
            EventItemStates = new Dictionary<string, EventItemState>(StringComparer.Ordinal)
            {
                ["halloween:tarcola"] = EventItemState.Allergic,
                ["halloween:salewa"] = EventItemState.Safe,
                ["last-year:crackers"] = EventItemState.Allergic,
                ["switched-off:juice"] = EventItemState.Allergic,
            },
        };
        EventDefinition[] events =
        [
            Event("halloween", "Halloween 2026", true, Now.AddDays(-1), Now.AddDays(7), "tarcola", "salewa", "water"),
            Event("last-year", "Halloween 2025", true, Now.AddDays(-400), Now.AddDays(-380), "crackers"),
            Event("switched-off", "Archived", false, null, null, "juice"),
        ];

        var allergic = AllergyWarningPlanner.AllergicItems(profile, events, Now);

        Assert.Equal("Halloween 2026", Assert.Single(allergic).Value);
        Assert.True(allergic.ContainsKey("tarcola"));
    }

    [Fact]
    public void TheWarningIsForThingsThatAreConsumed()
    {
        var allergic = new Dictionary<string, string>(StringComparer.Ordinal) { ["tarcola"] = "Halloween 2026", ["bolts"] = "Halloween 2026" };

        Assert.Equal("Allergic · Halloween 2026", AllergyWarningPlanner.Warning("tarcola", ItemCategory.Provision, allergic));
        Assert.Equal("Allergic · Halloween 2026", AllergyWarningPlanner.Warning("tarcola", ItemCategory.Medicine, allergic));
        Assert.Null(AllergyWarningPlanner.Warning("bolts", ItemCategory.Barter, allergic));
        Assert.Null(AllergyWarningPlanner.Warning("water", ItemCategory.Provision, allergic));
    }

    [Fact]
    public void UndoRemembersOneRealChangeAndHandsItBackOnce()
    {
        var undo = new EventStateUndo();
        undo.Record(new("halloween", "tarcola", "TarCola", EventItemState.Safe, EventItemState.Safe));
        Assert.False(undo.CanUndo);

        undo.Record(new("halloween", "tarcola", "TarCola", EventItemState.Untested, EventItemState.Allergic));
        undo.Record(new("halloween", "salewa", "Salewa", EventItemState.Safe, EventItemState.Allergic));
        undo.Forget("halloween", "tarcola");
        Assert.True(undo.CanUndo);

        var change = undo.Take();
        Assert.Equal(("salewa", EventItemState.Safe), (change?.ItemId, change?.Previous));
        Assert.Null(undo.Take());

        undo.Record(new("halloween", "salewa", "Salewa", EventItemState.Safe, EventItemState.Allergic));
        undo.Forget("halloween");
        Assert.False(undo.CanUndo);
    }

    private static EventDefinition Event(string id, string name, bool active, DateTimeOffset? start, DateTimeOffset? end, params string[] items) =>
        new(id, name, start, end, active, items.ToHashSet(StringComparer.Ordinal), "{}", new DataProvenance("test", Now, Now, null));
}
