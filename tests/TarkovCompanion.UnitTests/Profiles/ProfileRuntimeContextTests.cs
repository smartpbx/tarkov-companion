using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

public sealed class ProfileRuntimeContextTests
{
    [Theory]
    [InlineData(ProfileGameMode.Pvp, GameMode.Regular)]
    [InlineData(ProfileGameMode.Pve, GameMode.Pve)]
    [InlineData(ProfileGameMode.Seasonal, GameMode.PvpSeason)]
    public async Task Active_profile_drives_catalog_mode_and_language_without_regular_or_english_defaults(
        ProfileGameMode profileMode,
        GameMode expectedCatalogMode)
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(
            Context(Id(201), "generation-a", profileMode, language: "pt-BR"),
            "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);

        var snapshot = await runtime.InitializeAsync(CancellationToken.None);

        Assert.True(snapshot.IsInitialized);
        Assert.Equal(ProfileRuntimeContextState.Ready, snapshot.State);
        Assert.Same(profile.Context, snapshot.ActiveProfile!.Context);
        Assert.NotNull(snapshot.CatalogScope);
        Assert.Equal(expectedCatalogMode, snapshot.CatalogScope.GameMode);
        Assert.Equal("pt", snapshot.CatalogScope.Language);
        Assert.Equal(profile.Context.Identity, snapshot.CatalogScope.Context.Identity);
        Assert.Equal(profile.Context.WipeSeason, snapshot.CatalogScope.Context.WipeSeason);
        Assert.Equal(profile.Context.DataSnapshot, snapshot.CatalogScope.Context.DataSnapshot);
        var request = snapshot.CatalogScope.ToSyncRequest(force: true);
        Assert.Equal(expectedCatalogMode, request.GameMode);
        Assert.Equal("pt", request.Language);
        Assert.True(request.Force);
    }

    [Fact]
    public async Task Unknown_mode_remains_an_actionable_invalid_context_and_cannot_create_a_sync_request()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        await profiles.CreateAsync(
            Request(Profile(202, "generation-unknown", ProfileGameMode.Unknown, "item")),
            CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);

        var snapshot = await runtime.InitializeAsync(CancellationToken.None);

