using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileContextV2Tests
{
    [Fact]
    public async Task Revision_conflicts_retry_without_merging_profile_state()
    {
        var store = new RetryOnceProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var published = new List<long>();
        service.ContextChanged += change => published.Add(change.Snapshot.Revision);
        var pvp = Profile("00000000-0000-0000-0000-000000000001", "pvp-a", ProfileGameMode.Pvp, "bolts");
        var pve = Profile("00000000-0000-0000-0000-000000000002", "pve-b", ProfileGameMode.Pve, "ledx");

        await service.CreateAsync(Request(pvp), CancellationToken.None);
        await service.CreateAsync(Request(pve), CancellationToken.None);
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
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var pvp = Profile("00000000-0000-0000-0000-000000000003", "pvp-a", ProfileGameMode.Pvp, "bolts");
        var seasonal = Profile("00000000-0000-0000-0000-000000000004", "season-b", ProfileGameMode.Seasonal, "ledx");
        await service.CreateAsync(Request(pvp), CancellationToken.None);
        await service.CreateAsync(Request(seasonal), CancellationToken.None);
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

    /// <summary>
    /// Phase 1 has no in-place rollover operation. A new wipe is a new profile with its own stable
    /// id, and the previous wipe stays a separate, archivable, unchanged profile; reusing the old
    /// id for the new generation is refused rather than silently replacing the old wipe's state.
    /// </summary>
    [Fact]
    public async Task New_wipe_is_a_separate_profile_and_the_previous_wipe_stays_immutable()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var previousWipe = Profile("00000000-0000-0000-0000-000000000011", "wipe-2026-a", ProfileGameMode.Pvp, "old-wipe", "wipe-2026");
        var nextWipe = Profile("00000000-0000-0000-0000-000000000012", "wipe-2027-a", ProfileGameMode.Pvp, "new-wipe", "wipe-2027");
        await service.CreateAsync(Request(previousWipe), CancellationToken.None);
        await service.CreateAsync(Request(nextWipe), CancellationToken.None);
        await service.SwitchAsync(previousWipe.Context.Identity.ProfileId, CancellationToken.None);

        var snapshot = await service.GetAsync(CancellationToken.None);
        var comparison = await service.CompareAsync(previousWipe.Context.Identity.ProfileId, nextWipe.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal("wipe-2026", snapshot.ActiveProfile.Context.WipeSeason.Value);
        Assert.Contains("old-wipe", snapshot.ActiveProfile.Progress.WishlistItemIds);
        Assert.DoesNotContain("new-wipe", snapshot.ActiveProfile.Progress.WishlistItemIds);
        Assert.False(comparison.SameWipeSeason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new(Context(previousWipe.Context.Identity.ProfileId, "wipe-2027-b", ProfileGameMode.Pvp, "wipe-2027"), "duplicate-id", new ProfileProgress(1)),
            CancellationToken.None));

        await service.SwitchAsync(nextWipe.Context.Identity.ProfileId, CancellationToken.None);
        var archived = await service.ArchiveAsync(previousWipe.Context.Identity.ProfileId, CancellationToken.None);
        var kept = archived.Profiles.Single(profile => profile.Context.Identity.ProfileId == previousWipe.Context.Identity.ProfileId);

        Assert.Equal(ProfileLifecycle.Archived, kept.Lifecycle);
        Assert.Equal(previousWipe.Context, kept.Context);
        Assert.Equal(["old-wipe"], kept.Progress.WishlistItemIds);
        Assert.Equal(nextWipe.Context.Identity.ProfileId, archived.ActiveProfileId);
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
    public void Contracts_copy_caller_collections_once_and_expose_them_read_only()
    {
        var mismatches = new List<ProfileContextMismatch> { ProfileContextMismatch.Mode };
        var compatibility = new ProfileContextCompatibility(mismatches);
        mismatches.Clear();

        Assert.False(compatibility.IsCompatible);
        Assert.Equal([ProfileContextMismatch.Mode], compatibility.Mismatches);
        Assert.Throws<NotSupportedException>(() => ((IList<ProfileContextMismatch>)compatibility.Mismatches).Clear());
        Assert.Throws<ArgumentException>(() => new ProfileContextCompatibility([(ProfileContextMismatch)99]));
        Assert.Throws<ArgumentException>(() => new ProfileContextCompatibility([ProfileContextMismatch.Mode, ProfileContextMismatch.Mode]));

        var wishlist = new HashSet<string> { "ledx" };
        var traders = new Dictionary<string, int> { ["prapor"] = 2 };
        var progress = new ProfileProgress(10, traderLevels: traders, wishlistItemIds: wishlist);
        wishlist.Add("bitcoin");
        traders["prapor"] = 4;

        Assert.Equal(["ledx"], progress.WishlistItemIds);
        Assert.Equal(2, progress.TraderLevels["prapor"]);

        var first = Profile(20, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(21, "generation-second", ProfileGameMode.Pve, "second");
        var third = Profile(22, "generation-third", ProfileGameMode.Pve, "third");
        var profiles = new List<ProfileRecord> { first };
        var snapshot = new ProfileWorkspaceSnapshot(1, null, profiles);
        profiles.Add(second);

        Assert.Same(first, Assert.Single(snapshot.Profiles));
        Assert.Throws<NotSupportedException>(() => ((IList<ProfileRecord>)snapshot.Profiles).Add(second));

        // Count and each entry are observed once; later answers from the same list are not kept.
        var shifting = new ShiftingProfileList([first], [second, third]);
        var frozen = new ProfileWorkspaceSnapshot(1, null, shifting);

        Assert.Same(first, Assert.Single(frozen.Profiles));
        Assert.Equal(2, shifting.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileWorkspaceSnapshot(1, null, new NegativeCountProfileList()));
    }

    [Fact]
    public async Task Service_generated_timestamps_are_utc_even_when_the_clock_carries_an_offset()
    {
        var instant = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var clock = new ProfileClock(instant.ToOffset(TimeSpan.FromHours(3)));
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, clock);
        var active = Profile(30, "generation-active", ProfileGameMode.Pvp, "active");
        var other = Profile(31, "generation-other", ProfileGameMode.Pve, "other");

        var created = await service.CreateAsync(Request(active), CancellationToken.None);
        await service.CreateAsync(Request(other, makeActive: false), CancellationToken.None);
        clock.Set(instant.AddHours(1).ToOffset(TimeSpan.FromHours(-5)));
        var archived = await service.ArchiveAsync(other.Context.Identity.ProfileId, CancellationToken.None);
        clock.Set(instant.AddHours(2).ToOffset(TimeSpan.FromHours(9)));
        var restored = await service.RestoreAsync(other.Context.Identity.ProfileId, CancellationToken.None);

        AssertUtc(instant, Assert.Single(created.Profiles).UpdatedUtc);
        AssertUtc(instant.AddHours(1), archived.Profiles.Single(profile => profile.Context.Identity == other.Context.Identity).UpdatedUtc);
        AssertUtc(instant.AddHours(2), restored.Profiles.Single(profile => profile.Context.Identity == other.Context.Identity).UpdatedUtc);
        AssertUtc(instant, restored.Profiles.Single(profile => profile.Context.Identity == active.Context.Identity).UpdatedUtc);

        static void AssertUtc(DateTimeOffset expected, DateTimeOffset actual)
        {
            Assert.Equal(TimeSpan.Zero, actual.Offset);
            Assert.Equal(expected.UtcDateTime, actual.UtcDateTime);
        }
    }

    [Fact]
    public async Task Throwing_subscriber_cannot_fail_a_committed_revision_or_hide_it_from_other_subscribers()
    {
        var store = new MemoryProfileStore();
        var logger = new CapturingLogger<ProfileContextService>();
        using var service = new ProfileContextService(store, new ProfileClock(Now), logger);
        var delivered = new List<long>();
        service.ContextChanged += _ => throw new InvalidOperationException("presentation failed");
        service.ContextChanged += change => delivered.Add(change.Snapshot.Revision);
        var first = Profile(40, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(41, "generation-second", ProfileGameMode.Pve, "second");

        var created = await service.CreateAsync(Request(first), CancellationToken.None);
        var next = await service.CreateAsync(Request(second), CancellationToken.None);

        Assert.Equal(1, created.Revision);
        Assert.Equal(2, next.Revision);
        Assert.Same(next, store.Current);
        Assert.Equal([1L, 2L], delivered);
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.IsType<InvalidOperationException>(entry.Error);
        });
    }

    [Fact]
    public async Task Reentrant_subscriber_runs_after_the_gate_is_released_and_sees_revisions_in_commit_order()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var first = Profile(50, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(51, "generation-second", ProfileGameMode.Pve, "second");
        await service.CreateAsync(Request(first), CancellationToken.None);
        await service.CreateAsync(Request(second, makeActive: false), CancellationToken.None);

        var observed = new List<(long Revision, Guid? Active)>();
        Task<ProfileWorkspaceSnapshot>? nested = null;
        var nestedCompletedInsideHandler = false;
        service.ContextChanged += change =>
        {
            observed.Add((change.Snapshot.Revision, change.Snapshot.ActiveProfileId));
            if (nested is null)
            {
                // With the gate still held this call could not finish before the handler returns.
                nested = service.SwitchAsync(first.Context.Identity.ProfileId, CancellationToken.None);
                nestedCompletedInsideHandler = nested.IsCompleted;
            }
        };

        var outer = await service.SwitchAsync(second.Context.Identity.ProfileId, CancellationToken.None);

        Assert.NotNull(nested);
        var inner = await nested;
        Assert.True(nestedCompletedInsideHandler);
        Assert.Equal(3, outer.Revision);
        Assert.Equal(4, inner.Revision);
        Assert.Equal(
            [(3L, (Guid?)second.Context.Identity.ProfileId), (4L, (Guid?)first.Context.Identity.ProfileId)],
            observed);
        Assert.Equal(first.Context.Identity.ProfileId, (await service.GetAsync(CancellationToken.None)).ActiveProfileId);
    }

    [Fact]
    public async Task Rapid_concurrent_switching_commits_each_revision_once_in_order_without_leaking_progress()
    {
        var store = new MemoryProfileStore { YieldOnAccess = true };
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var profiles = Enumerable.Range(0, 8)
            .Select(index => Profile(60 + index, $"generation-{index}", index % 2 == 0 ? ProfileGameMode.Pvp : ProfileGameMode.Pve, $"wishlist-{index}"))
            .ToArray();
        foreach (var profile in profiles)
        {
            await service.CreateAsync(Request(profile), CancellationToken.None);
        }

        var wishlistById = profiles.ToDictionary(profile => profile.Context.Identity.ProfileId, profile => profile.Progress.WishlistItemIds.Single());
        var baseline = (await service.GetAsync(CancellationToken.None)).Revision;
        var published = new ConcurrentQueue<ProfileWorkspaceSnapshot>();
        service.ContextChanged += change => published.Enqueue(change.Snapshot);

        var requests = Enumerable.Range(0, 240).Select(index => profiles[(index * 3) % profiles.Length].Context.Identity.ProfileId).ToArray();
        var results = await Task.WhenAll(requests.Select(profileId => Task.Run(() => service.SwitchAsync(profileId, CancellationToken.None))));
        var final = await service.GetAsync(CancellationToken.None);

        Assert.Same(final, store.Current);
        Assert.True(final.Revision > baseline);
        for (var index = 0; index < requests.Length; index++)
        {
            Assert.Equal(requests[index], results[index].ActiveProfileId);
        }

        // Every committed revision is published exactly once and in commit order, and each carries
        // only the progress of the profile it made active.
        var expectedRevisions = Enumerable.Range(1, checked((int)(final.Revision - baseline))).Select(offset => baseline + offset).ToArray();
        Assert.Equal(expectedRevisions, published.Select(snapshot => snapshot.Revision));
        Assert.Equal(expectedRevisions, results.Select(result => result.Revision).Where(revision => revision > baseline).Distinct().Order());
        Assert.All(published, snapshot =>
        {
            Assert.Equal(profiles.Length, snapshot.Profiles.Count);
            Assert.Equal([wishlistById[snapshot.ActiveProfile.Context.Identity.ProfileId]], snapshot.ActiveProfile.Progress.WishlistItemIds);
        });
        Assert.Equal(final.ActiveProfileId, published.Last().ActiveProfileId);
    }

    [Fact]
    public async Task Switching_to_the_active_profile_is_not_a_new_revision()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(70, "generation", ProfileGameMode.Pvp, "item");
        var created = await service.CreateAsync(Request(profile), CancellationToken.None);
        var published = 0;
        service.ContextChanged += _ => published++;

        var switched = await service.SwitchAsync(profile.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Same(created, switched);
        Assert.Equal(0, published);
        Assert.Equal(1, store.ReplaceAttempts);
    }

    [Fact]
    public async Task Workspace_maximum_is_enforced_by_create_and_import_so_accepted_state_stays_exportable()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var profiles = Enumerable.Range(0, ProfileWorkspaceSnapshot.MaximumProfiles)
            .Select(index => Profile(1_000 + index, $"generation-{index}", ProfileGameMode.Pvp, $"item-{index}"))
            .ToArray();
        foreach (var profile in profiles)
        {
            await service.CreateAsync(Request(profile, makeActive: false), CancellationToken.None);
        }

        var overflow = Profile(2_000, "generation-overflow", ProfileGameMode.Pvp, "overflow");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Request(overflow), CancellationToken.None));
        var full = await service.GetAsync(CancellationToken.None);
        Assert.Equal(ProfileWorkspaceSnapshot.MaximumProfiles, full.Profiles.Count);
        Assert.Equal(ProfileWorkspaceSnapshot.MaximumProfiles, full.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync([overflow], CancellationToken.None));
        Assert.Same(full, store.Current);

        var codec = new TarkovCompanion.Infrastructure.Profile.JsonProfileContextTransferCodec();
        var transfers = new ProfileTransferService(service, codec, new ProfileClock(Now));
        var exported = codec.Read(await transfers.ExportAsync(CancellationToken.None));
        Assert.Equal(full.Profiles.Select(profile => profile.Context.Identity), exported.Profiles.Select(profile => profile.Context.Identity));

        var otherStore = new MemoryProfileStore();
        using var other = new ProfileContextService(otherStore, new ProfileClock(Now));
        await other.CreateAsync(Request(overflow), CancellationToken.None);
        var before = otherStore.Current;
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ImportAsync(exported.Profiles, CancellationToken.None));
        Assert.Same(before, otherStore.Current);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileWorkspaceSnapshot(0, null, [.. profiles, overflow]));
    }

    [Fact]
    public async Task Service_rejects_null_dependencies_arguments_and_store_output()
    {
        using var service = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var profile = Profile(80, "generation", ProfileGameMode.Pvp, "item");

        Assert.Throws<ArgumentNullException>(() => new ProfileContextService(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateAsync(new(null!, "name", new ProfileProgress(1)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateAsync(new(profile.Context, "name", null!), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ImportAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync([profile, null!], CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new ProfileContextCompatibility(null!));
        Assert.Throws<ArgumentNullException>(() => ProfileContextCompatibility.Compare(null!, profile.Context));
        Assert.Throws<ArgumentNullException>(() => ProfileContextCompatibility.Compare(profile.Context, null!));
        Assert.Throws<ArgumentException>(() => new ProfileWorkspaceSnapshot(0, null, [profile, null!]));

        using var broken = new ProfileContextService(new NullSnapshotStore(), new ProfileClock(Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.CreateAsync(Request(profile), CancellationToken.None));
    }

    [Fact]
    public async Task Import_uses_one_copy_of_the_callers_list_and_an_empty_import_is_not_a_revision()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        var reviewed = Profile(90, "generation-reviewed", ProfileGameMode.Pvp, "reviewed");
        var swapped = Profile(91, "generation-swapped", ProfileGameMode.Pve, "swapped");
        var published = new List<long>();
        service.ContextChanged += change => published.Add(change.Snapshot.Revision);

        var empty = await service.ImportAsync([], CancellationToken.None);
        Assert.Equal(0, empty.Revision);
        Assert.Equal(0, store.ReplaceAttempts);
        Assert.Empty(published);

        var imported = await service.ImportAsync(new ShiftingProfileList([reviewed], [swapped, swapped]), CancellationToken.None);

        Assert.Equal(1, imported.Revision);
        Assert.Same(reviewed, Assert.Single(imported.Profiles));
        Assert.Equal([1L], published);
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
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            new(Profile("00000000-0000-0000-0000-000000000010", "generation", ProfileGameMode.Pvp, "item").Context, "cancelled", new ProfileProgress(1)),
            cancellation.Token));
        Assert.Empty((await service.GetAsync(CancellationToken.None)).Profiles);
    }
}
