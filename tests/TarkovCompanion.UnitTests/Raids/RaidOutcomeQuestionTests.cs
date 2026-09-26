using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>[#712 0-8] The after-raid question: when it is asked, what an answer stores, where a raid ended.</summary>
public sealed class RaidOutcomeQuestionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 19, 18, 0, TimeSpan.Zero);
    private static readonly Guid RaidId = Guid.Parse("70000000-0000-0000-0000-000000000001");

    [Fact]
    public void The_end_of_a_raid_seen_in_progress_is_reported_once()
    {
        var detector = new RaidEndDetector();
        var squad = new SquadSnapshot([Member("Geo"), Member("Riley")], Start, null, Start);

        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.LoadingRaid), SquadSnapshot.Empty));
        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.InRaid), squad));
        var ended = detector.Observe(Snapshot(RaidLifecycleState.PostRaid, Start.AddMinutes(34)), SquadSnapshot.Empty);

        Assert.NotNull(ended);
        Assert.Equal(RaidId, ended.RaidId);
        Assert.Equal("customs", ended.MapId);
        Assert.Equal(Start.AddMinutes(34), ended.EndedUtc);
        Assert.Equal(["Geo", "Riley"], ended.Squad);
        // The menu after the post-raid screens, and the same raid's end repeated, ask nothing more.
        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.Menu), SquadSnapshot.Empty));
        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.InRaid), SquadSnapshot.Empty));
        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.PostRaid), SquadSnapshot.Empty));
    }

    [Fact]
    public void A_raid_never_seen_in_progress_or_replaced_by_another_is_not_reported()
    {
        var detector = new RaidEndDetector();
        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.PostRaid), SquadSnapshot.Empty));

        Assert.Null(detector.Observe(Snapshot(RaidLifecycleState.InRaid), SquadSnapshot.Empty));
        var next = Snapshot(RaidLifecycleState.Menu) with { RaidId = Guid.NewGuid() };
        Assert.Null(detector.Observe(next, SquadSnapshot.Empty));
    }

    [Fact]
    public void The_question_is_asked_only_when_there_is_no_outcome_and_no_earlier_answer()
    {
        Assert.True(RaidOutcomeQuestion.ShouldAsk(null, []));
        Assert.True(RaidOutcomeQuestion.ShouldAsk("  ", []));
        Assert.False(RaidOutcomeQuestion.ShouldAsk("Died", []));
        Assert.False(RaidOutcomeQuestion.ShouldAsk(null, [RaidOutcomeAnswer.Dismissal(Start)]));
        Assert.False(RaidOutcomeQuestion.ShouldAsk(null, [RaidOutcomeAnswer.Answered(RaidOutcomeBucket.Survived, Start)]));
    }

    [Theory]
    [InlineData(RaidOutcomeBucket.Survived)]
    [InlineData(RaidOutcomeBucket.Died)]
    [InlineData(RaidOutcomeBucket.RunThrough)]
    [InlineData(RaidOutcomeBucket.Mia)]
    public void Every_answer_is_stored_as_text_the_survival_counts_read_back_as_the_same_answer(RaidOutcomeBucket bucket) =>
        Assert.Equal(bucket, RaidCoverage.Classify(RaidOutcomeQuestion.StoredText(bucket)));

    [Fact]
    public void An_answer_round_trips_with_the_player_as_its_source_and_a_bad_row_costs_nothing_else()
    {
        var stored = RaidOutcomeAnswer.Answered(RaidOutcomeBucket.Died, Start).ToPayload();

        var answer = Assert.Single(RaidOutcomeAnswer.ParseAll(["not json", "{}", stored]));

        Assert.Equal("Died", answer.Outcome);
        Assert.False(answer.Dismissed);
        Assert.Equal(RaidOutcomeAnswer.PlayerSource, answer.Source);
        Assert.Equal(Start, answer.RecordedUtc);
    }

    [Fact]
    public void The_recorded_time_is_the_newest_correction_of_the_outcome_and_none_without_an_outcome()
    {
        var raid = new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Regular", Start, Start.AddMinutes(30), "Survived", null);
        IReadOnlyList<RaidCorrection> corrections =
        [
            new(Start.AddMinutes(31), new RaidFieldChange(null, "Died"), null),
            new(Start.AddMinutes(40), null, new RaidFieldChange(null, "note")),
            new(Start.AddMinutes(35), new RaidFieldChange("Died", "Survived"), null),
        ];

        Assert.Equal(Start.AddMinutes(35), RaidOutcomeQuestion.RecordedUtc(raid, corrections));
        Assert.Null(RaidOutcomeQuestion.RecordedUtc(raid with { Outcome = null }, corrections));
    }

    [Fact]
    public void Ended_near_names_the_closest_offered_extract_within_reach_and_nothing_further_away()
    {
        IReadOnlyList<MapExtract> extracts =
        [
            Extract("ZB-1011", 621.5, -128.6),
            Extract("Crossroads", 612, -122),
            Extract("Trailer Park", 200, 100),
        ];
        var last = Position(616, -124);

        Assert.Equal("Crossroads", RaidOutcomeQuestion.NearestExtract(last, extracts, null)?.Name);
        // The photographed list did not offer Crossroads, so it is not where this raid ended.
        Assert.Equal("ZB-1011", RaidOutcomeQuestion.NearestExtract(last, extracts, ["ZB-1011", "Trailer Park"])?.Name);
        Assert.Null(RaidOutcomeQuestion.NearestExtract(Position(400, 0), extracts, null));
        Assert.Null(RaidOutcomeQuestion.NearestExtract(null, extracts, null));
    }

    private static RaidSnapshot Snapshot(RaidLifecycleState state, DateTimeOffset? updated = null) =>
        new(RaidId, state, "customs", Start, updated ?? Start, Confidence.Unknown, null, [], false) { Side = "PMC" };

    private static GroupMember Member(string nickname) => new(null, null, nickname, "Usec", 20, null, null, null, []);

    private static MapExtract Extract(string name, double x, double z) =>
        new(name, "customs", name, new MapPoint(x, z), null, new DataProvenance("test", Start));

    private static ScreenshotPosition Position(double x, double z) =>
        new(Start, new WorldPosition(x, 1, z), new QuaternionOrientation(0, 0, 0, 1), 0, null, null, "t.png");
}