        Assert.Equal(ProfileRuntimeContextState.UnknownGameMode, snapshot.State);
        Assert.Null(snapshot.CatalogScope);
        Assert.Equal("profile-context-mode-unknown", snapshot.Code);
        Assert.Contains("Choose PvP, PvE, or Seasonal", snapshot.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_workspace_is_distinct_from_an_uninitialized_context()
    {
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        using var runtime = new ProfileRuntimeContextService(profiles);

        Assert.False(runtime.Current.IsInitialized);
        Assert.Equal(ProfileRuntimeContextState.Uninitialized, runtime.Current.State);

        var initialized = await runtime.InitializeAsync(CancellationToken.None);

        Assert.True(initialized.IsInitialized);
        Assert.Equal(ProfileRuntimeContextState.NoActiveProfile, initialized.State);
        Assert.Equal("profile-context-not-selected", initialized.Code);
        Assert.Null(initialized.ActiveProfile);
        Assert.Null(initialized.CatalogScope);
        Assert.Equal(0, initialized.WorkspaceRevision);
    }

    [Fact]
    public async Task Switch_commits_first_then_publishes_one_snapshot_with_matching_profile_progress_and_catalog_scope()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var pvp = Profile(
            Context(Id(203), "generation-pvp", ProfileGameMode.Pvp, language: "en-US"),
            "pvp-item");
        var pve = Profile(
            Context(Id(204), "generation-pve", ProfileGameMode.Pve, language: "de-DE"),
            "pve-item");
        await profiles.CreateAsync(Request(pvp), CancellationToken.None);
        await profiles.CreateAsync(Request(pve, makeActive: false), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        var before = await runtime.InitializeAsync(CancellationToken.None);
        var published = new List<ProfileRuntimeContextSnapshot>();
        runtime.ContextChanged += change =>
        {
            Assert.Same(store.Current, change.Snapshot.Workspace);
            published.Add(change.Snapshot);
        };

        var switched = await runtime.SwitchAsync(pve.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Single(published);
        Assert.Same(switched, published[0]);
        Assert.Equal(before.WorkspaceRevision + 1, switched.WorkspaceRevision);
        Assert.Equal(before.Revision + 1, switched.Revision);
        Assert.Equal(pve.Context.Identity, switched.ActiveProfile!.Context.Identity);
        Assert.Equal(["pve-item"], switched.ActiveProfile.Progress.WishlistItemIds);
        Assert.Equal(GameMode.Pve, switched.CatalogScope!.GameMode);
        Assert.Equal("de", switched.CatalogScope.Language);
        Assert.Equal(pve.Context.Identity.ProfileId, store.Current.ActiveProfileId);
    }

    [Fact]
    public async Task Selecting_the_already_active_profile_does_not_publish_a_fake_runtime_revision()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(205, "generation", ProfileGameMode.Pvp, "item");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        var before = await runtime.InitializeAsync(CancellationToken.None);
        var published = 0;
        runtime.ContextChanged += _ => published++;

        var after = await runtime.SwitchAsync(profile.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Same(before, after);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Progress_update_is_bound_to_the_active_identity_generation_and_workspace_revision()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(206, "generation-a", ProfileGameMode.Pvp, "old-item");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        var before = await runtime.InitializeAsync(CancellationToken.None);
        var progress = new ProfileProgress(42, wishlistItemIds: new HashSet<string> { "new-item" });

        var updated = await runtime.UpdateActiveProgressAsync(before, progress, CancellationToken.None);

        Assert.Equal(before.WorkspaceRevision + 1, updated.WorkspaceRevision);
        Assert.Same(progress, updated.ActiveProfile!.Progress);
        Assert.Equal(["new-item"], store.Current.ActiveProfile.Progress.WishlistItemIds);
        Assert.Equal(profile.Context, updated.ActiveProfile.Context);
    }

    [Fact]
    public async Task A_progress_write_started_before_a_switch_is_rejected_without_touching_either_profile()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var first = Profile(207, "generation-first", ProfileGameMode.Pvp, "first-item");
        var second = Profile(208, "generation-second", ProfileGameMode.Pve, "second-item");
        await profiles.CreateAsync(Request(first), CancellationToken.None);
        await profiles.CreateAsync(Request(second, makeActive: false), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        var beforeSwitch = await runtime.InitializeAsync(CancellationToken.None);
        await runtime.SwitchAsync(second.Context.Identity.ProfileId, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ProfileWorkspaceRevisionConflictException>(() =>
            runtime.UpdateActiveProgressAsync(
                beforeSwitch,
                new ProfileProgress(99, wishlistItemIds: new HashSet<string> { "leaked-item" }),
                CancellationToken.None));

        Assert.Equal(beforeSwitch.WorkspaceRevision, error.ExpectedRevision);
        Assert.Equal(beforeSwitch.WorkspaceRevision + 1, error.ActualRevision);
        Assert.Equal(second.Context.Identity.ProfileId, store.Current.ActiveProfileId);
        Assert.Equal(["first-item"], store.Current.Profiles.Single(value => value.Context.Identity == first.Context.Identity).Progress.WishlistItemIds);
        Assert.Equal(["second-item"], store.Current.Profiles.Single(value => value.Context.Identity == second.Context.Identity).Progress.WishlistItemIds);
    }

    [Fact]
    public async Task Runtime_subscriber_failure_does_not_turn_a_committed_switch_into_a_failed_call()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var first = Profile(209, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(210, "generation-second", ProfileGameMode.Pve, "second");
        await profiles.CreateAsync(Request(first), CancellationToken.None);
        await profiles.CreateAsync(Request(second, makeActive: false), CancellationToken.None);
        var logger = new CapturingLogger<ProfileRuntimeContextService>();
        using var runtime = new ProfileRuntimeContextService(profiles, logger);
        await runtime.InitializeAsync(CancellationToken.None);
        var delivered = 0;
        runtime.ContextChanged += _ => throw new InvalidOperationException("subscriber failed");
        runtime.ContextChanged += _ => delivered++;

        var switched = await runtime.SwitchAsync(second.Context.Identity.ProfileId, CancellationToken.None);

        Assert.Equal(second.Context.Identity.ProfileId, switched.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Equal(1, delivered);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Error);
    }

    [Fact]
    public async Task Reentrant_switch_publication_stays_in_commit_order_for_every_subscriber()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var first = Profile(213, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(214, "generation-second", ProfileGameMode.Pve, "second");
        var third = Profile(215, "generation-third", ProfileGameMode.Seasonal, "third");
        await profiles.CreateAsync(Request(first), CancellationToken.None);
        await profiles.CreateAsync(Request(second, makeActive: false), CancellationToken.None);
        await profiles.CreateAsync(Request(third, makeActive: false), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        Task<ProfileRuntimeContextSnapshot>? reentrantSwitch = null;
        runtime.ContextChanged += change =>
        {
            if (change.Snapshot.ActiveProfile!.Context.Identity == second.Context.Identity)
            {
                reentrantSwitch = runtime.SwitchAsync(third.Context.Identity.ProfileId, CancellationToken.None);
            }
        };
        var observed = new List<ProfileIdentity>();
        runtime.ContextChanged += change => observed.Add(change.Snapshot.ActiveProfile!.Context.Identity);

        await runtime.SwitchAsync(second.Context.Identity.ProfileId, CancellationToken.None);
        Assert.NotNull(reentrantSwitch);
        await reentrantSwitch;

        Assert.Equal([second.Context.Identity, third.Context.Identity], observed);
        Assert.Equal(third.Context.Identity.ProfileId, store.Current.ActiveProfileId);
        Assert.Equal(store.Current.Revision, runtime.Current.WorkspaceRevision);
    }

    [Fact]
    public async Task Refresh_cannot_regress_a_newer_direct_profile_publication()
    {
        var store = new MemoryProfileStore();
        using var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var first = Profile(211, "generation-first", ProfileGameMode.Pvp, "first");
        var second = Profile(212, "generation-second", ProfileGameMode.Pve, "second");
        await profiles.CreateAsync(Request(first), CancellationToken.None);
        await profiles.CreateAsync(Request(second, makeActive: false), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);

        await profiles.SwitchAsync(second.Context.Identity.ProfileId, CancellationToken.None);
        var refreshed = await runtime.RefreshAsync(CancellationToken.None);

        Assert.Equal(second.Context.Identity, refreshed.ActiveProfile!.Context.Identity);
        Assert.Equal(store.Current.Revision, refreshed.WorkspaceRevision);
        Assert.Equal(GameMode.Pve, refreshed.CatalogScope!.GameMode);
    }
}
