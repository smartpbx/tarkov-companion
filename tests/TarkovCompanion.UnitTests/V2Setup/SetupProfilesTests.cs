using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.UnitTests.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// #269: a player can create, switch and archive profiles from Setup, and the app follows. The
/// composition test at the bottom is the one that fails if any of that wiring is taken out.
/// </summary>
public sealed class SetupProfilesTests
{
    [Fact]
    public async Task CreatingAProfileMakesItActiveInItsModeAndListsIt()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp));

        fixture.View.NewName = "PvE alt";
        fixture.View.NewWipe = "Wipe 3";
        fixture.View.SelectedMode = fixture.View.Modes.Single(mode => mode.Mode == ProfileGameMode.Pve);
        await ((AsyncDelegateCommand)fixture.View.CreateCommand).ExecuteAsync();

        Assert.Equal("PvE alt", fixture.View.ActiveTitle);
        Assert.Equal("PvE · Wipe 3", fixture.View.ActiveDetail);
        Assert.Equal("Game data: PvE, en", fixture.View.ScopeLine);
        Assert.Equal(2, fixture.View.Profiles.Count);
        Assert.Contains(fixture.View.Profiles, row => row.Name == "PvE alt" && row.IsActive);
        Assert.False(fixture.View.MessageIsError);
        Assert.Equal(string.Empty, fixture.View.NewName);
    }

    [Fact]
    public async Task ABlankNameIsRefusedWithAMessageAndCreatesNothing()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp));

        fixture.View.NewName = "   ";
        await ((AsyncDelegateCommand)fixture.View.CreateCommand).ExecuteAsync();

        Assert.True(fixture.View.MessageIsError);
        Assert.Single(fixture.View.Profiles);
    }

    [Fact]
    public async Task SwitchingChangesTheActiveProfileAndTheScopeLine()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Pve));
        var other = fixture.View.Profiles.Single(row => !row.IsActive);
        Assert.True(other.CanSwitch);

        other.SwitchCommand.Execute(other);
        await fixture.UntilAsync(() => fixture.View.ActiveTitle == other.Name);

        Assert.Equal(other.Name, fixture.View.ActiveTitle);
        Assert.Equal("Game data: PvE, en", fixture.View.ScopeLine);
        Assert.Single(fixture.View.Profiles, row => row.IsActive);
    }

    [Fact]
    public async Task ArchivingHidesAProfileUntilShownAndRestoreBringsItBack()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Pve));
        var other = fixture.View.Profiles.Single(row => !row.IsActive);
        Assert.True(other.CanArchive);

        other.ArchiveCommand.Execute(other);
        await fixture.UntilAsync(() => fixture.View.HasArchived);

        Assert.Single(fixture.View.Profiles);
        Assert.True(fixture.View.HasArchived);
        fixture.View.ShowArchived = true;
        var archived = fixture.View.Profiles.Single(row => row.IsArchived);
        Assert.True(archived.CanRestore);
        Assert.False(archived.CanSwitch);

        archived.RestoreCommand.Execute(archived);
        await fixture.UntilAsync(() => fixture.View.Profiles.All(row => !row.IsArchived));

        Assert.All(fixture.View.Profiles, row => Assert.False(row.IsArchived));
    }

    [Fact]
    public async Task TheActiveProfileCannotBeArchivedFromTheList()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Pve));

        var active = fixture.View.Profiles.Single(row => row.IsActive);

        Assert.False(active.CanArchive);
        Assert.False(active.CanSwitch);
    }

    [Fact]
    public async Task EditingAProfileChangesItsModeAndWipeLabel()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Pve));
        var other = fixture.View.Profiles.Single(row => !row.IsActive);
        Assert.False(other.IsEditing);

        other.BeginEditCommand.Execute(null);
        Assert.True(other.IsEditing);
        other.EditMode = other.Modes.Single(mode => mode.Mode == ProfileGameMode.Seasonal);
        other.EditWipe = "Wipe 9";
        other.SaveEditCommand.Execute(other);
        await fixture.UntilAsync(() => fixture.View.Profiles.Any(row => row.Wipe == "Wipe 9"));

        var updated = fixture.View.Profiles.Single(row => row.Wipe == "Wipe 9");
        Assert.Equal("Seasonal", updated.Mode);
        Assert.False(updated.IsEditing);
        Assert.False(fixture.View.MessageIsError);
    }

    [Fact]
    public async Task CancellingAnEditLeavesTheProfileUnchanged()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Pve));
        var other = fixture.View.Profiles.Single(row => !row.IsActive);
        var originalWipe = other.Wipe;

        other.BeginEditCommand.Execute(null);
        other.EditWipe = "Should not be saved";
        other.CancelEditCommand.Execute(null);

        Assert.False(other.IsEditing);
        Assert.Equal(originalWipe, other.Wipe);
        Assert.Single(fixture.View.Profiles, row => row.Wipe == originalWipe);
    }

    [Fact]
    public async Task AProfileWithNoModeIsSaidPlainlyAndNothingIsLoadedForIt()
    {
        using var fixture = await Fixture.CreateAsync((1, ProfileGameMode.Unknown));

        Assert.True(fixture.View.HasNotice);
        Assert.Contains("no game mode", fixture.View.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, fixture.View.ScopeLine);
    }

    [Fact]
    public async Task TheRealCompositionScopesProgressAndCatalogToTheProfileTheSetupPageSwitchedTo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-profiles-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();

            var profiles = Assert.IsType<ProfileScopedPlayerProfileService>(services.GetRequiredService<IPlayerProfileService>());
            var view = Assert.IsType<SetupProfilesViewModel>(shell.SetupWorkspace!.Profiles);
            var firstProfile = await profiles.GetActiveAsync(default);
            var staleRuntime = services.GetRequiredService<IRuntimeStateStore>().Current;

            view.NewName = "PvE alt";
            view.SelectedMode = view.Modes.Single(mode => mode.Mode == ProfileGameMode.Pve);
            await ((AsyncDelegateCommand)view.CreateCommand).ExecuteAsync();

            // Progress is the new profile's, in its own file, and the first profile's is untouched.
            var active = await profiles.GetActiveAsync(default);
            Assert.NotEqual(firstProfile.Id, active.Id);
            Assert.Equal("PvE alt", active.Name);
            Assert.Equal(GameMode.Pve, active.GameMode);
            Assert.True(File.Exists(profiles.PathFor(active.Id)));

            // The runtime context the catalog refresh reads is that profile's mode and language.
            var context = services.GetRequiredService<IProfileRuntimeContextService>().Current;
            Assert.Equal(ProfileRuntimeContextState.Ready, context.State);
            Assert.Equal(GameMode.Pve, context.CatalogScope!.GameMode);
            Assert.Equal(2, view.Profiles.Count);

            // An ordinary progress-file save does not publish another runtime snapshot. Setup's
            // summary follows the profile save itself so the level changes immediately.
            var summaryChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            legacy.Settings.PropertyChanged += OnSettingsChanged;
            try
            {
                await profiles.SaveAsync(active with { Level = 42 }, default);
                await summaryChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                legacy.Settings.PropertyChanged -= OnSettingsChanged;
            }

            // A catalog refresh started before the switch may still publish the old profile.
            // It must not replace the newer progress signal for the profile active now.
            legacy.Settings.Apply(staleRuntime);
            Assert.Contains("PvE alt · level 42", legacy.Settings.ProfileContext, StringComparison.Ordinal);

            void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(SettingsPageViewModel.ProfileContext) &&
                    legacy.Settings.ProfileContext.Contains("PvE alt · level 42", StringComparison.Ordinal))
                {
                    summaryChanged.TrySetResult();
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ProfileContextService _profiles;
        private readonly ProfileRuntimeContextService _runtime;

        private Fixture(ProfileContextService profiles, ProfileRuntimeContextService runtime, SetupProfilesViewModel view)
        {
            _profiles = profiles;
            _runtime = runtime;
            View = view;
        }

        public SetupProfilesViewModel View { get; }

        public static async Task<Fixture> CreateAsync(params (int Id, ProfileGameMode Mode)[] specs)
        {
            var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
            foreach (var (id, mode) in specs)
            {
                await profiles.CreateAsync(
                    Request(Profile(Context(Id(id), $"generation-{id}", mode, language: "en"), $"item-{id}"), makeActive: id == specs[0].Id),
                    default);
            }

            var runtime = new ProfileRuntimeContextService(profiles);
            await runtime.InitializeAsync(default);
            var view = new SetupProfilesViewModel(new ProfileManagementService(profiles, runtime, new ProfileClock(Now)));
            return new(profiles, runtime, view);
        }

        /// <summary>Row commands are fire-and-forget by design; wait for what they publish.</summary>
        public async Task UntilAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(condition(), "the profile list did not settle");
        }

        public void Dispose()
        {
            View.Dispose();
            _runtime.Dispose();
            _profiles.Dispose();
        }
    }
}
