using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// Setup's "Reset this section", "Reset everything", export and import (#292 task 2): the diff
/// that drives every preview, and the export/validate logic behind a real file on disk.
/// </summary>
public sealed class SetupSettingsExportTests
{
    [Fact]
    public void IdenticalSnapshotsHaveNoDifferences() =>
        Assert.Empty(SetupSettingsDiff.Compare(SetupSettingsSnapshot.Default, SetupSettingsSnapshot.Default));

    [Fact]
    public void OnlyTheFieldsThatChangedAppearInTheDiff()
    {
        var current = SetupSettingsSnapshot.Default;
        var incoming = current with
        {
            Appearance = current.Appearance with { Theme = AppearanceTheme.Light, TextScalePercent = 150 },
        };

        var diff = SetupSettingsDiff.Compare(current, incoming).Select(SetupSettingsDiffRow.From).ToArray();

        Assert.Equal(2, diff.Length);
        Assert.Contains(diff, entry => entry.Field == "Theme" && entry.CurrentValue == "Dark" && entry.NewValue == "Light");
        Assert.Contains(diff, entry => entry.Field == "Text size" && entry.NewValue == "150");
    }

    [Fact]
    public void BooleansReadAsOnOrOffRatherThanTrueOrFalse()
    {
        var current = SetupSettingsSnapshot.Default;
        var incoming = current with { Notifications = current.Notifications with { SquadMark = false } };

        var entry = SetupSettingsDiffRow.From(Assert.Single(SetupSettingsDiff.Compare(current, incoming)));

        Assert.Equal("Squadmate marks", entry.Field);
        Assert.Equal("On", entry.CurrentValue);
        Assert.Equal("Off", entry.NewValue);
    }

    [Fact]
    public void AnEnumReadsAsWordsNotPascalCase()
    {
        var current = SetupSettingsSnapshot.Default;
        var incoming = current with
        {
            Appearance = current.Appearance with { Theme = AppearanceTheme.HighContrast },
        };

        var entry = SetupSettingsDiffRow.From(Assert.Single(SetupSettingsDiff.Compare(current, incoming)));

        Assert.Equal("High Contrast", entry.NewValue);
    }

    [Fact]
    public void TheDiffComparesNormalizedValuesSoAnAlreadyClampedFieldNeverAppears()
    {
        // A hand-edited 900% arrives already clamped to 200% by the store that read it, so it
        // reads as "incoming 200%", not "incoming 900%" nobody could apply.
        var current = SetupSettingsSnapshot.Default;
        var incoming = current with { Appearance = current.Appearance with { TextScalePercent = 900 } };

        var entry = SetupSettingsDiffRow.From(Assert.Single(SetupSettingsDiff.Compare(current, incoming)));

        Assert.Equal("200", entry.NewValue);
    }

    [Fact]
    public void ExportRoundTripsThroughValidate()
    {
        var snapshot = new SetupSettingsSnapshot(
            new WorkspacePreferences(AppearanceTheme.Light, ColorVisionMode.RedGreenSafe, 150, InterfaceDensity.Compact, true, true),
            new NotificationSettings { SquadMark = false, ShowsDesktopPopup = true },
            new ScreenshotRetentionSettings(true, 48));

        var json = SetupSettingsExport.ToJson(snapshot);
        var result = SetupSettingsExport.Validate(json);

        Assert.True(result.IsValid);
        Assert.Equal(snapshot, result.Snapshot);
    }

    [Fact]
    public void TheFileNamesItsChoicesRatherThanNumberingThem()
    {
        var json = SetupSettingsExport.ToJson(SetupSettingsSnapshot.Default with
        {
            Appearance = SetupSettingsSnapshot.Default.Appearance with { Theme = AppearanceTheme.HighContrast },
        });

        Assert.Contains("\"theme\": \"HighContrast\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInTheExportCouldEverCarryASecret()
    {
        var json = SetupSettingsExport.ToJson(SetupSettingsSnapshot.Default);

        foreach (var forbidden in new[] { "token", "credential", "password", "secret" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MalformedJsonFailsValidationRatherThanCrashing()
    {
        var result = SetupSettingsExport.Validate("{ not json");

        Assert.False(result.IsValid);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ANewerSchemaVersionIsRefusedRatherThanGuessedAt()
    {
        var result = SetupSettingsExport.Validate("{\"schemaVersion\": 99, \"theme\": \"Light\"}");

        Assert.False(result.IsValid);
        Assert.Contains("newer", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileWithOneKeyInItKeepsTheDefaultsForTheRest()
    {
        var result = SetupSettingsExport.Validate("{\"schemaVersion\": 1, \"textScalePercent\": 150}");

        Assert.True(result.IsValid);
        Assert.Equal(150, result.Snapshot!.Appearance.TextScalePercent);
        Assert.Equal(AppearanceTheme.Dark, result.Snapshot.Appearance.Theme);
        Assert.Equal(SetupSettingsSnapshot.Default.Notifications, result.Snapshot.Notifications);
    }

    [Fact]
    public void AWildEnumValueIsClampedRatherThanRejected()
    {
        var result = SetupSettingsExport.Validate("{\"schemaVersion\": 1, \"textScalePercent\": 900}");

        Assert.True(result.IsValid);
        Assert.Equal(200, result.Snapshot!.Appearance.TextScalePercent);
    }

    [Fact]
    public void AnEmptyFileFailsValidation()
    {
        var result = SetupSettingsExport.Validate("null");

        Assert.False(result.IsValid);
    }
}
