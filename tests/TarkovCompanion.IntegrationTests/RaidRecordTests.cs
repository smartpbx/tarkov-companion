using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// What a raid's own record holds, against what the game actually said during it.
/// </summary>
/// <remarks>
/// <c>RaidActivityCoordinator</c> wrote <c>position</c>, <c>scan</c>, <c>state</c> and
/// <c>extracts</c> and nothing else. Meanwhile <c>QuestLogProgressService</c> saw every quest
/// the game announced and applied it to recorded progress, and <c>FleaSaleStateService</c> kept
/// every sale — for the session only, by its own remark. Neither was ever tied to the raid it
/// happened in, so a raid's record was thinner than what the game had said during it.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class RaidRecordTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T02:00:00Z");

    [Fact]
    public async Task A_sale_made_during_a_raid_is_recorded_against_it()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartRaidAsync();

        await harness.Coordinator.RecordSaleAsync(Sale("offer-1"), CancellationToken.None);

        Assert.Equal(1, await harness.CountAsync(raidId, "sale"));
    }

    [Fact]
    public async Task A_sale_made_in_the_menu_belongs_to_no_raid()
    {
        // Most selling is done in the menu. Attaching one to the last raid would put it in the
        // record of something that had already finished.
        await using var harness = await Harness.CreateAsync();

        await harness.Coordinator.RecordSaleAsync(Sale("offer-1"), CancellationToken.None);

        Assert.Equal(0, await harness.CountAsync(null, "sale"));
    }

    [Fact]
    public async Task A_quest_announced_during_a_raid_is_recorded_against_it()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartRaidAsync();

        await harness.Coordinator.RecordQuestAsync(Quest("task-debut"), CancellationToken.None);

        Assert.Equal(1, await harness.CountAsync(raidId, "quest"));
    }

    [Fact]
    public async Task A_quest_announced_in_the_menu_belongs_to_no_raid()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.Coordinator.RecordQuestAsync(Quest("task-debut"), CancellationToken.None);

        Assert.Equal(0, await harness.CountAsync(null, "quest"));
    }

    [Fact]
    public async Task Recording_one_does_not_disturb_the_other()
    {
        // They are separate event types on one raid, and a reader asking for sales must not
        // get quests back.
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartRaidAsync();

        await harness.Coordinator.RecordSaleAsync(Sale("offer-1"), CancellationToken.None);
        await harness.Coordinator.RecordSaleAsync(Sale("offer-2"), CancellationToken.None);
        await harness.Coordinator.RecordQuestAsync(Quest("task-debut"), CancellationToken.None);

        Assert.Equal(2, await harness.CountAsync(raidId, "sale"));
        Assert.Equal(1, await harness.CountAsync(raidId, "quest"));
    }

    [Fact]
    public async Task What_was_recorded_survives_the_raid_ending()
    {
        // The whole point over FleaSaleStateService, which keeps sales for the session and says
        // so in its own remark. A record that vanished with the process would answer "what
        // happened in that raid" only until somebody closed the application.
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartRaidAsync();
        await harness.Coordinator.RecordSaleAsync(Sale("offer-1"), CancellationToken.None);

        await harness.EndRaidAsync();

        Assert.Equal(1, await harness.CountAsync(raidId, "sale"));
    }

    private static FleaSaleObservation Sale(string offerId) => new(offerId, "item-salewa", 2, Now);

    private static QuestStatusObservation Quest(string taskId) =>
        new($"event-{taskId}", taskId, RecordedTaskState.Completed, Now);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JsonFilePlayerProfileService _profiles;

        private Harness(
            string directory,
            SqliteConnectionFactory factory,
            JsonFilePlayerProfileService profiles,
            RaidStateService state,
            RaidActivityCoordinator coordinator)
        {
            _directory = directory;
            _profiles = profiles;
            Factory = factory;
            State = state;
            Coordinator = coordinator;
        }

        public SqliteConnectionFactory Factory { get; }

        public RaidStateService State { get; }

        public RaidActivityCoordinator Coordinator { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-raid-record-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "record.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);

            var clock = new ManualTimeProvider(Now);
            var profiles = new JsonFilePlayerProfileService(new(Path.Combine(directory, "profile.json")), clock);
            var state = new RaidStateService();
            var coordinator = new RaidActivityCoordinator(
                state,
                new SqliteRaidHistoryService(factory),
                profiles,
                new RuntimeStateStore(
                    new(
                        DemoMode: false,
                        Offline: true,
                        GameMode.Regular,
                        "en",
                        TimeSpan.FromHours(9),
                        TimeSpan.FromMinutes(5)),
                    clock));
            return new(directory, factory, profiles, state, coordinator);
        }

        /// <summary>Opens a raid the way the log observer would, and says which one.</summary>
        public async Task<Guid> StartRaidAsync()
        {
            await Coordinator.ApplyEvidenceAsync(
                new(
                    RaidEvidenceKind.LogLine,
                    Now,
                    "customs",
                    RaidLifecycleState.InRaid,
                    new Confidence(0.95),
                    "A raid started."),
                CancellationToken.None);
            return State.Current.RaidId!.Value;
        }

        public Task EndRaidAsync() => Coordinator.ApplyEvidenceAsync(
            new(
                RaidEvidenceKind.LogLine,
                Now.AddMinutes(20),
                null,
                RaidLifecycleState.Menu,
                new Confidence(0.95),
                "The raid ended."),
            CancellationToken.None);

        /// <summary>How many events of one type the raid holds, or the whole table for null.</summary>
        public async Task<long> CountAsync(Guid? raidId, string type)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = raidId is null
                ? "SELECT COUNT(*) FROM raid_events WHERE type = $type;"
                : "SELECT COUNT(*) FROM raid_events WHERE raid_id = $raidId AND type = $type;";
            command.Parameters.AddWithValue("$type", type);
            if (raidId is { } id)
            {
                command.Parameters.AddWithValue("$raidId", id.ToString("D"));
            }

            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync()
        {
            _profiles.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                ScratchDirectory.Remove(_directory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
