using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// A companion restarted mid-raid, against the record the previous run left behind.
/// </summary>
/// <remarks>
/// The raid lives in memory. Restarting mid-raid recovered the raid from the log and then gave
/// it a new identity: a new id, a start time of whenever the restart happened, and an empty
/// trail drawn over screenshots already on disk. The previous run's row kept <c>end_utc
/// NULL</c> for ever and History showed it as in progress.
///
/// Velopack restarts this application whenever it installs an update and it checks on every
/// launch, so this is an ordinary evening rather than a crash.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class RaidResumeRecordTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-09-14T02:00:00Z");

    [Fact]
    public async Task The_raid_keeps_the_identity_the_previous_run_gave_it()
    {
        await using var harness = await Harness.CreateAsync();
        var first = await harness.StartRaidAsync("customs", Started);

        var resumed = await harness.RestartAsync("customs", Started.AddMinutes(11));

        Assert.Equal(first, resumed.RaidId);
        Assert.Single(await harness.RaidsAsync());
    }

    [Fact]
    public async Task The_raid_keeps_the_time_it_actually_started()
    {
        // Not the moment the companion came back. A raid dated to the restart reports a
        // twenty-minute raid as a four-minute one for the rest of the wipe.
        await using var harness = await Harness.CreateAsync();
        await harness.StartRaidAsync("customs", Started);

        var resumed = await harness.RestartAsync("customs", Started.AddMinutes(11));

        Assert.Equal(Started, resumed.StartedUtc);
    }

    [Fact]
    public async Task The_trail_comes_back_from_the_screenshots_already_recorded()
    {
        // Every point is already a row, recorded as the screenshot was taken. Starting the
        // trail empty draws a raid the player has not walked.
        await using var harness = await Harness.CreateAsync();
        await harness.StartRaidAsync("customs", Started);
        await harness.RecordPositionAsync(Started.AddMinutes(2), 10, 20);
        await harness.RecordPositionAsync(Started.AddMinutes(4), 30, 40);

        var resumed = await harness.RestartAsync("customs", Started.AddMinutes(11));

        Assert.Equal(2, resumed.PositionTrail.Count);
        Assert.Equal(30, resumed.LastKnownPosition?.Position.X);
    }

    [Fact]
    public async Task A_raid_that_ended_while_the_companion_was_closed_is_not_adopted()
    {
        // The guard that matters. Adopting this would draw the last raid's trail across this
        // one and date this raid to whenever that one started.
        await using var harness = await Harness.CreateAsync();
        var stale = await harness.StartRaidAsync("customs", Started);

        var resumed = await harness.RestartAsync("customs", Started.AddHours(5));

        Assert.NotEqual(stale, resumed.RaidId);
        Assert.Equal(2, (await harness.RaidsAsync()).Count);
    }

    [Fact]
    public async Task A_row_that_is_not_this_raid_is_closed_rather_than_left_open_for_ever()
    {
        await using var harness = await Harness.CreateAsync();
        var abandoned = await harness.StartRaidAsync("woods", Started);

        await harness.RestartAsync("customs", Started.AddMinutes(11));

        var closed = (await harness.RaidsAsync()).Single(raid => raid.Id == abandoned);
        Assert.NotNull(closed.EndedUtc);
        Assert.Equal("Closed on restart", closed.Outcome);
    }

    [Fact]
    public async Task The_raid_that_was_adopted_is_not_closed_along_with_the_rest()
    {
        await using var harness = await Harness.CreateAsync();
        // The older raid comes first. Since #568 a raid that starts ends the one before it, so two
        // raids are never open together with the adopted one the older of the two; this used to
        // start customs and then woods, which now (correctly) closes customs the moment woods begins.
        var older = await harness.StartRaidAsync("woods", Started);
        var open = await harness.StartRaidAsync("customs", Started.AddMinutes(1));

        await harness.RestartAsync("customs", Started.AddMinutes(11));

        var raids = await harness.RaidsAsync();
        Assert.Null(raids.Single(raid => raid.Id == open).EndedUtc);
        Assert.NotNull(raids.Single(raid => raid.Id == older).EndedUtc);
    }

    [Fact]
    public async Task Events_recorded_after_the_resume_belong_to_the_original_raid()
    {
        // The point of keeping the identity. A raid whose second half is filed under a
        // different id is two half-records of something that happened once.
        await using var harness = await Harness.CreateAsync();
        var first = await harness.StartRaidAsync("customs", Started);
        await harness.RestartAsync("customs", Started.AddMinutes(11));

        await harness.RecordPositionAsync(Started.AddMinutes(12), 55, 66);

        Assert.Equal(1, await harness.CountAsync(first, "position"));
    }

    [Fact]
    public async Task A_raid_whose_map_the_replay_could_not_establish_adopts_nothing()
    {
        // Picking the newest open row on recency alone is the mistake this is guarding.
        await using var harness = await Harness.CreateAsync();
        var open = await harness.StartRaidAsync("customs", Started);

        var resumed = await harness.RestartAsync(null, Started.AddMinutes(11));

        Assert.NotEqual(open, resumed.RaidId);
        Assert.NotNull((await harness.RaidsAsync()).Single(raid => raid.Id == open).EndedUtc);
    }

    /// <summary>
    /// A raid that is still loading is a new raid, so there is nothing to take over.
    /// </summary>
    /// <remarks>
    /// The rows a previous run left open are still there, though, and this is still the moment
    /// to close them.
    /// </remarks>
    [Fact]
    public async Task Resuming_into_a_loading_raid_adopts_nothing_and_still_tidies_up()
    {
        await using var harness = await Harness.CreateAsync();
        var open = await harness.StartRaidAsync("customs", Started);

        var resumed = await harness.RestartAsync(
            "woods",
            Started.AddMinutes(11),
            RaidLifecycleState.LoadingRaid);

        Assert.Null(resumed.RaidId);
        Assert.NotNull((await harness.RaidsAsync()).Single(raid => raid.Id == open).EndedUtc);
    }

    /// <summary>
    /// A launch into the menu still closes what no raid could still be.
    /// </summary>
    /// <remarks>
    /// The resume only runs when the game says a raid is in progress, and most launches are
    /// not. A player who crashed mid-raid on Tuesday and opened the companion to look at the
    /// flea on Wednesday left Tuesday's row open for the rest of the wipe.
    /// </remarks>
    [Fact]
    public async Task Starting_in_the_menu_closes_a_raid_that_can_no_longer_be_running()
    {
        await using var harness = await Harness.CreateAsync();
        var crashed = await harness.StartRaidAsync("customs", Started);

        await harness.Sweep(Started.AddHours(20));

        var row = (await harness.RaidsAsync()).Single(raid => raid.Id == crashed);
        Assert.Equal("Closed on restart", row.Outcome);
    }

    [Fact]
    public async Task The_sweep_leaves_a_raid_the_resume_might_still_want()
    {
        // It runs before observation starts, so closing a row the resume is about to adopt
        // would be a race the resume could never win.
        await using var harness = await Harness.CreateAsync();
        var running = await harness.StartRaidAsync("customs", Started);

        await harness.Sweep(Started.AddMinutes(11));

        Assert.Null((await harness.RaidsAsync()).Single(raid => raid.Id == running).EndedUtc);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JsonFilePlayerProfileService _profiles;
        private readonly SqliteConnectionFactory _factory;
        private readonly ManualTimeProvider _clock;
        private RaidStateService _state;

        private Harness(
            string directory,
            SqliteConnectionFactory factory,
            JsonFilePlayerProfileService profiles,
            ManualTimeProvider clock)
        {
            _directory = directory;
            _factory = factory;
            _profiles = profiles;
            _clock = clock;
            _state = new();
            Coordinator = Build();
        }

        public RaidActivityCoordinator Coordinator { get; private set; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-raid-resume-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "resume.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var clock = new ManualTimeProvider(Started);
            var profiles = new JsonFilePlayerProfileService(new(Path.Combine(directory, "profile.json")), clock);
            return new(directory, factory, profiles, clock);
        }

        /// <summary>Opens a raid the way the log observer would, and says which one.</summary>
        public async Task<Guid> StartRaidAsync(string mapId, DateTimeOffset startedUtc)
        {
            await Coordinator.ApplyEvidenceAsync(
                new(
                    RaidEvidenceKind.LogLine,
                    startedUtc,
                    mapId,
                    RaidLifecycleState.InRaid,
                    new Confidence(0.95),
                    "A raid started.")
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    StartsNewRaid = true,
                },
                CancellationToken.None);
            return _state.Current.RaidId!.Value;
        }

        /// <summary>
        /// Throws away everything held in memory and comes back, the way a restart does.
        /// </summary>
        /// <remarks>
        /// The database is the only thing that survives, which is the whole premise. The
        /// evidence is the one the startup replay produces: a raid is running, on this map,
        /// noticed now.
        /// </remarks>
        public async Task<RaidSnapshot> RestartAsync(
            string? mapId,
            DateTimeOffset nowUtc,
            RaidLifecycleState state = RaidLifecycleState.InRaid)
        {
            _state = new();
            Coordinator = Build();
            return await Coordinator.ApplyEvidenceAsync(
                new(
                    RaidEvidenceKind.LogLine,
                    nowUtc,
                    mapId,
                    state,
                    new Confidence(0.80),
                    "A raid was already running when the companion started.")
                {
                    ResumesSession = true,
                },
                CancellationToken.None);
        }

        /// <summary>Comes back into the menu, which is what most launches are.</summary>
        public Task Sweep(DateTimeOffset nowUtc)
        {
            _state = new();
            _clock.Advance(nowUtc - _clock.GetUtcNow());
            Coordinator = Build();
            return Coordinator.CloseAbandonedAsync(CancellationToken.None);
        }

        public Task RecordPositionAsync(DateTimeOffset timestamp, double x, double z) =>
            Coordinator.ApplyPositionAsync(
                new(
                    timestamp,
                    new WorldPosition(x, 0, z),
                    new QuaternionOrientation(0, 0, 0, 1),
                    0,
                    null,
                    null,
                    $"{timestamp:yyyy-MM-dd_HH-mm-ss}_{x}_{z}.png"),
                CancellationToken.None);

        public async Task<IReadOnlyList<RaidHistoryEntry>> RaidsAsync() =>
            await new SqliteRaidHistoryService(_factory).ListAsync(CancellationToken.None);

        public async Task<long> CountAsync(Guid raidId, string type)
        {
            await using var connection = await _factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM raid_events WHERE raid_id = $raidId AND type = $type;";
            command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
            command.Parameters.AddWithValue("$type", type);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private RaidActivityCoordinator Build() => new(
            _state,
            new SqliteRaidHistoryService(_factory),
            _profiles,
            new RuntimeStateStore(
                new(
                    DemoMode: false,
                    Offline: true,
                    GameMode.Regular,
                    "en",
                    TimeSpan.FromHours(9),
                    TimeSpan.FromMinutes(5)),
                _clock),
            mapDataService: null,
            timeProvider: _clock);

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
