using System.Text.Json;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

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

    private sealed class FakeRaidHistoryService : IRaidHistoryService
    {
        private readonly Dictionary<Guid, RaidHistoryEntry> _raids = [];
        private readonly Dictionary<Guid, IReadOnlyList<ScreenshotPosition>> _positions = [];
        private readonly Dictionary<(Guid RaidId, string Type), List<string>> _events = [];

        public (string? Outcome, string? Notes)? LastCorrection { get; private set; }

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

        public Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([.. _raids.Values]);

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
}
