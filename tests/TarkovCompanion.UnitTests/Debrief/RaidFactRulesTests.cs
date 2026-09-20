using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Debrief;

/// <summary>
/// Which of observed, inferred, estimated and manual stands behind each field of a raid, decided
/// from the ways the record can be written rather than from a flag somebody has to remember to set.
/// </summary>
public sealed class RaidFactRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_raid_the_log_saw_start_and_end_is_observed_and_its_mode_is_inferred()
    {
        var sources = RaidFactRules.Classify(Raid(), []);

        Assert.Equal(RaidFactKind.Observed, sources.Map);
        Assert.Equal(RaidFactKind.Inferred, sources.Mode);
        Assert.Equal(RaidFactKind.Observed, sources.Started);
        Assert.Equal(RaidFactKind.Observed, sources.Ended);
        Assert.Equal(RaidFactKind.Observed, sources.Duration);
    }

    [Fact]
    public void A_field_with_no_value_has_no_source_rather_than_a_default()
    {
        var sources = RaidFactRules.Classify(Raid(mapId: null, ended: false), []);

        Assert.Equal(RaidFactKind.Unknown, sources.Map);
        Assert.Equal(RaidFactKind.Unknown, sources.Ended);
        Assert.Equal(RaidFactKind.Unknown, sources.Outcome);
        Assert.Equal(RaidFactKind.Unknown, sources.Notes);
        Assert.Equal(RaidFactKind.Unknown, sources.Duration);
    }

    [Fact]
    public void A_raid_the_companion_closed_on_restart_has_an_inferred_end_and_an_estimated_duration()
    {
        var sources = RaidFactRules.Classify(
            Raid(outcome: RaidClosure.ClosedOnRestartOutcome, notes: RaidClosure.ClosedOnRestartNotes),
            []);

        Assert.Equal(RaidFactKind.Inferred, sources.Ended);
        Assert.Equal(RaidFactKind.Inferred, sources.Outcome);
        Assert.Equal(RaidFactKind.Inferred, sources.Notes);
        Assert.Equal(RaidFactKind.Estimated, sources.Duration);
    }

    [Fact]
    public void Text_the_player_typed_is_manual_because_nothing_else_writes_an_outcome()
    {
        var sources = RaidFactRules.Classify(Raid(outcome: "Survived", notes: "Found a GPU"), []);

        Assert.Equal(RaidFactKind.Manual, sources.Outcome);
        Assert.Equal(RaidFactKind.Manual, sources.Notes);
        Assert.Equal(RaidFactKind.Observed, sources.Ended);
    }

    [Fact]
    public void A_player_who_types_the_companions_own_words_is_still_manual_because_the_correction_says_so()
    {
        var before = Raid();
        var correction = RaidCorrection.Between(before, RaidClosure.ClosedOnRestartOutcome, null, Start.AddHours(1))!;

        var sources = RaidFactRules.Classify(before with { Outcome = RaidClosure.ClosedOnRestartOutcome }, [correction]);

        Assert.Equal(RaidFactKind.Manual, sources.Outcome);
    }

    [Fact]
    public void Overwriting_the_companions_text_leaves_the_end_inferred_because_the_correction_remembers_it()
    {
        var closed = Raid(outcome: RaidClosure.ClosedOnRestartOutcome, notes: RaidClosure.ClosedOnRestartNotes);
        var correction = RaidCorrection.Between(closed, "Survived", "Extracted at Gate 3", Start.AddHours(2))!;

        var sources = RaidFactRules.Classify(closed with { Outcome = "Survived", Notes = "Extracted at Gate 3" }, [correction]);

        Assert.Equal(RaidFactKind.Manual, sources.Outcome);
        Assert.Equal(RaidFactKind.Manual, sources.Notes);
        Assert.Equal(RaidFactKind.Inferred, sources.Ended);
    }

    [Fact]
    public void Saving_a_form_with_nothing_changed_records_no_correction()
    {
        Assert.Null(RaidCorrection.Between(Raid(outcome: "Survived", notes: "n"), "Survived", "n", Start));
    }

    [Fact]
    public void A_correction_round_trips_and_an_unreadable_one_is_skipped()
    {
        var correction = RaidCorrection.Between(Raid(), "Survived", null, Start)!;

        var read = RaidCorrection.ParseAll([correction.ToPayload(), "not json", "{"]);

        var only = Assert.Single(read);
        Assert.Equal("Survived", only.Outcome?.To);
        Assert.Null(only.Notes);
    }

    [Fact]
    public void Every_kind_has_a_label_and_an_export_word_except_unknown()
    {
        Assert.Equal(["Observed", "Inferred", "Estimate", "Manual"], new[] { RaidFactKind.Observed, RaidFactKind.Inferred, RaidFactKind.Estimated, RaidFactKind.Manual }.Select(kind => kind.Label()));
        Assert.Equal(["observed", "inferred", "estimated", "manual"], new[] { RaidFactKind.Observed, RaidFactKind.Inferred, RaidFactKind.Estimated, RaidFactKind.Manual }.Select(kind => kind.Slug()));
        Assert.Equal(string.Empty, RaidFactKind.Unknown.Label());
        Assert.Null(RaidFactKind.Unknown.Slug());
    }

    private static RaidHistoryEntry Raid(
        string? mapId = "customs",
        bool ended = true,
        string? outcome = null,
        string? notes = null) => new(
        Guid.Parse("40000000-0000-0000-0000-000000000001"),
        Guid.Parse("50000000-0000-0000-0000-000000000001"),
        mapId,
        "Regular",
        Start,
        ended ? Start.AddMinutes(24) : null,
        outcome,
        notes);
}
