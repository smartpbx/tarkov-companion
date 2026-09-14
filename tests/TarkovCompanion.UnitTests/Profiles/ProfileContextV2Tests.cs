using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileContextV2Tests
{
    [Fact]
    public async Task Revision_conflicts_retry_without_merging_profile_state()
    {
        var store = new RetryOnceMemoryStore();
        using var service = new ProfileContextService(store, new FixedTimeProvider());
        var published = new List<long>();
        service.ContextChanged += change => published.Add(change.Snapshot.Revision);
        var pvp = Profile("00000000-0000-0000-0000-000000000001", "pvp-a", ProfileGameMode.Pvp, "bolts");
        var pve = Profile("00000000-0000-0000-0000-000000000002", "pve-b", ProfileGameMode.Pve, "ledx");

        await service.CreateAsync(new(pvp.Context, pvp.Name, pvp.Progress), CancellationToken.None);
        await service.CreateAsync(new(pve.Context, pve.Name, pve.Progress), CancellationToken.None);
        var switched = await service.SwitchAsync(pvp.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal(3, switched.Revision);
        Assert.Equal(4, store.ReplaceAttempts);
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
    public async Task Wipe_rollover_requires_a_new_identity_and_keeps_the_previous_context_immutable()
    {
        var store = new MemoryStore();
        using var service = new ProfileContextService(store, new FixedTimeProvider());
        var beforeRollover = Profile("00000000-0000-0000-0000-000000000011", "wipe-2026-a", ProfileGameMode.Pvp, "old-wipe", "wipe-2026");
        var afterRollover = Profile("00000000-0000-0000-0000-000000000012", "wipe-2027-a", ProfileGameMode.Pvp, "new-wipe", "wipe-2027");
        await service.CreateAsync(new(beforeRollover.Context, beforeRollover.Name, beforeRollover.Progress), CancellationToken.None);
        await service.CreateAsync(new(afterRollover.Context, afterRollover.Name, afterRollover.Progress), CancellationToken.None);
        await service.SwitchAsync(beforeRollover.Context.Identity.ProfileId, CancellationToken.None);

        var snapshot = await service.GetAsync(CancellationToken.None);
        var comparison = await service.CompareAsync(beforeRollover.Context.Identity.ProfileId, afterRollover.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal("wipe-2026", snapshot.ActiveProfile.Context.WipeSeason.Value);
        Assert.Contains("old-wipe", snapshot.ActiveProfile.Progress.WishlistItemIds);
        Assert.DoesNotContain("new-wipe", snapshot.ActiveProfile.Progress.WishlistItemIds);
        Assert.False(comparison.SameWipeSeason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new(Context(beforeRollover.Context.Identity.ProfileId, "wipe-2027-b", ProfileGameMode.Pvp, "wipe-2027"), "duplicate-id", new ProfileProgress(1)),
            CancellationToken.None));
    }

    [Fact]
    public void Locale_timezone_and_utc_snapshot_are_preserved_without_current_culture_or_offset_assumptions()
    {
        var locale = new ProfileLocale("pt-BR", "br", "America/Sao_Paulo");
        var snapshot = new DataSnapshotContext("catalog-2026-09", new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.FromHours(-3)));
        var context = new ProfileContext(new ProfileIdentity(Guid.Parse("00000000-0000-0000-0000-000000000013"), "generation"), ProfileGameMode.Pve, new WipeSeason("wipe-2026"), locale, snapshot);

        Assert.Equal("pt-BR", context.Locale.Language);
        Assert.Equal("BR", context.Locale.Region);
        Assert.Equal("America/Sao_Paulo", context.Locale.TimeZone);
        Assert.Equal(TimeSpan.Zero, context.DataSnapshot.PublishedUtc.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T13:30:00Z"), context.DataSnapshot.PublishedUtc);
    }

    [Fact]
    public void Context_contract_signals_exact_team_model_and_history_incompatibilities_without_merging_them()
    {
        var expected = Context(Guid.Parse("00000000-0000-0000-0000-000000000014"), "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-a");
        var incompatible = Context(Guid.Parse("00000000-0000-0000-0000-000000000015"), "generation-b", ProfileGameMode.Pve, "wipe-b", "de-DE", "DE", "Europe/Berlin", "catalog-b");

        var compatibility = ProfileContextCompatibility.Compare(expected, incompatible);

        Assert.False(compatibility.IsCompatible);
        Assert.Equal(
        [
            ProfileContextMismatch.ProfileId,
            ProfileContextMismatch.Generation,
            ProfileContextMismatch.Mode,
            ProfileContextMismatch.WipeSeason,
            ProfileContextMismatch.Locale,
            ProfileContextMismatch.DataSnapshot,
        ], compatibility.Mismatches);
        Assert.True(ProfileContextCompatibility.Compare(expected, Context(expected.Identity.ProfileId, "generation-a", ProfileGameMode.Pvp, "wipe-a", "en-US", "US", "America/New_York", "catalog-a")).IsCompatible);
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
    public void Transfer_rejects_hostile_envelopes_before_they_can_become_a_preview()
    {
        var codec = new JsonProfileContextTransferCodec();
        var json = codec.Write(new(1, DateTimeOffset.UnixEpoch, [Profile("00000000-0000-0000-0000-000000000007", "generation", ProfileGameMode.Unknown, "item")]));

        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"checksum\":\"", "\"unexpected\":true,\"checksum\":\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"checksum\":\"", "\"checksum\":\"0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read(json.Replace("\"mode\":\"unknown\"", "\"mode\":0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => codec.Read("{\"formatId\":\"tarkov-companion.profile-context\"}"));
        Assert.Throws<InvalidDataException>(() => codec.Read(new string('x', 2_097_153)));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.Write(new(1, DateTimeOffset.UnixEpoch, Enumerable.Range(0, 65)
            .Select(index => Profile($"00000000-0000-0000-0000-{index + 100:D12}", $"generation-{index}", ProfileGameMode.Unknown, $"item-{index}")).ToArray())));
    }

    [Fact]
    public async Task Transfer_service_rechecks_the_external_codec_contract_before_previewing_or_mutating_context()
    {
        var store = new MemoryStore();
        using var contexts = new ProfileContextService(store, new FixedTimeProvider());
        var hostileCodec = new StaticCodec(new ProfileTransferDocument(2, DateTimeOffset.UnixEpoch, []));
        var transfers = new ProfileTransferService(contexts, hostileCodec, new FixedTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => transfers.PreviewAsync("external-document", CancellationToken.None));

        Assert.Empty((await contexts.GetAsync(CancellationToken.None)).Profiles);
    }

    [Fact]
    public void Legacy_migration_is_a_deterministic_pure_projection_that_preserves_supported_state_without_inventing_context()
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
        Assert.Equal(["task"], migrated.Progress.CompletedTaskIds);
        Assert.Equal(DateTimeOffset.UnixEpoch, migrated.UpdatedUtc);
        Assert.Throws<ArgumentException>(() => LegacyProfileMigration.Migrate(legacy, Context(id, "another-generation", ProfileGameMode.Unknown)));
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

    private static ProfileRecord Profile(string id, string generation, ProfileGameMode mode, string wishlist, string? wipe = null) => new(
        Context(Guid.Parse(id), generation, mode, wipe),
        $"profile-{generation}",
        new ProfileProgress(20, wishlistItemIds: new HashSet<string> { wishlist }),
        ProfileLifecycle.Active,
        DateTimeOffset.UnixEpoch);

    private static ProfileContext Context(
        Guid id,
        string generation,
        ProfileGameMode mode,
        string? wipe = null,
        string language = "en-US",
        string region = "EU",
        string timeZone = "Etc/UTC",
        string snapshot = "snapshot-a") => new(
        new ProfileIdentity(id, generation), mode, new WipeSeason(wipe ?? $"wipe-{mode}"), new ProfileLocale(language, region, timeZone), new DataSnapshotContext(snapshot, DateTimeOffset.UnixEpoch));

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

    private sealed class RetryOnceMemoryStore : IProfileWorkspaceStore
    {
        private readonly MemoryStore _inner = new();
        private bool _conflictInjected;

        public int ReplaceAttempts { get; private set; }

        public Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);

        public Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
        {
            ReplaceAttempts++;
            if (!_conflictInjected)
            {
                _conflictInjected = true;
                return Task.FromResult(false);
            }

            return _inner.TryReplaceAsync(expectedRevision, replacement, cancellationToken);
        }
    }

    private sealed class StaticCodec(ProfileTransferDocument result) : IProfileTransferCodec
    {
        public string Write(ProfileTransferDocument document) => throw new NotSupportedException();

        public ProfileTransferDocument Read(string document) => result;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-14T00:00:00Z");
    }
}
