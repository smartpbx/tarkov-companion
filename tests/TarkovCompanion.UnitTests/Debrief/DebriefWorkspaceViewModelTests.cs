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

    private static AppDataPaths TestPaths() => AppDataPaths.Resolve(
        Path.Combine(Path.GetTempPath(), $"tarkov-companion-debrief-tests-{Guid.NewGuid():N}"));

    private static ScreenshotPosition Position(DateTimeOffset timestamp) => new(
        timestamp,
        new WorldPosition(0, 0, 0),
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
