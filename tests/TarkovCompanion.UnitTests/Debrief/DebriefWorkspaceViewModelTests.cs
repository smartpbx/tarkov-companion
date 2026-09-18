using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
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

    private sealed class FakeRaidHistoryService : IRaidHistoryService
    {
        private readonly Dictionary<Guid, RaidHistoryEntry> _raids = [];
        private readonly Dictionary<Guid, IReadOnlyList<ScreenshotPosition>> _positions = [];

        public (string? Outcome, string? Notes)? LastCorrection { get; private set; }

        public void Seed(RaidHistoryEntry raid) => _raids[raid.Id] = raid;

        public void SeedPositions(Guid raidId, IReadOnlyList<ScreenshotPosition> positions) => _positions[raidId] = positions;

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
                _raids[raidId] = raid with { Outcome = outcome, Notes = notes };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([.. _raids.Values]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult(_positions.GetValueOrDefault(raidId, []));

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(Guid raidId, string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(string mapId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
