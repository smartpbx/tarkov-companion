using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// Setup's "Reset this section", "Reset everything" and import preview (#292 task 2), against
/// real <see cref="WorkspacePreferenceService"/> and <see cref="IScreenshotRetentionStore"/>
/// instances rather than the pure diff/export logic <c>SetupSettingsExportTests</c> already covers.
/// </summary>
public sealed class SetupSettingsAdminViewModelTests
{
    [Fact]
    public async Task ResettingASectionWithNothingToResetSaysSoWithoutOpeningAPreview()
    {
        var (view, _, _) = Build();
        view.SetCurrentSection(V2SetupSection.Data);

        await ((AsyncDelegateCommand)view.ResetSectionCommand).ExecuteAsync();

        Assert.False(view.HasPendingChange);
        Assert.Equal("Nothing on this section can be reset.", view.StatusMessage);
    }

    [Fact]
    public async Task ResettingAnUnchangedSectionSaysAlreadyAtDefaults()
    {
        var (view, _, _) = Build();
        view.SetCurrentSection(V2SetupSection.Accessibility);

        await ((AsyncDelegateCommand)view.ResetSectionCommand).ExecuteAsync();

        Assert.False(view.HasPendingChange);
        Assert.Equal("This section is already at its defaults.", view.StatusMessage);
    }

    [Fact]
    public async Task ResettingAChangedSectionPreviewsBeforeApplying()
    {
        var (view, preferences, _) = Build();
        view.SetCurrentSection(V2SetupSection.Accessibility);
        await preferences.UpdateAsync(WorkspacePreferences.Default with { Theme = AppearanceTheme.Light }, CancellationToken.None);

        await ((AsyncDelegateCommand)view.ResetSectionCommand).ExecuteAsync();

        Assert.True(view.HasPendingChange);
        Assert.Contains(view.PendingDiff, entry => entry.Field == "Theme");
        // Nothing applied yet.
        Assert.Equal(AppearanceTheme.Light, preferences.Current.Theme);
    }

    [Fact]
    public async Task ConfirmingAppliesThePendingChangeAndClearsIt()
    {
        var (view, preferences, _) = Build();
        view.SetCurrentSection(V2SetupSection.Accessibility);
        await preferences.UpdateAsync(WorkspacePreferences.Default with { Theme = AppearanceTheme.Light }, CancellationToken.None);
        await ((AsyncDelegateCommand)view.ResetSectionCommand).ExecuteAsync();

        await ((AsyncDelegateCommand)view.ConfirmCommand).ExecuteAsync();

        Assert.False(view.HasPendingChange);
        Assert.Equal(AppearanceTheme.Dark, preferences.Current.Theme);
        Assert.Equal("This section's settings were reset.", view.StatusMessage);
    }

    [Fact]
    public async Task CancellingLeavesEverythingUntouched()
    {
        var (view, preferences, _) = Build();
        view.SetCurrentSection(V2SetupSection.Accessibility);
        await preferences.UpdateAsync(WorkspacePreferences.Default with { Theme = AppearanceTheme.Light }, CancellationToken.None);
        await ((AsyncDelegateCommand)view.ResetSectionCommand).ExecuteAsync();

        view.CancelCommand.Execute(null);

        Assert.False(view.HasPendingChange);
        Assert.Equal(AppearanceTheme.Light, preferences.Current.Theme);
    }

    [Fact]
    public async Task ResetEverythingAlsoResetsScreenshotRetention()
    {
        var (view, _, retention) = Build();
        await retention.SaveAsync(new ScreenshotRetentionSettings(true, 72), CancellationToken.None);

        await ((AsyncDelegateCommand)view.ResetAllCommand).ExecuteAsync();
        Assert.True(view.HasPendingChange);
        await ((AsyncDelegateCommand)view.ConfirmCommand).ExecuteAsync();

        Assert.Equal(ScreenshotRetentionSettings.Default, await retention.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExportThenPreviewImportOfTheSameFileHasNothingToApply()
    {
        var (view, _, _) = Build();
        using var directory = new TemporaryDirectory();
        view.ExchangePath = directory.File("preferences.json");

        await ((AsyncDelegateCommand)view.ExportCommand).ExecuteAsync();
        Assert.StartsWith("Exported to", view.StatusMessage, StringComparison.Ordinal);

        await ((AsyncDelegateCommand)view.PreviewImportCommand).ExecuteAsync();

        Assert.False(view.HasPendingChange);
        Assert.Equal("Already matches what is in force. Nothing to import.", view.StatusMessage);
    }

    [Fact]
    public async Task PreviewingAnImportOfAnInvalidFileFailsWithoutOpeningAPreview()
    {
        var (view, _, _) = Build();
        using var directory = new TemporaryDirectory();
        view.ExchangePath = directory.File("bad.json");
        await File.WriteAllTextAsync(view.ExchangePath, "{ not json");

        await ((AsyncDelegateCommand)view.PreviewImportCommand).ExecuteAsync();

        Assert.False(view.HasPendingChange);
        Assert.StartsWith("Not imported", view.StatusMessage, StringComparison.Ordinal);
    }

    private static (SetupSettingsAdminViewModel View, WorkspacePreferenceService Preferences, IScreenshotRetentionStore Retention) Build()
    {
        var preferences = new WorkspacePreferenceService(new InMemoryPreferenceStore());
        preferences.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        var retention = new InMemoryRetentionStore();
        return (new SetupSettingsAdminViewModel(preferences, retention), preferences, retention);
    }

    private sealed class InMemoryPreferenceStore : IWorkspacePreferenceStore
    {
        private WorkspacePreferences _stored = WorkspacePreferences.Default;

        public Task<WorkspacePreferences> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
        {
            _stored = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRetentionStore : IScreenshotRetentionStore
    {
        private ScreenshotRetentionSettings _stored = ScreenshotRetentionSettings.Default;

        public Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
        {
            _stored = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tc-settings-admin-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_root);

        public string File(string name) => Path.Combine(_root, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
