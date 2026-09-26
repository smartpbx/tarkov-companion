using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class SessionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoHoursIsFourRaidsAndTheFirstIsTheNextRaidSuggestion()
    {
        QuestSummaryReadModel[] tasks =
        [
            Task("swag", "Golden Swag", Objective("s1", "customs"), Objective("s2", "customs")),
            Task("check", "Checking", Objective("c1", "customs")),
            Task("shoot", "Shootout Picnic", Objective("p1", "woods"), Objective("p2", "woods"), Objective("p3", "woods")),
            Task("fact", "Supply Plans", Objective("f1", "factory")),
            Task("inter", "Interchange Job", Objective("i1", "interchange")),
            Task("reserve", "Reserve Job", Objective("r1", "reserve")),
        ];

        var plan = SessionPlanner.Plan(tasks, TimeSpan.FromHours(2), SessionProgress.Fresh, Name);

        Assert.Equal(["customs", "woods", "factory", "interchange"], plan.Raids.Select(raid => raid.MapKey));
        Assert.Equal(["Golden Swag", "Checking"], plan.Raids[0].QuestNames);
        Assert.False(plan.PerRaidMeasured);
        Assert.Equal(SessionPlanner.DefaultPerRaid, plan.PerRaid);
    }

    [Fact]
    public void AMapWithMoreObjectivesThanOneRaidComesBackLater()
    {
        var customs = Enumerable.Range(1, 6).Select(index => Objective($"c{index}", "customs")).ToArray();
        QuestSummaryReadModel[] tasks =
        [
            Task("big", "Big Customs Quest", customs),
            Task("other", "Other Customs Quest", Objective("o1", "customs")),
            Task("woods", "Woods Quest", Objective("w1", "woods")),
        ];

        var plan = SessionPlanner.Plan(tasks, TimeSpan.FromHours(2), SessionProgress.Fresh, Name);

        Assert.Equal(["customs", "customs", "woods"], plan.Raids.Select(raid => raid.MapKey));
        Assert.Equal(SessionPlanner.ObjectivesPerRaid, plan.Raids[0].Objectives);
        Assert.Equal(["Big Customs Quest"], plan.Raids[0].QuestNames);
        Assert.Equal(["Big Customs Quest", "Other Customs Quest"], plan.Raids[1].QuestNames);
    }

    [Fact]
    public void SpareSlotsBecomeLootRunsOnlyWhenTheHideoutNeedsSomething()
    {
        QuestSummaryReadModel[] tasks = [Task("one", "One", Objective("a", "woods"))];

        var none = SessionPlanner.Plan(tasks, TimeSpan.FromHours(2), SessionProgress.Fresh, Name);
        var hideout = SessionPlanner.Plan(tasks, TimeSpan.FromHours(2), SessionProgress.Fresh, Name, hideoutItemsNeeded: 5);

        Assert.Single(none.Raids);
        Assert.Equal(4, hideout.Raids.Count);
        Assert.All(hideout.Raids.Skip(1), raid => Assert.True(raid.IsLootRun));
    }

    [Fact]
    public void AFinishedRaidIsOneFewerSlotAndDoneObjectivesLeaveThePlan()
    {
        QuestSummaryReadModel[] before =
        [
            Task("swag", "Golden Swag", Objective("s1", "customs")),
            Task("shoot", "Shootout Picnic", Objective("p1", "woods")),
            Task("fact", "Supply Plans", Objective("f1", "factory")),
        ];
        QuestSummaryReadModel[] after =
        [
            Task("swag", "Golden Swag", Objective("s1", "customs", RecordedObjectiveState.Completed)),
            before[1],
            before[2],
        ];
        RaidHistoryEntry[] history = [Raid(Now.AddMinutes(-35), Now.AddMinutes(-10))];

        var fresh = SessionPlanner.Plan(before, TimeSpan.FromHours(2), SessionProgress.Fresh, Name);
        var progress = SessionPlanner.Measure(history, Now);
        var replanned = SessionPlanner.Plan(after, TimeSpan.FromHours(2), progress, Name);

        Assert.Equal("customs", fresh.Raids[0].MapKey);
        Assert.Equal(1, progress.RaidsPlayed);
        Assert.Equal(TimeSpan.FromMinutes(35), progress.Elapsed);
        Assert.Equal(1, replanned.RaidsPlayed);
        Assert.Equal(TimeSpan.FromMinutes(85), replanned.Remaining);
        Assert.DoesNotContain(replanned.Raids, raid => raid.MapKey == "customs");
        Assert.Equal(2, replanned.Raids.Count);
    }

    [Fact]
    public void TheSessionIsTheRunOfRaidsWithoutALongBreak()
    {
        RaidHistoryEntry[] history =
        [
            Raid(Now.AddHours(-26), Now.AddHours(-25.6)),
            Raid(Now.AddMinutes(-100), Now.AddMinutes(-75)),
            Raid(Now.AddMinutes(-70), Now.AddMinutes(-45)),
            Raid(Now.AddMinutes(-40), Now.AddMinutes(-20)),
        ];

        var progress = SessionPlanner.Measure(history, Now);

        Assert.Equal(3, progress.RaidsPlayed);
        Assert.Equal(Now.AddMinutes(-100), progress.StartedUtc);
        Assert.Equal(TimeSpan.FromMinutes(25), progress.MedianRaid);
    }

    [Fact]
    public void AnEveningAfterALongBreakStartsFreshButKeepsTheMeasuredRaidLength()
    {
        RaidHistoryEntry[] history =
        [
            Raid(Now.AddHours(-5), Now.AddHours(-5).AddMinutes(20)),
            Raid(Now.AddHours(-4), Now.AddHours(-4).AddMinutes(20)),
            Raid(Now.AddHours(-3), Now.AddHours(-3).AddMinutes(20)),
        ];

        var progress = SessionPlanner.Measure(history, Now);
        var plan = SessionPlanner.Plan([Task("a", "A", Objective("a1", "woods"))], TimeSpan.FromHours(2), progress, Name);

        Assert.Equal(0, progress.RaidsPlayed);
        Assert.Null(progress.StartedUtc);
        Assert.True(plan.PerRaidMeasured);
        Assert.Equal(TimeSpan.FromMinutes(27), plan.PerRaid);
    }

    [Fact]
    public void ASessionThatHasRunOutPlansNothing()
    {
        var progress = new SessionProgress(Now.AddHours(-3), 6, TimeSpan.FromHours(3), null);

        var plan = SessionPlanner.Plan([Task("a", "A", Objective("a1", "woods"))], TimeSpan.FromHours(2), progress, Name);

        Assert.Empty(plan.Raids);
        Assert.Equal(TimeSpan.Zero, plan.Remaining);
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("", 120)]
    [InlineData("junk", 120)]
    [InlineData("5", 120)]
    [InlineData("90", 90)]
    [InlineData("240", 240)]
    public void TheStoredSessionLengthFallsBackToTwoHours(string? stored, int expected) =>
        Assert.Equal(expected, SessionPlanner.ParseSessionMinutes(stored));

    [Fact]
    public void AReadyQuestIsAReminderToVisitItsTrader()
    {
        QuestSummaryReadModel[] tasks =
        [
            Task("swag", "Golden Swag", Objective("s1", "customs", RecordedObjectiveState.Completed)) with
            {
                RecordedObjectivesSatisfied = RecordedObjectivesSatisfaction.Satisfied,
                TraderId = "skier",
                TraderName = "Skier",
            },
            Task("open", "Still Open", Objective("o1", "woods")),
        ];

        var reminder = Assert.Single(HandInReminders.Find(tasks, new Dictionary<string, int>()));

        Assert.Equal("Golden Swag", reminder.TaskName);
        Assert.Equal("Skier", reminder.TraderLabel);
        Assert.Equal(HandInReason.Ready, reminder.Reason);
    }

    [Fact]
    public void ItemsHeldForTheOnlyOpenHandOverAreAReminderButAnUnrecordedStashIsNot()
    {
        var task = Task(
            "flash",
            "Flash Drive Quest",
            Objective("find", "customs", RecordedObjectiveState.Completed),
            Give("give", "flash-drive", 2)) with { TraderId = "therapist" };

        var held = HandInReminders.Find([task], new Dictionary<string, int> { ["flash-drive"] = 2 });
        var tooFew = HandInReminders.Find([task], new Dictionary<string, int> { ["flash-drive"] = 1 });
        var unknown = HandInReminders.Find([task], new Dictionary<string, int>());
        var otherOpen = HandInReminders.Find(
            [task with { Objectives = [.. task.Objectives, Objective("visit", "woods")] }],
            new Dictionary<string, int> { ["flash-drive"] = 2 });

        Assert.Equal(HandInReason.ItemsHeld, Assert.Single(held).Reason);
        Assert.Equal("therapist", held[0].TraderLabel);
        Assert.Empty(tooFew);
        Assert.Empty(unknown);
        Assert.Empty(otherOpen);
    }

    private static string Name(string mapId) => char.ToUpperInvariant(mapId[0]) + mapId[1..];

    private static RaidHistoryEntry Raid(DateTimeOffset started, DateTimeOffset ended) =>
        new(Guid.NewGuid(), Guid.Empty, "customs", "pvp", started, ended, null, null);

    private static QuestSummaryReadModel Task(string id, string name, params QuestObjectiveReadModel[] objectives) =>
        new(
            id,
            name,
            TraderId: "prapor",
            PrimaryMapId: null,
            RecordedState: RecordedTaskState.Active,
            ProgressSource: "test",
            ProgressModifiedUtc: null,
            Eligibility: new(QuestEligibilityState.Available, []),
            RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.NotSatisfied,
            IsPinned: false,
            Restartable: null,
            HasFailureConditions: false,
            FailureConditionNotes: [],
            Prerequisites: [],
            Objectives: objectives);

    private static QuestObjectiveReadModel Objective(
        string id,
        string map,
        RecordedObjectiveState state = RecordedObjectiveState.InProgress) =>
        new(id, id, QuestObjectiveKind.Visit, false, false, state, null, null, null, "test", null, false, [map], []);

    private static QuestObjectiveReadModel Give(string id, string itemId, int count) =>
        new(
            id,
            id,
            QuestObjectiveKind.GiveItem,
            false,
            false,
            RecordedObjectiveState.InProgress,
            0,
            count,
            true,
            "test",
            null,
            false,
            [],
            [new(itemId, "items", 0, 0, count, true)]);
}
