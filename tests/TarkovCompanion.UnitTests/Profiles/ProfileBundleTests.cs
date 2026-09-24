using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileBundleTests
{
    [Fact]
    public async Task AProfileExportedAndImportedAsNewArrivesWhole()
    {
        var world = new World();
        var source = world.Add("Main", ProfileGameMode.Pvp);
        await world.Players.SaveAsync(world.Players.Profiles[source] with
        {
            Level = 42,
            Faction = Faction.Bear,
            TraderLevels = new Dictionary<string, int> { ["prapor"] = 3 },
            CompletedTaskIds = new HashSet<string> { "debut", "checking" },
            HideoutStationLevels = new Dictionary<string, int> { ["lavatory"] = 2, ["medstation"] = 1 },
            WishlistItemIds = new HashSet<string> { "gpu" },
            ItemOverrides = new Dictionary<string, string> { ["bolts"] = "keep" },
            EventItemStates = new Dictionary<string, EventItemState> { ["bandage"] = EventItemState.Allergic },
        }, CancellationToken.None);
        var scope = world.Scope(source);
        await world.Quests.ApplyAsync(new SetTaskStateMutation(scope, "Main", "debut", RecordedTaskState.Completed,
            QuestProgressActor.User, "Manual", Guid.NewGuid(), World.Now), CancellationToken.None);
        await world.Quests.ApplyAsync(new SetQuestPinMutation(scope, "Main", QuestPinTargetKind.Task, "shortage", true, 0, "soon",
            QuestProgressActor.User, "Manual", Guid.NewGuid(), World.Now), CancellationToken.None);
        var raidId = await world.Raids.StartAsync(new RaidHistoryEntry(Guid.NewGuid(), source, "customs", "Regular",
            World.Now.AddHours(-2), World.Now.AddHours(-1.5), "Survived", "good raid"), CancellationToken.None);
        await world.Raids.SetManualMetadataAsync(raidId, new RaidManualMetadata(2, 5, null, 400_000), CancellationToken.None);
        // Another profile's raid must not travel with this one.
        await world.Raids.StartAsync(new RaidHistoryEntry(Guid.NewGuid(), Guid.NewGuid(), "woods", "Regular",
            World.Now, null, null, null), CancellationToken.None);

        var service = world.Service();
        var json = ProfileBundleCodec.Write(await service.ExportAsync(CancellationToken.None));
        var preview = await service.PreviewAsync(json, ProfileBundleTarget.NewProfile, CancellationToken.None);
        Assert.Null(preview.Refusal);
        Assert.Contains(preview.Changes, change => change.Area == "Level" && change.After == "42");
        Assert.Contains(preview.Changes, change => change.Area == "Raids" && change.After == "1");

        Assert.Equal("Main", await service.ImportAsync(preview, CancellationToken.None));

        var target = world.Active;
        Assert.NotEqual(source, target);
        var imported = world.Players.Profiles[target];
        Assert.Equal(42, imported.Level);
        Assert.Equal(Faction.Bear, imported.Faction);
        Assert.Equal(3, imported.TraderLevels["prapor"]);
        Assert.True(imported.CompletedTaskIds.SetEquals(["debut", "checking"]));
        Assert.Equal(2, imported.HideoutStationLevels["lavatory"]);
        Assert.Contains("gpu", imported.WishlistItemIds);
        Assert.Equal("keep", imported.ItemOverrides["bolts"]);
        Assert.Equal(EventItemState.Allergic, imported.EventItemStates["bandage"]);

        var quests = await world.Quests.GetAsync(world.Scope(target), CancellationToken.None);
        Assert.Equal(RecordedTaskState.Completed, quests.Tasks["debut"].State);
        Assert.Equal("soon", Assert.Single(quests.Pins).Note);

        var raid = Assert.Single(world.Raids.Entries, entry => entry.ProfileId == target);
        Assert.NotEqual(raidId, raid.Id);
        Assert.Equal("Survived", raid.Outcome);
        Assert.Equal(400_000, (await world.Raids.GetManualMetadataAsync(raid.Id, CancellationToken.None))!.ValueRoubles);

        // The same file again finds nothing new to add to the profile it already went into.
        var again = await service.PreviewAsync(json, ProfileBundleTarget.ActiveProfile, CancellationToken.None);
        Assert.Empty(again.Changes);
    }

    [Fact]
    public void AnImportedProfileDoesNotTakeANameAlreadyInUse()
    {
        Assert.Equal("Main", TarkovCompanion.App.Services.ProfileTransferComposition.UnusedName("Main", ["Alt"]));
        Assert.Equal("Main (imported)", TarkovCompanion.App.Services.ProfileTransferComposition.UnusedName("Main", ["main"]));
        Assert.Equal(
            "Main (imported 2)",
            TarkovCompanion.App.Services.ProfileTransferComposition.UnusedName("Main", ["Main", "Main (imported)"]));
    }

    [Fact]
    public void AFileFromANewerVersionIsRefusedNotHalfRead()
    {
        var json = """{ "format": "tarkov-companion/profile", "version": 2, "profile": { "name": "Main", "mode": "Pvp", "wipe": "0.16" }, "progress": { "level": 3 } }""";

        var refused = Assert.Throws<ProfileBundleVersionException>(() => ProfileBundleCodec.Read(json));
        Assert.Equal(2, refused.FileVersion);
        Assert.Contains("newer version", refused.Message);
    }

    [Theory]
    [InlineData("""{ "format": "something-else", "version": 1 }""")]
    [InlineData("""{ "format": "tarkov-companion/profile" }""")]
    [InlineData("""{ "format": "tarkov-companion/profile", "version": 1, "profile": { "name": "", "mode": "Pvp" }, "progress": {} }""")]
    [InlineData("""{ "format": "tarkov-companion/profile", "version": 1, "profile": { "name": "Main", "mode": "Pvp" } }""")]
    [InlineData("not json")]
    public void AForeignOrIncompleteFileIsRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => ProfileBundleCodec.Read(json));

    [Fact]
    public void UnknownFieldsAreIgnoredAndMissingListsReadEmpty()
    {
        var bundle = ProfileBundleCodec.Read(
            """{ "format": "tarkov-companion/profile", "version": 1, "extra": true, "profile": { "name": "Main", "mode": "Pve", "wipe": "w" }, "progress": { "level": 7, "somethingNew": 1 } }""");

        Assert.Equal(7, bundle.Progress.Level);
        Assert.Equal(ProfileGameMode.Pve, bundle.Profile.Mode);
        Assert.Empty(bundle.Progress.WishlistItemIds);
        Assert.Empty(bundle.Quests.Tasks);
        Assert.Empty(bundle.Raids);
    }

    [Fact]
    public async Task AFileOfAnotherModeIsNotWrittenIntoTheActiveProfile()
    {
        var world = new World();
        world.Add("Main", ProfileGameMode.Pvp);
        var service = world.Service();
        var pve = ProfileBundleCodec.Write((await service.ExportAsync(CancellationToken.None)) with
        {
            Profile = new ProfileBundleIdentity("Other", ProfileGameMode.Pve, "w"),
        });

        var preview = await service.PreviewAsync(pve, ProfileBundleTarget.ActiveProfile, CancellationToken.None);

        Assert.False(preview.CanImportIntoActive);
        Assert.Contains("PvE", preview.Refusal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(preview, CancellationToken.None));
    }

    /// <summary>
    /// [#269] A raid played in another mode is refused on import and said so in the preview, even
    /// inside a file whose own mode matches (a profile whose mode was corrected after it raided).
    /// </summary>
    [Fact]
    public async Task RaidsFromAnotherModeAreNotImported()
    {
        var world = new World();
        var source = world.Add("Main", ProfileGameMode.Pvp);
        await world.Raids.StartAsync(new RaidHistoryEntry(Guid.NewGuid(), source, "customs", "Regular",
            World.Now.AddHours(-3), World.Now.AddHours(-2.5), "Survived", null), CancellationToken.None);
        await world.Raids.StartAsync(new RaidHistoryEntry(Guid.NewGuid(), source, "woods", "Pve",
            World.Now.AddHours(-2), World.Now.AddHours(-1.5), "Killed", null), CancellationToken.None);
        var service = world.Service();
        var json = ProfileBundleCodec.Write(await service.ExportAsync(CancellationToken.None));

        var preview = await service.PreviewAsync(json, ProfileBundleTarget.NewProfile, CancellationToken.None);

        Assert.Contains(preview.Changes, change => change.Area == "Raids" && change.After == "1");
        Assert.Contains(preview.Changes, change => change.Area == "Raids from another mode" && change.Now == "1");
        await service.ImportAsync(preview, CancellationToken.None);
        var target = world.Active;
        var landed = (await world.Raids.ListAsync(CancellationToken.None)).Where(raid => raid.ProfileId == target).ToArray();
        Assert.Equal("customs", Assert.Single(landed).MapId);
    }

    [Theory]
    [InlineData("Regular", ProfileGameMode.Pvp)]
    [InlineData("Pve", ProfileGameMode.Pve)]
    [InlineData("PvpSeason", ProfileGameMode.Seasonal)]
    [InlineData("something else", null)]
    public void ARaidsRecordedModeIsReadAsAProfileMode(string recorded, ProfileGameMode? expected) =>
        Assert.Equal(expected, ProfileBundleChanges.RaidMode(recorded));

    private sealed class World
    {
        public static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        private readonly Dictionary<Guid, ProfileBundleIdentity> _identities = [];

        public World()
        {
            Players = new FakePlayers(this);
        }

        public Guid Active { get; set; }

        public FakePlayers Players { get; }

        public FakeQuests Quests { get; } = new();

        public FakeRaids Raids { get; } = new();

        public Guid Add(string name, ProfileGameMode mode)
        {
            var id = Guid.NewGuid();
            _identities[id] = new(name, mode, "0.16");
            Players.Profiles[id] = new PlayerProfile(
                id, name, mode == ProfileGameMode.Pve ? GameMode.Pve : GameMode.Regular, 1, Faction.Unknown, null,
                new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
                new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, EventItemState>(),
                new Dictionary<string, string>(), Now, $"gen-{id:N}");
            Active = id;
            return id;
        }

        public QuestProfileScope Scope(Guid id) =>
            new(id, Players.Profiles[id].GameMode, Players.Profiles[id].ProfileGeneration);

        public ProfileBundleService Service() => new(
            Players,
            Quests,
            Raids,
            _ => Task.FromResult<ProfileBundleIdentity?>(_identities.GetValueOrDefault(Active)),
            (identity, _) =>
            {
                Add(identity.Name, identity.Mode);
                return Task.CompletedTask;
            });
    }

    private sealed class FakePlayers(World world) : IPlayerProfileService
    {
        public Dictionary<Guid, PlayerProfile> Profiles { get; } = [];

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profiles[world.Active]);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
        {
            Profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeQuests : IQuestProgressStore
    {
        private readonly Dictionary<QuestProfileScope, QuestProgressSnapshot> _snapshots = [];

        public Task<QuestProgressSnapshot> GetAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Get(scope));

        public Task<QuestProgressCommandResult> ApplyAsync(QuestProgressMutation mutation, CancellationToken cancellationToken)
        {
            var current = Get(mutation.Scope);
            var revision = current.Revision + 1;
            var next = mutation switch
            {
                SetTaskStateMutation task => current with
                {
                    Tasks = new Dictionary<string, RecordedTaskProgress>(current.Tasks)
                    {
                        [task.TaskId] = new(task.TaskId, task.State, task.Source, revision, task.RecordedUtc),
                    },
                },
                SetObjectiveProgressMutation objective => current with
                {
                    Objectives = new Dictionary<string, RecordedObjectiveProgress>(current.Objectives)
                    {
                        [objective.ObjectiveId] = new(objective.ObjectiveId, objective.State, objective.Count, objective.Source, revision, objective.RecordedUtc),
                    },
                },
                SetItemHoldingMutation holding => current with
                {
                    ItemHoldings = [.. current.ItemHoldings, new(holding.ItemId, holding.FoundInRaid, holding.Count ?? 0, holding.Source, revision, holding.RecordedUtc)],
                },
                SetQuestPinMutation pin => current with
                {
                    Pins = [.. current.Pins, new(pin.TargetKind, pin.TargetId, pin.SortOrder, pin.Note, pin.Source, revision, pin.RecordedUtc)],
                },
                _ => throw new NotSupportedException(),
            };
            _snapshots[mutation.Scope] = next with { Revision = revision };
            return Task.FromResult(new QuestProgressCommandResult(mutation.CorrelationId, revision, true));
        }

        public Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestProgressChange>>([]);

        private QuestProgressSnapshot Get(QuestProfileScope scope) => _snapshots.GetValueOrDefault(scope)
            ?? new(scope, 0, new Dictionary<string, RecordedTaskProgress>(), new Dictionary<string, RecordedObjectiveProgress>(), [], []);
    }

    private sealed class FakeRaids : IRaidHistoryService
    {
        private readonly Dictionary<Guid, RaidManualMetadata> _manual = [];

        public List<RaidHistoryEntry> Entries { get; } = [];

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
        {
            if (Entries.Any(entry => entry.Id == raid.Id))
            {
                throw new InvalidOperationException("duplicate raid id");
            }

            Entries.Add(raid);
            return Task.FromResult(raid.Id);
        }

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([.. Entries]);

        public Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken) =>
            Task.FromResult(_manual.GetValueOrDefault(raidId));

        public Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken)
        {
            _manual[raidId] = metadata;
            return Task.CompletedTask;
        }

        public Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(Guid raidId, string type, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(string mapId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
