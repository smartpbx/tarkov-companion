using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.ReleaseExperience;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.ReleaseExperience;

public sealed class ReleaseExperienceViewModelTests
{
    private const string Version = "2.0.42";

    [Fact]
    public async Task A_first_install_records_a_baseline_without_claiming_an_update()
    {
        var store = new MemoryStore();
        using var viewModel = Build(store);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsBannerVisible);
        Assert.Equal(Version, Assert.Single(store.Saved).LastSeenVersion);
    }

    [Fact]
    public async Task An_update_opens_its_checked_in_list_and_dismisses_once_for_that_version()
    {
        var store = new MemoryStore(new ReleaseExperienceState("2.0.41", null));
        using var viewModel = Build(store);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsBannerVisible);
        Assert.Equal("Updated to 2.0.42", viewModel.BannerText);
        Assert.Equal(["See the route before the raid.", "Review the debrief afterwards."], viewModel.Changes);

        viewModel.OpenCommand.Execute(null);
        Assert.True(viewModel.IsListOpen);

        await Assert.IsType<AsyncDelegateCommand>(viewModel.DismissCommand).ExecuteAsync();

        Assert.False(viewModel.IsBannerVisible);
        Assert.False(viewModel.IsListOpen);
        Assert.Equal(Version, store.Saved[^1].DismissedVersion);
    }

    [Fact]
    public async Task Loading_and_active_raids_hide_it_then_the_menu_restores_it()
    {
        var runtime = new RuntimeStore(Snapshot(RaidLifecycleState.LoadingRaid));
        using var viewModel = Build(
            new MemoryStore(new ReleaseExperienceState("2.0.41", null)),
            runtime);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.IsBannerVisible);

        runtime.Set(Snapshot(RaidLifecycleState.InRaid));
        Assert.False(viewModel.IsBannerVisible);

        runtime.Set(Snapshot(RaidLifecycleState.Menu));
        Assert.True(viewModel.IsBannerVisible);
    }

    [Fact]
    public async Task A_dismissal_for_this_build_stays_hidden()
    {
        using var viewModel = Build(new MemoryStore(new ReleaseExperienceState(Version, Version)));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsBannerVisible);
    }

    private static ReleaseExperienceViewModel Build(MemoryStore store, RuntimeStore? runtime = null) => new(
        PlayerChangelog.Parse("""
            {
              "schemaVersion": 1,
              "releases": [{
                "version": "2.0.42",
                "changes": ["See the route before the raid.", "Review the debrief afterwards."]
              }]
            }
            """),
        store,
        runtime ?? new RuntimeStore(Snapshot(RaidLifecycleState.Menu)),
        new AppBuildIdentity(Version, null));

    private static ApplicationRuntimeSnapshot Snapshot(RaidLifecycleState state)
    {
        var snapshot = V2ShellTestData.Snapshot();
        return snapshot with
        {
            Raid = new RaidSnapshot(
                snapshot.Raid.RaidId,
                state,
                snapshot.Raid.MapId,
                snapshot.Raid.StartedUtc,
                snapshot.Raid.UpdatedUtc,
                Confidence.Unknown,
                null,
                [],
                false),
        };
    }

    private sealed class MemoryStore(ReleaseExperienceState? state = null) : IReleaseExperienceStateStore
    {
        public List<ReleaseExperienceState> Saved { get; } = [];

        public Task<ReleaseExperienceState?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task SaveAsync(ReleaseExperienceState value, CancellationToken cancellationToken)
        {
            state = value;
            Saved.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
        public event EventHandler? Changed;

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update) => Set(update(Current));

        public void Set(ApplicationRuntimeSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
