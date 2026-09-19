using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.Personalization;

/// <summary>
/// The appearance record, the file it lives in, and the resource values it means (#266, #315).
/// </summary>
public sealed class WorkspacePreferenceTests
{
    [Fact]
    public void DefaultIsTodaysApplication()
    {
        var defaults = WorkspacePreferences.Default;

        Assert.Equal(AppearanceTheme.Dark, defaults.Theme);
        Assert.Equal(ColorVisionMode.Standard, defaults.ColorVision);
        Assert.Equal(100, defaults.TextScalePercent);
        Assert.Equal(InterfaceDensity.Standard, defaults.Density);
        Assert.False(defaults.ReduceMotion);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-40, 100)]
    [InlineData(112, 100)]
    [InlineData(140, 150)]
    [InlineData(900, 200)]
    public void AHandEditedTextScaleSnapsToAnOfferedOne(int stored, int expected) =>
        Assert.Equal(expected, new WorkspacePreferences(TextScalePercent: stored).Normalized().TextScalePercent);

    [Fact]
    public void SteppingStopsAtTheEndsRatherThanWrapping()
    {
        Assert.Equal(100, WorkspacePreferences.StepTextScale(100, -1));
        Assert.Equal(125, WorkspacePreferences.StepTextScale(100, 1));
        Assert.Equal(200, WorkspacePreferences.StepTextScale(200, 1));
    }

    [Fact]
    public void AnUnknownEnumValueFallsBackRatherThanPaintingNothing()
    {
        var wild = new WorkspacePreferences((AppearanceTheme)77, (ColorVisionMode)9, 100, (InterfaceDensity)5)
            .Normalized();

        Assert.Equal(AppearanceTheme.Dark, wild.Theme);
        Assert.Equal(ColorVisionMode.Standard, wild.ColorVision);
        Assert.Equal(InterfaceDensity.Standard, wild.Density);
    }

    [Fact]
    public async Task TheStoredRecordSurvivesARestart()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonFileWorkspacePreferenceStore(directory.File("preferences.json"));
        var wanted = new WorkspacePreferences(
            AppearanceTheme.HighContrast,
            ColorVisionMode.BlueYellowSafe,
            175,
            InterfaceDensity.Comfortable,
            ReduceMotion: true);

        await store.SaveAsync(wanted, CancellationToken.None);
        var reopened = new JsonFileWorkspacePreferenceStore(directory.File("preferences.json"));

        Assert.Equal(wanted, await reopened.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheFileNamesItsChoicesRatherThanNumberingThem()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("preferences.json");

        await new JsonFileWorkspacePreferenceStore(path).SaveAsync(
            new WorkspacePreferences(AppearanceTheme.Light, TextScalePercent: 125),
            CancellationToken.None);

        var written = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains("\"theme\": \"Light\"", written, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\": 99, \"theme\": \"Light\"}")]
    public async Task AFileThisBuildCannotUnderstandOpensAtTheDefault(string contents)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("preferences.json");
        await File.WriteAllTextAsync(path, contents, CancellationToken.None);

        Assert.Equal(
            WorkspacePreferences.Default,
            await new JsonFileWorkspacePreferenceStore(path).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFileWithOneKeyInItKeepsTheDefaultsForTheRest()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("preferences.json");
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\": 1, \"textScalePercent\": 150}",
            CancellationToken.None);

        var stored = await new JsonFileWorkspacePreferenceStore(path).GetAsync(CancellationToken.None);

        Assert.Equal(150, stored.TextScalePercent);
        Assert.Equal(AppearanceTheme.Dark, stored.Theme);
    }

    [Fact]
    public async Task AChangeIsAnnouncedBeforeItIsWritten()
    {
        var store = new RecordingStore();
        var service = new WorkspacePreferenceService(store);
        var announced = new List<WorkspacePreferences>();
        service.Changed += (_, preferences) => announced.Add(preferences);

        await service.LoadAsync(CancellationToken.None);
        await service.UpdateAsync(
            current => current with { Theme = AppearanceTheme.Light },
            CancellationToken.None);

        Assert.Equal([AppearanceTheme.Dark, AppearanceTheme.Light], announced.Select(p => p.Theme));
        Assert.Equal(AppearanceTheme.Light, service.Current.Theme);
        Assert.Equal(AppearanceTheme.Light, Assert.Single(store.Saved).Theme);
    }

    [Fact]
    public async Task SettingWhatIsAlreadyInForceWritesNothing()
    {
        var store = new RecordingStore();
        var service = new WorkspacePreferenceService(store);
        await service.LoadAsync(CancellationToken.None);

        await service.UpdateAsync(WorkspacePreferences.Default, CancellationToken.None);

        Assert.Empty(store.Saved);
    }

    [Fact]
    public void TwoHundredPercentDoublesTheTypeRamp()
    {
        var baseline = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["V2.Type.Body.Size"] = 16d,
            ["V2.Type.Body.LineHeight"] = 24d,
            ["Instrument.Type.Caption.Size"] = 11d,
        };

        var overrides = V2AppearanceResources.Overrides(
            new WorkspacePreferences(TextScalePercent: 200),
            baseline);

        Assert.Equal(32d, overrides["V2.Type.Body.Size"]);
        Assert.Equal(48d, overrides["V2.Type.Body.LineHeight"]);
        Assert.Equal(22d, overrides["Instrument.Type.Caption.Size"]);
    }

    [Fact]
    public void AScaledSizeIsAWholeNumberOfPixels()
    {
        var overrides = V2AppearanceResources.Overrides(
            new WorkspacePreferences(TextScalePercent: 125),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["V2.Type.Label.Size"] = 14d });

        // 14 * 1.25 is 17.5; a half pixel lands the ramp's steps on different device pixels.
        Assert.Equal(18d, overrides["V2.Type.Label.Size"]);
    }

    [Fact]
    public void DensityCopiesTheChosenGapOverTheOneEveryStyleAsksFor()
    {
        var baseline = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["V2.Density.Compact.Gap"] = 8d,
            ["V2.Density.Standard.Gap"] = 12d,
            ["V2.Density.Comfortable.Gap"] = 16d,
        };

        Assert.Equal(
            16d,
            V2AppearanceResources.Overrides(
                new WorkspacePreferences(Density: InterfaceDensity.Comfortable),
                baseline)["V2.Density.Standard.Gap"]);
        Assert.Equal(
            8d,
            V2AppearanceResources.Overrides(
                new WorkspacePreferences(Density: InterfaceDensity.Compact),
                baseline)["V2.Density.Standard.Gap"]);
    }

    [Fact]
    public void ReducedMotionResolvesToTheZeroDuration()
    {
        var baseline = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["V2.Motion.Full.Duration"] = TimeSpan.FromMilliseconds(160),
            ["V2.Motion.Reduced.Duration"] = TimeSpan.Zero,
        };

        Assert.Equal(
            TimeSpan.Zero,
            V2AppearanceResources.Overrides(new WorkspacePreferences(ReduceMotion: true), baseline)["V2.Motion.Effective.Duration"]);
        Assert.Equal(
            TimeSpan.FromMilliseconds(160),
            V2AppearanceResources.Overrides(new WorkspacePreferences(), baseline)["V2.Motion.Effective.Duration"]);
    }

    private sealed class RecordingStore : IWorkspacePreferenceStore
    {
        public List<WorkspacePreferences> Saved { get; } = [];

        public Task<WorkspacePreferences> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WorkspacePreferences.Default);

        public Task SaveAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
        {
            Saved.Add(preferences);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tc-preferences-{Guid.NewGuid():N}");

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
