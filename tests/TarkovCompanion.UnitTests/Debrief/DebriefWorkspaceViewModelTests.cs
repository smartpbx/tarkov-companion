using System.Text.Json;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.PlayerTime;

namespace TarkovCompanion.UnitTests.Debrief;

public sealed class DebriefWorkspaceViewModelTests
{
    private static readonly Guid RaidId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Started = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loading_lists_raids_and_selecting_one_loads_its_detail()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(
            RaidId,
            Guid.NewGuid(),
            "customs",
            "Pmc",
            Started,
            Started.AddMinutes(24),
            "Survived",
            "Found a GPU"));
        service.SeedPositions(RaidId, [Position(Started), Position(Started.AddMinutes(1))]);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal("customs", row.MapLabel);

        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);

        Assert.True(viewModel.HasSelection);
        Assert.Equal("customs", viewModel.SelectedMapLabel);
        Assert.Equal("24m 00s", viewModel.SelectedDurationLabel);
        Assert.Equal("2 screenshots recorded.", viewModel.SelectedPathLabel);
    }

    /// <summary>
    /// The raid began at 18:00 UTC. A player at UTC-4 was in the lobby at 14:00, and the page must
    /// say so: the list, the selected raid and its quest and sale rows all read that clock. The zone
    /// is pinned to one that is never UTC, so a UTC-only CI box cannot pass this by coincidence.
    /// </summary>
    [Fact]
    public async Task Raid_times_are_shown_on_the_players_clock_not_in_utc()
    {
        using var pin = PlayerClock.Pin();
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        service.SeedEvent(RaidId, "quest", JsonSerializer.Serialize(new QuestStatusObservation(
            "EVENT_1", "TASK_1", RecordedTaskState.Completed, Started.AddMinutes(3))));
        service.SeedEvent(RaidId, "sale", JsonSerializer.Serialize(new FleaSaleObservation(
            "OFFER_1", "ITEM_1", 1, Started.AddMinutes(5))));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal("09/15/2026 14:00", row.StartedLabel);
        Assert.Equal("09/15/2026 14:24", row.EndedLabel);
        Assert.Equal("09/15/2026 14:00", viewModel.SelectedStartedLabel);
        Assert.Equal("09/15/2026 14:24", viewModel.SelectedEndedLabel);
        Assert.Equal("14:03", Assert.Single(viewModel.SelectedQuestEvents).TimeLabel);
        Assert.Equal("14:05", Assert.Single(viewModel.SelectedSales).TimeLabel);
    }

    /// <summary>
    /// The export is named for the moment it was made, in a folder the player opens: the name
    /// carries their own clock rather than the UTC one the app stores.
    /// </summary>
    [Fact]
    public async Task An_export_is_named_by_the_players_clock()
    {
        using var pin = PlayerClock.Pin();
        var paths = TestPaths();
        var viewModel = new DebriefWorkspaceViewModel(new FakeRaidHistoryService(), paths, new FixedClock(Started));

        await ((AsyncDelegateCommand)viewModel.ExportCsvCommand).ExecuteAsync();

        Assert.StartsWith("Exported to ", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("20260915-140000-raid-history.csv", Path.GetFileName(viewModel.Status["Exported to ".Length..]));
    }

    /// <summary>
    /// Two offers for the same item read as one row: "2 sold", not two rows nobody asked to
    /// tell apart.
    /// </summary>
    [Fact]
    public async Task SelectingARaidCountsFleaSalesPerItem()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.SeedEvent(RaidId, "sale", JsonSerializer.Serialize(new FleaSaleObservation(
            "OFFER_1", "ITEM_1", 1, Started.AddMinutes(2))));
        service.SeedEvent(RaidId, "sale", JsonSerializer.Serialize(new FleaSaleObservation(
            "OFFER_2", "ITEM_1", 2, Started.AddMinutes(4))));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasSelectedSales);
        var row = Assert.Single(viewModel.SelectedSales);
        Assert.Equal("ITEM_1", row.ItemLabel);
        Assert.Equal("3 sold", row.CountLabel);
    }

    /// <summary>The game's own words about a quest, kept apart from what the player typed by hand.</summary>
    [Fact]
    public async Task SelectingARaidListsTheQuestEventsTheGameAnnounced()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.SeedEvent(RaidId, "quest", JsonSerializer.Serialize(new QuestStatusObservation(
            "EVENT_1", "TASK_1", RecordedTaskState.Completed, Started.AddMinutes(3))));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasSelectedQuestEvents);
        var row = Assert.Single(viewModel.SelectedQuestEvents);
        Assert.Equal("TASK_1", row.QuestLabel);
        Assert.Equal("Handed in", row.StateLabel);
    }

    [Fact]
    public async Task Quest_event_names_use_the_configured_catalog_language()
    {
        var history = new FakeRaidHistoryService();
        history.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        history.SeedEvent(RaidId, "quest", JsonSerializer.Serialize(new QuestStatusObservation(
            "EVENT_1", "TASK_1", RecordedTaskState.Completed, Started.AddMinutes(3))));
        var catalog = new RecordingQuestCatalog();
        var viewModel = new DebriefWorkspaceViewModel(
            history,
            TestPaths(),
            questCatalog: catalog,
            profileService: new StubProfileService(),
            questOptions: new QuestTrackingOptions("de"));

        await viewModel.LoadAsync();

        Assert.Equal("de", catalog.Language);
    }

    /// <summary>docs/research/EFT_LOG_FACTS.md names the `real` figure as present and unused.</summary>
    [Fact]
    public async Task SelectingARaidShowsTheQueueLoadTimeWhenOneWasSeen()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.SeedEvent(RaidId, "state", """{"Summary":"in a raid","LoadSeconds":25.02}""");
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.Equal("25.0s queue/load", viewModel.SelectedLoadTimeLabel);
    }

    /// <summary>
    /// Raids and durations, grouped by map. No survival column: the game records no outcome, so
    /// this must not compute one from a field the player fills in by hand.
    /// </summary>
    [Fact]
    public async Task LoadingBuildsPerMapStatsFromRaidHistory()
    {
        var service = new FakeRaidHistoryService();
        var secondRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(secondRaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.SeedEvent(RaidId, "state", """{"LoadSeconds":20.0}""");
        service.SeedEvent(secondRaidId, "state", """{"LoadSeconds":30.0}""");
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var stat = Assert.Single(viewModel.MapStats);
        Assert.Equal("customs", stat.MapLabel);
        Assert.Equal("2 raids", stat.RaidsLabel);
        Assert.Equal("avg 25.0s (2 raids measured)", stat.LoadLabel);
    }

    [Fact]
    public async Task Saving_a_correction_writes_through_to_the_service()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        viewModel.CorrectedOutcome = "Killed by scav";
        viewModel.CorrectedNotes = "Died at Dorms";

        await ((AsyncDelegateCommand)viewModel.SaveCorrectionCommand).ExecuteAsync();

        Assert.NotNull(service.LastCorrection);
        Assert.Equal("Killed by scav", service.LastCorrection.Value.Outcome);
        Assert.Equal("Died at Dorms", service.LastCorrection.Value.Notes);
    }

    [Fact]
    public async Task Loading_selects_the_newest_raid_and_names_its_map_from_the_catalog()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        viewModel.UseMapNames(id => id == "customs" ? "Customs" : null);

        await viewModel.LoadAsync();

        Assert.Equal("1 raid", viewModel.Status);
        Assert.Equal("Customs", Assert.Single(viewModel.Raids).MapLabel);
        Assert.True(viewModel.HasSelection);
        Assert.False(viewModel.HasNoSelection);
        Assert.Equal("Customs", viewModel.SelectedMapLabel);
    }

    [Fact]
    public async Task A_map_the_catalog_does_not_know_keeps_the_id_it_was_recorded_with()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "sandbox_high", "Pmc", Started, Started.AddMinutes(3), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        viewModel.UseMapNames(_ => null);

        await viewModel.LoadAsync();

        Assert.Equal("sandbox_high", Assert.Single(viewModel.Raids).MapLabel);
    }

    [Fact]
    public async Task A_raid_that_has_not_ended_reads_as_in_progress_rather_than_unknown()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, null, null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.Equal("In progress", Assert.Single(viewModel.Raids).DurationLabel);
        Assert.Equal("In progress", viewModel.SelectedDurationLabel);
    }

    [Fact]
    public async Task The_selected_raid_says_when_it_ended_and_how_far_it_went()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        service.SeedPositions(RaidId, [Position(Started, 0, 0), Position(Started.AddMinutes(2), 300, 400), Position(Started.AddMinutes(4), 300, 1400)]);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        // 500 m then 1000 m, which is V1's "at least" floor: straight lines between screenshots.
        Assert.Equal("At least 1.5 km", viewModel.SelectedDistanceLabel);
        Assert.True(viewModel.HasDistance);
        Assert.NotEqual("In progress", viewModel.SelectedEndedLabel);
    }

    [Fact]
    public async Task A_raid_still_in_progress_has_no_end_and_a_single_screenshot_has_no_distance()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, null, null, null));
        service.SeedPositions(RaidId, [Position(Started, 0, 0)]);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.Equal("In progress", viewModel.SelectedEndedLabel);
        Assert.False(viewModel.HasDistance);
        Assert.Equal(string.Empty, viewModel.SelectedDistanceLabel);
    }

    [Fact]
    public async Task Watching_a_raid_asks_the_shell_to_draw_its_trail_on_the_raids_own_map()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        var trail = new[] { Position(Started, 0, 0), Position(Started.AddMinutes(1), 10, 10) };
        service.SeedPositions(RaidId, trail);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        viewModel.UseMapNames(id => id == "customs" ? "Customs" : null);
        DebriefReplayRequest? request = null;
        viewModel.ReplayRequested += (_, asked) => request = asked;

        await viewModel.LoadAsync();
        Assert.True(viewModel.CanWatch);
        viewModel.WatchOnMapCommand.Execute(null);

        Assert.NotNull(request);
        // The map travels with the trail: a replay is only positions, and V1 drew them on whichever
        // map happened to be showing.
        Assert.Equal("customs", request.MapId);
        Assert.StartsWith("Customs · ", request.Title, StringComparison.Ordinal);
        Assert.Equal(trail, request.Positions);
    }

    [Fact]
    public async Task A_raid_with_no_screenshots_cannot_be_watched_and_says_so()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        var raised = false;
        viewModel.ReplayRequested += (_, _) => raised = true;

        await viewModel.LoadAsync();
        viewModel.WatchOnMapCommand.Execute(null);

        Assert.False(viewModel.CanWatch);
        Assert.False(raised);
        Assert.Equal("That raid has no screenshots to watch.", viewModel.Status);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AppDataPaths TestPaths() => AppDataPaths.Resolve(
        Path.Combine(Path.GetTempPath(), $"tarkov-companion-debrief-tests-{Guid.NewGuid():N}"));

    private static ScreenshotPosition Position(DateTimeOffset timestamp, double x = 0, double z = 0) => new(
        timestamp,
        new WorldPosition(x, 0, z),
        new QuaternionOrientation(0, 0, 0, 1),
        0,
        null,
        null,
        "fixture-position");

    [Fact]
    public async Task Each_detail_fact_says_where_it_came_from()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var kinds = viewModel.SelectedFacts.ToDictionary(fact => fact.Label, fact => fact.KindLabel);
        Assert.Equal("Observed", viewModel.SelectedMapKindLabel);
        Assert.Equal("Inferred", kinds["Mode"]);
        Assert.Equal("Observed", kinds["Started"]);
        Assert.Equal("Observed", kinds["Ended"]);
        Assert.Equal("Observed", kinds["Duration"]);
        // Nothing was typed and the game records no outcome, so it has no source rather than a wrong one.
        Assert.Equal(string.Empty, kinds["Outcome"]);
        Assert.Equal(string.Empty, kinds["Queue/load"]);
    }

    [Fact]
    public async Task A_raid_closed_on_restart_reads_as_inferred_and_its_duration_as_an_estimate()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(
            RaidId,
            Guid.NewGuid(),
            "customs",
            "Pmc",
            Started,
            Started.AddHours(3),
            RaidClosure.ClosedOnRestartOutcome,
            RaidClosure.ClosedOnRestartNotes));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var kinds = viewModel.SelectedFacts.ToDictionary(fact => fact.Label, fact => fact.KindLabel);
        Assert.Equal("Inferred", kinds["Ended"]);
        Assert.Equal("Estimate", kinds["Duration"]);
        Assert.Equal("Inferred", kinds["Outcome"]);
        Assert.Equal("Inferred", viewModel.Raids.Single().OutcomeKindLabel);
    }

    /// <summary>The bug this exists for: a corrected outcome used to overwrite the field with nothing to say who wrote it.</summary>
    [Fact]
    public async Task Saving_a_correction_marks_the_outcome_manual_in_the_detail_and_the_list()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        Assert.Equal(string.Empty, viewModel.Raids.Single().OutcomeKindLabel);

        viewModel.CorrectedOutcome = "Survived";
        viewModel.SaveCorrectionCommand.Execute(null);
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        await viewModel.LoadAsync();

        var kinds = viewModel.SelectedFacts.ToDictionary(fact => fact.Label, fact => fact.KindLabel);
        Assert.Equal("Manual", kinds["Outcome"]);
        Assert.Equal("Manual", viewModel.Raids.Single().OutcomeKindLabel);
    }

    [Fact]
    public async Task Typing_the_companions_own_closing_words_by_hand_is_still_manual()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        viewModel.CorrectedOutcome = RaidClosure.ClosedOnRestartOutcome;
        viewModel.SaveCorrectionCommand.Execute(null);
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);

        Assert.Equal("Manual", viewModel.SelectedFacts.Single(fact => fact.Label == "Outcome").KindLabel);
    }

    [Fact]
    public async Task Scans_during_a_raid_are_listed_with_an_inferred_item_and_an_estimated_value()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        service.SeedEvent(RaidId, "scan", JsonSerializer.Serialize(new ScanExecutionResult(
            true, true, "item-gpu", "Graphics card", 12_000, 12_000, "Take", new(0.93), Started.AddMinutes(5), "screenshot", "detail")));
        service.SeedEvent(RaidId, "scan", JsonSerializer.Serialize(new ScanExecutionResult(
            true, false, null, null, null, null, null, new(0), Started.AddMinutes(7), "screenshot", "detail")));
        service.SeedEvent(RaidId, "scan", JsonSerializer.Serialize(ScanExecutionResult.Unavailable("no recogniser", Started.AddMinutes(9))));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasSelectedScans);
        Assert.Equal("3 scans · 1 recognised · 1 unavailable", viewModel.SelectedScanSummary);
        var recognised = viewModel.SelectedScans[0];
        Assert.Equal("Graphics card", recognised.ItemLabel);
        Assert.Equal("Inferred", recognised.IdentityKindLabel);
        Assert.Equal("Estimate", recognised.ValueKindLabel);
        Assert.Contains("12", recognised.ValueLabel, StringComparison.Ordinal);
        Assert.Contains("Take", recognised.DetailLabel, StringComparison.Ordinal);
        var nothing = viewModel.SelectedScans[1];
        Assert.Equal("Nothing recognised", nothing.ItemLabel);
        Assert.False(nothing.HasIdentityKind);
        Assert.False(nothing.HasValue);
        Assert.Equal("Scan unavailable", viewModel.SelectedScans[2].ItemLabel);
    }

    [Fact]
    public async Task Marking_a_scan_wrong_keeps_the_row_but_removes_it_from_totals_and_can_be_restored()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        service.SeedEvent(RaidId, "scan", JsonSerializer.Serialize(new ScanExecutionResult(
            true, true, "item-gpu", "Graphics card", 12_000, 12_000, "Take", new(0.93), Started.AddMinutes(5), "screenshot", "detail")));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths(), new FixedClock(Started.AddHours(1)));

        await viewModel.LoadAsync();
        var scanId = Assert.Single(viewModel.SelectedScans).ScanId;
        await viewModel.CorrectScanAsync(scanId, isWrong: true);

        var wrong = Assert.Single(viewModel.SelectedScans);
        Assert.True(wrong.IsWrong);
        Assert.Equal("0 scans · 0 recognised · 1 marked wrong", viewModel.SelectedScanSummary);
        Assert.Single(await service.ListEventPayloadsAsync(RaidId, RaidScanCorrection.EventType, CancellationToken.None));

        await viewModel.CorrectScanAsync(scanId, isWrong: false);

        Assert.False(Assert.Single(viewModel.SelectedScans).IsWrong);
        Assert.Equal("1 scan · 1 recognised", viewModel.SelectedScanSummary);
    }

    [Fact]
    public async Task A_raid_with_no_scans_says_so_and_a_screenshot_distance_is_labelled_an_estimate()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), null, null));
        service.SeedPositions(RaidId, [Position(Started), Position(Started.AddMinutes(1))]);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasSelectedScans);
        Assert.Equal("No scans during this raid.", viewModel.SelectedScanSummary);
        Assert.Equal("Estimate", viewModel.SelectedDistanceKindLabel);
    }

    [Fact]
    public async Task Searching_matches_notes_and_the_per_map_stats_follow_the_filtered_set()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), "Survived", "Dorms then RUAF roadblock"));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), "Survived", "Scav run, nothing found"));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        viewModel.SearchText = "dorms";

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, row.RaidId);
        Assert.Equal("1 of 2 raids", viewModel.Status);
        var stat = Assert.Single(viewModel.MapStats);
        Assert.Equal("customs", stat.MapLabel);
    }

    [Fact]
    public async Task Clearing_search_restores_every_raid()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), "Survived", "Dorms"));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), "Survived", "Nothing found"));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        viewModel.SearchText = "dorms";
        Assert.Single(viewModel.Raids);

        ((DelegateCommand)viewModel.ClearSearchCommand).Execute(null);

        Assert.Equal(2, viewModel.Raids.Count);
        Assert.False(viewModel.HasActiveFilters);
    }

    [Theory]
    [InlineData(DebriefOutcomeFilter.Survived, "Survived", true)]
    [InlineData(DebriefOutcomeFilter.Survived, "Closed on restart", false)]
    [InlineData(DebriefOutcomeFilter.Died, "Killed by scav", true)]
    [InlineData(DebriefOutcomeFilter.Mia, "MIA after disconnect", true)]
    [InlineData(DebriefOutcomeFilter.RunThrough, "Run-through, nothing seen", true)]
    public async Task Outcome_filter_matches_by_keyword_over_the_free_text_outcome(
        DebriefOutcomeFilter filter, string outcome, bool expectMatch)
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), outcome, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        viewModel.OutcomeFilter = filter;

        Assert.Equal(expectMatch ? 1 : 0, viewModel.Raids.Count);
    }

    [Fact]
    public async Task Side_filter_matches_the_side_read_from_the_raids_state_events()
    {
        var pmcRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.SeedEvent(RaidId, "state", """{"Side":"scav"}""");
        service.Seed(new RaidHistoryEntry(pmcRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.SeedEvent(pmcRaidId, "state", """{"Side":"PMC"}""");
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        viewModel.SideFilter = DebriefSideFilter.Scav;

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, row.RaidId);
    }

    [Fact]
    public async Task Map_filter_narrows_the_list_to_one_map()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        var customsOption = Assert.Single(viewModel.MapFilterOptions, option => option.MapId == "customs");

        viewModel.SelectedMapFilterOption = customsOption;

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, row.RaidId);
    }

    [Fact]
    public async Task Date_range_excludes_raids_outside_it_by_the_players_local_day()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started.AddDays(-10), Started.AddDays(-10).AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        viewModel.DateFrom = Started.AddDays(-1);

        var row = Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, row.RaidId);
    }

    [Fact]
    public async Task Clear_filters_resets_search_map_outcome_side_and_dates()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), "Survived", "Dorms"));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        viewModel.SearchText = "dorms";
        viewModel.OutcomeFilter = DebriefOutcomeFilter.Survived;
        viewModel.SideFilter = DebriefSideFilter.Pmc;
        viewModel.DateFrom = Started;
        Assert.True(viewModel.HasActiveFilters);

        ((DelegateCommand)viewModel.ClearFiltersCommand).Execute(null);

        Assert.False(viewModel.HasActiveFilters);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Single(viewModel.Raids);
    }

    [Fact]
    public async Task Deleting_a_raid_previews_it_then_hides_it_and_updates_the_map_stats()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        Assert.False(viewModel.IsConfirmingDelete);

        ((DelegateCommand)viewModel.BeginDeleteCommand).Execute(null);
        Assert.True(viewModel.IsConfirmingDelete);
        Assert.Contains("customs", viewModel.DeletePreviewLabel, StringComparison.Ordinal);

        await ((AsyncDelegateCommand)viewModel.ConfirmDeleteCommand).ExecuteAsync();

        Assert.False(viewModel.IsConfirmingDelete);
        Assert.Single(viewModel.Raids);
        Assert.Equal(otherRaidId, viewModel.Raids[0].RaidId);
        Assert.Single(viewModel.MapStats);
        Assert.Equal("woods", viewModel.MapStats[0].MapLabel);
        Assert.True(viewModel.CanUndoDelete);
        Assert.Contains("customs", viewModel.UndoDeleteSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_delete_preview_leaves_the_raid_in_place()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        ((DelegateCommand)viewModel.BeginDeleteCommand).Execute(null);

        ((DelegateCommand)viewModel.CancelDeleteCommand).Execute(null);

        Assert.False(viewModel.IsConfirmingDelete);
        Assert.Single(viewModel.Raids);
        Assert.False(viewModel.CanUndoDelete);
    }

    [Fact]
    public async Task Undo_restores_a_deleted_raid_and_clears_the_banner()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        ((DelegateCommand)viewModel.BeginDeleteCommand).Execute(null);
        await ((AsyncDelegateCommand)viewModel.ConfirmDeleteCommand).ExecuteAsync();
        Assert.Empty(viewModel.Raids);
        Assert.True(viewModel.CanUndoDelete);

        await ((AsyncDelegateCommand)viewModel.UndoDeleteCommand).ExecuteAsync();

        Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, viewModel.Raids[0].RaidId);
        Assert.False(viewModel.CanUndoDelete);
        Assert.Equal(0, service.PurgedCount);
    }

    [Fact]
    public async Task A_second_delete_finalizes_the_first_undo_can_no_longer_restore_it()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        ((DelegateCommand)viewModel.BeginDeleteCommand).Execute(null);
        await ((AsyncDelegateCommand)viewModel.ConfirmDeleteCommand).ExecuteAsync();
        Assert.Equal(0, service.PurgedCount);

        await viewModel.SelectRaidAsync(otherRaidId, CancellationToken.None);
        ((DelegateCommand)viewModel.BeginDeleteCommand).Execute(null);
        await ((AsyncDelegateCommand)viewModel.ConfirmDeleteCommand).ExecuteAsync();

        // The undo banner now names only the most recent delete; the first raid's undo window is
        // over and its row was hard-deleted rather than left soft-deleted forever.
        Assert.Empty(viewModel.Raids);
        Assert.Equal(1, service.PurgedCount);
        Assert.Contains("woods", viewModel.UndoDeleteSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_before_a_date_previews_the_count_then_removes_only_the_older_raids()
    {
        var oldRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(oldRaidId, Guid.NewGuid(), "woods", "Pmc", Started.AddDays(-10), Started.AddDays(-10).AddMinutes(10), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        viewModel.DeleteBeforeDate = Started.AddDays(-1);

        ((DelegateCommand)viewModel.BeginBulkDeleteCommand).Execute(null);
        Assert.True(viewModel.IsConfirmingBulkDelete);
        Assert.Contains("1 raid", viewModel.BulkDeletePreviewLabel, StringComparison.Ordinal);

        await ((AsyncDelegateCommand)viewModel.ConfirmBulkDeleteCommand).ExecuteAsync();

        Assert.False(viewModel.IsConfirmingBulkDelete);
        var remaining = Assert.Single(viewModel.Raids);
        Assert.Equal(RaidId, remaining.RaidId);
        Assert.True(viewModel.CanUndoDelete);
    }

    [Fact]
    public async Task Delete_before_with_no_matching_raids_does_not_enter_preview()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        viewModel.DeleteBeforeDate = Started.AddDays(-30);

        ((DelegateCommand)viewModel.BeginBulkDeleteCommand).Execute(null);

        Assert.False(viewModel.IsConfirmingBulkDelete);
        Assert.Single(viewModel.Raids);
    }

    [Fact]
    public async Task Saving_manual_kills_and_value_writes_through_and_shows_as_manual_facts()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        viewModel.ManualPmcKills = 2;
        viewModel.ManualScavKills = 1;
        viewModel.ManualBossKills = 0;
        viewModel.ManualValueRoubles = 450_000;

        await ((AsyncDelegateCommand)viewModel.SaveManualMetadataCommand).ExecuteAsync();

        var stored = await service.GetManualMetadataAsync(RaidId, CancellationToken.None);
        Assert.Equal(new RaidManualMetadata(2, 1, 0, 450_000), stored);
        Assert.Contains(viewModel.SelectedFacts, fact => fact.Label == "PMC kills" && fact.Value == "2" && fact.KindLabel == "Manual");
        Assert.Contains(viewModel.SelectedFacts, fact => fact.Label == "Boss kills" && fact.Value == "0" && fact.KindLabel == "Manual");
        Assert.Contains(viewModel.SelectedFacts, fact => fact.Label == "Value brought out" && fact.KindLabel == "Manual");
    }

    [Fact]
    public async Task A_raid_with_no_manual_fields_shows_none_of_them_as_facts()
    {
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);

        Assert.DoesNotContain(viewModel.SelectedFacts, fact => fact.Label == "PMC kills");
        Assert.DoesNotContain(viewModel.SelectedFacts, fact => fact.Label == "Value brought out");
        Assert.Null(viewModel.ManualPmcKills);
    }

    [Fact]
    public async Task Selecting_a_different_raid_loads_its_own_manual_fields()
    {
        var otherRaidId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(otherRaidId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(10), null, null));
        await service.SetManualMetadataAsync(RaidId, new RaidManualMetadata(3, 0, 1, 200_000), CancellationToken.None);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());
        await viewModel.LoadAsync();

        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        Assert.Equal(3, viewModel.ManualPmcKills);

        await viewModel.SelectRaidAsync(otherRaidId, CancellationToken.None);
        Assert.Null(viewModel.ManualPmcKills);
    }

    [Fact]
    public async Task Per_map_stats_sum_manual_kills_and_value_over_that_maps_raids_only()
    {
        var secondCustomsId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var woodsId = Guid.Parse("40000000-0000-0000-0000-000000000003");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(secondCustomsId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(10), null, null));
        service.Seed(new RaidHistoryEntry(woodsId, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(15), null, null));
        await service.SetManualMetadataAsync(RaidId, new RaidManualMetadata(2, 1, null, 100_000), CancellationToken.None);
        await service.SetManualMetadataAsync(secondCustomsId, new RaidManualMetadata(1, null, null, 50_000), CancellationToken.None);
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        var customs = Assert.Single(viewModel.MapStats, stat => stat.MapLabel == "customs");
        Assert.True(customs.HasManual);
        Assert.Contains("3 PMC", customs.ManualLabel, StringComparison.Ordinal);
        Assert.Contains("1 Scav", customs.ManualLabel, StringComparison.Ordinal);
        Assert.Contains("150,000 roubles", customs.ManualLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("boss", customs.ManualLabel, StringComparison.Ordinal);
        var woods = Assert.Single(viewModel.MapStats, stat => stat.MapLabel == "woods");
        Assert.False(woods.HasManual);
        Assert.Equal(string.Empty, woods.ManualLabel);
    }

    [Fact]
    public async Task Tags_can_be_added_removed_and_used_to_filter_raids()
    {
        var second = Guid.Parse("40000000-0000-0000-0000-000000000009");
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), null, null));
        service.Seed(new RaidHistoryEntry(second, Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(20), null, null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths(), new FixedClock(Started));

        await viewModel.LoadAsync();
        await viewModel.SelectRaidAsync(RaidId, CancellationToken.None);
        viewModel.NewTag = "Tasks";
        await viewModel.AddTagAsync();

        var selectedTag = Assert.Single(viewModel.SelectedTags);
        Assert.Equal("Tasks", selectedTag.Label);
        viewModel.SelectedTagFilterOption = Assert.Single(viewModel.TagFilterOptions, option => option.Tag == "Tasks");
        Assert.Equal(RaidId, Assert.Single(viewModel.Raids).RaidId);

        await viewModel.RemoveTagAsync("Tasks");

        Assert.Empty(viewModel.SelectedTags);
        Assert.DoesNotContain(viewModel.TagFilterOptions, option => option.Tag == "Tasks");
    }

    [Fact]
    public async Task Saved_view_remembers_search_and_filters_for_the_next_workspace()
    {
        var layout = new FakeWorkspaceLayoutStore();
        var service = new FakeRaidHistoryService();
        service.Seed(new RaidHistoryEntry(
            RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(20), "Survived", "Found a GPU"));
        service.Seed(new RaidHistoryEntry(
            Guid.Parse("40000000-0000-0000-0000-000000000010"), Guid.NewGuid(), "woods", "Pmc", Started, Started.AddMinutes(20), "Died", "No loot"));
        var first = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: layout);
        await first.LoadAsync();
        first.SearchText = "GPU";
        first.OutcomeFilter = DebriefOutcomeFilter.Survived;
        first.SelectedMapFilterOption = Assert.Single(first.MapFilterOptions, option => option.MapId == "customs");
        first.SavedViewName = "Good Customs";
        first.SaveViewCommand.Execute(null);

        var reopened = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: layout);
        await reopened.LoadAsync();
        reopened.SelectedSavedView = Assert.Single(reopened.SavedViews);
        reopened.ApplySavedViewCommand.Execute(null);

        Assert.Equal("GPU", reopened.SearchText);
        Assert.Equal(DebriefOutcomeFilter.Survived, reopened.OutcomeFilter);
        Assert.Equal("customs", reopened.SelectedMapFilterOption.MapId);
        Assert.Equal(RaidId, Assert.Single(reopened.Raids).RaidId);
    }

    private sealed class RecordingQuestCatalog : IQuestCatalog
    {
        public string? Language { get; private set; }

        public Task<QuestCatalogSnapshot?> GetAsync(GameMode gameMode, string language, CancellationToken cancellationToken)
        {
            Language = language;
            return Task.FromResult<QuestCatalogSnapshot?>(null);
        }
    }

    private sealed class StubProfileService : IPlayerProfileService
    {
        private static readonly PlayerProfile Profile = new(
            Guid.NewGuid(), "test", GameMode.Regular, 1, Faction.Unknown, null,
            new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(), new Dictionary<string, string>(), Started);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(PlayerProfile value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRaidHistoryService : IRaidHistoryService
    {
        private readonly Dictionary<Guid, RaidHistoryEntry> _raids = [];
        private readonly Dictionary<Guid, IReadOnlyList<ScreenshotPosition>> _positions = [];
        private readonly Dictionary<(Guid RaidId, string Type), List<string>> _events = [];
        private readonly HashSet<Guid> _deleted = [];
        private readonly Dictionary<Guid, RaidManualMetadata> _manual = [];

        public (string? Outcome, string? Notes)? LastCorrection { get; private set; }

        /// <summary>How many raids <see cref="PurgeDeletedAsync"/> has actually removed, across every call.</summary>
        public int PurgedCount { get; private set; }

        public void Seed(RaidHistoryEntry raid) => _raids[raid.Id] = raid;

        public void SeedPositions(Guid raidId, IReadOnlyList<ScreenshotPosition> positions) => _positions[raidId] = positions;

        public void SeedEvent(Guid raidId, string type, string payloadJson)
        {
            var key = (raidId, type);
            if (!_events.TryGetValue(key, out var payloads))
            {
                payloads = [];
                _events[key] = payloads;
            }

            payloads.Add(payloadJson);
        }

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
        {
            _raids[raid.Id] = raid;
            return Task.FromResult(raid.Id);
        }

        public Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken)
        {
            SeedEvent(raidId, type, payloadJson);
            return Task.CompletedTask;
        }

        public Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken)
        {
            LastCorrection = (outcome, notes);
            if (_raids.TryGetValue(raidId, out var raid))
            {
                // What the real service does in the same transaction: keep that it was corrected.
                if (RaidCorrection.Between(raid, outcome, notes, Started) is { } correction)
                {
                    SeedEvent(raidId, RaidCorrection.EventType, correction.ToPayload());
                }

                _raids[raidId] = raid with { Outcome = outcome, Notes = notes };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>(
                [.. _raids.Values.Where(raid => !_deleted.Contains(raid.Id))]);

        public Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken)
        {
            foreach (var raidId in raidIds)
            {
                _deleted.Add(raidId);
            }

            return Task.CompletedTask;
        }

        public Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken)
        {
            foreach (var raidId in raidIds)
            {
                _deleted.Remove(raidId);
            }

            return Task.CompletedTask;
        }

        public Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken)
        {
            foreach (var raidId in _deleted.Where(id => !exceptRaidIds.Contains(id)).ToArray())
            {
                _deleted.Remove(raidId);
                _raids.Remove(raidId);
                PurgedCount++;
            }

            return Task.CompletedTask;
        }

        public Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult(_manual.GetValueOrDefault(raidId));

        public Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken)
        {
            if (metadata.IsEmpty)
            {
                _manual.Remove(raidId);
            }
            else
            {
                _manual[raidId] = metadata;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult(_positions.GetValueOrDefault(raidId, []));

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(Guid raidId, string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(
                _events.TryGetValue((raidId, type), out var payloads) ? payloads : []);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(string mapId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceLayoutStore : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
