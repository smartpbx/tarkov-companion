using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileContextV2Tests
{
    [Fact]
    public async Task Switching_publishes_one_atomic_revision_and_never_merges_profile_state()
    {
        var store = new MemoryStore();
        using var service = new ProfileContextService(store, new FixedTimeProvider());
        var published = new List<long>();
        service.ContextChanged += change => published.Add(change.Snapshot.Revision);
        var pvp = Profile("00000000-0000-0000-0000-000000000001", "pvp-a", ProfileGameMode.Pvp, "bolts");
        var pve = Profile("00000000-0000-0000-0000-000000000002", "pve-b", ProfileGameMode.Pve, "ledx");

        await service.CreateAsync(new(pvp.Context, pvp.Name, pvp.Progress), CancellationToken.None);
        await service.CreateAsync(new(pve.Context, pve.Name, pve.Progress), CancellationToken.None);
        var switched = await service.SwitchAsync(pvp.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal(3, switched.Revision);
        Assert.Equal(pvp.Context.Identity.ProfileId, switched.ActiveProfileId);
        Assert.Equal([1L, 2L, 3L], published);
        Assert.Contains("bolts", switched.ActiveProfile.Progress.WishlistItemIds);
        Assert.DoesNotContain("ledx", switched.ActiveProfile.Progress.WishlistItemIds);
        Assert.Equal(ProfileGameMode.Pvp, switched.ActiveProfile.Context.Mode);
    }

    [Fact]
    public async Task Archive_restore_and_compare_keep_contexts_separate()
    {
        var store = new MemoryStore();
        using var service = new ProfileContextService(store, new FixedTimeProvider());
        var pvp = Profile("00000000-0000-0000-0000-000000000003", "pvp-a", ProfileGameMode.Pvp, "bolts");
        var seasonal = Profile("00000000-0000-0000-0000-000000000004", "season-b", ProfileGameMode.Seasonal, "ledx");
        await service.CreateAsync(new(pvp.Context, pvp.Name, pvp.Progress), CancellationToken.None);
        await service.CreateAsync(new(seasonal.Context, seasonal.Name, seasonal.Progress), CancellationToken.None);
        await service.SwitchAsync(pvp.Context.Identity.ProfileId, CancellationToken.None);

        var archived = await service.ArchiveAsync(seasonal.Context.Identity.ProfileId, CancellationToken.None);
        Assert.Equal(ProfileLifecycle.Archived, archived.Profiles.Single(profile => profile.Context.Identity.ProfileId == seasonal.Context.Identity.ProfileId).Lifecycle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(seasonal.Context.Identity.ProfileId, CancellationToken.None));
        var restored = await service.RestoreAsync(seasonal.Context.Identity.ProfileId, CancellationToken.None);
        var comparison = await service.CompareAsync(pvp.Context.Identity.ProfileId, seasonal.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal(ProfileLifecycle.Active, restored.Profiles.Single(profile => profile.Context.Identity.ProfileId == seasonal.Context.Identity.ProfileId).Lifecycle);
        Assert.False(comparison.SameMode);
        Assert.False(comparison.SameWipeSeason);
        Assert.Equal(1, comparison.LeftWishlistItems);
        Assert.Equal(1, comparison.RightWishlistItems);
    }

    [Fact]
    public async Task Wrong_generation_is_quarantined_and_confirm_only_imports_reviewed_new_contexts()
    {
        var store = new MemoryStore();
        using var contexts = new ProfileContextService(store, new FixedTimeProvider());
        var local = Profile("00000000-0000-0000-0000-000000000005", "generation-a", ProfileGameMode.Pvp, "local");
        var incomingWrongGeneration = Profile("00000000-0000-0000-0000-000000000005", "generation-b", ProfileGameMode.Pvp, "wrong");
        var incomingNew = Profile("00000000-0000-0000-0000-000000000006", "generation-c", ProfileGameMode.Pve, "new");
        await contexts.CreateAsync(new(local.Context, local.Name, local.Progress), CancellationToken.None);
        var codec = new JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(contexts, codec, new FixedTimeProvider());
        var json = codec.Write(new(1, DateTimeOffset.UnixEpoch, [incomingWrongGeneration, incomingNew]));

        var preview = await transfers.PreviewAsync(json, CancellationToken.None);
        var applied = await transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None);

        Assert.Contains(preview.Entries, entry => entry.Disposition == ProfileImportDisposition.QuarantinedWrongGeneration);
        Assert.Single(preview.ReadyEntries);
        Assert.Equal(2, applied.Profiles.Count);
        Assert.DoesNotContain(applied.Profiles, profile => profile.Progress.WishlistItemIds.Contains("wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ConfirmAsync(preview.ConfirmationId, CancellationToken.None));
    }

    [Fact]
    public void Transfer_rejects_unknown_members_tampered_checksum_and_integer_mode()
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, DateTimeOffset.UnixEpoch, [Profile("00000000-0000-0000-0000-000000000007", "generation", ProfileGameMode.Unknown, "item")]));

        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"checksum\":\"", "\"unexpected\":true,\"checksum\":\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"checksum\":\"", "\"checksum\":\"0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"mode\":\"unknown\"", "\"mode\":0", StringComparison.Ordinal)));
    }

    [Fact]
    public void Legacy_migration_preserves_progress_wishlist_events_overrides_and_pins_without_inventing_context()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000008");
        var legacy = new PlayerProfile(id, "Legacy", TarkovCompanion.Core.Common.GameMode.Regular, 24, Faction.Usec, null,
            new Dictionary<string, int> { ["trader"] = 2 }, new HashSet<string> { "task" }, new Dictionary<string, int> { ["objective"] = 3 },
            new Dictionary<string, int> { ["hideout"] = 1 }, new HashSet<string> { "wishlist" }, new Dictionary<string, int> { ["owned"] = 4 },
            new Dictionary<string, EventItemState> { ["event:item"] = EventItemState.Allergic }, new Dictionary<string, string> { ["override"] = "keep" }, DateTimeOffset.UnixEpoch, "legacy-a");
        var context = Context(id, "legacy-a", ProfileGameMode.Unknown);

        var migrated = LegacyProfileMigration.Migrate(legacy, context, [new ProfilePin("task", "task", 0, "pin")]);

        Assert.Equal(ProfileGameMode.Unknown, migrated.Context.Mode);
        Assert.Contains("wishlist", migrated.Progress.WishlistItemIds);
        Assert.Equal("Allergic", migrated.Progress.EventItemStates["event:item"]);
        Assert.Equal("keep", migrated.Progress.ItemOverrides["override"]);
        Assert.Single(migrated.Progress.Pins);
    }

    [Fact]
    public async Task Json_store_uses_compare_and_swap_and_keeps_utc_context_without_a_partial_switch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-v2-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "workspace.json");
            using var store = new JsonProfileWorkspaceStore(path);
            var profile = Profile("00000000-0000-0000-0000-000000000009", "generation", ProfileGameMode.Pve, "item");
            var revisionOne = new ProfileWorkspaceSnapshot(1, profile.Context.Identity.ProfileId, [profile]);

            Assert.True(await store.TryReplaceAsync(0, revisionOne, CancellationToken.None));
            Assert.False(await store.TryReplaceAsync(0, new ProfileWorkspaceSnapshot(1, null, [profile]), CancellationToken.None));
            var read = await store.ReadAsync(CancellationToken.None);

            Assert.Equal(1, read.Revision);
            Assert.Equal(profile.Context.Identity.ProfileId, read.ActiveProfileId);
            Assert.Equal(TimeSpan.Zero, read.ActiveProfile.UpdatedUtc.Offset);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_profile_state_is_read_or_changed()
    {
        var store = new MemoryStore();
        using var service = new ProfileContextService(store, new FixedTimeProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            new(Profile("00000000-0000-0000-0000-000000000010", "generation", ProfileGameMode.Pvp, "item").Context, "cancelled", new ProfileProgress(1)),
            cancellation.Token));
        Assert.Empty((await service.GetAsync(CancellationToken.None)).Profiles);
    }

    private static ProfileRecord Profile(string id, string generation, ProfileGameMode mode, string wishlist) => new(
        Context(Guid.Parse(id), generation, mode),
        $"profile-{generation}",
        new ProfileProgress(20, wishlistItemIds: new HashSet<string> { wishlist }),
        ProfileLifecycle.Active,
        DateTimeOffset.UnixEpoch);

    private static ProfileContext Context(Guid id, string generation, ProfileGameMode mode) => new(
        new ProfileIdentity(id, generation), mode, new WipeSeason($"wipe-{mode}"), new ProfileLocale("en-US", "EU", "Etc/UTC"), new DataSnapshotContext("snapshot-a", DateTimeOffset.UnixEpoch));

    private sealed class MemoryStore : IProfileWorkspaceStore
    {
        private ProfileWorkspaceSnapshot _value = new(0, null, []);
        public Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_value); }
        public Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_value.Revision != expectedRevision) return Task.FromResult(false);
            _value = replacement;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-14T00:00:00Z");
    }
}
