using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.Application.Services.Setup;

/// <summary>
/// Everything Setup's "Reset this section", "Reset everything", export and import (#292 task 2)
/// treat as one bundle.
/// </summary>
/// <remarks>
/// Deliberately three records, not the whole application: <see cref="WorkspacePreferences"/>
/// (theme, colour vision, text size, density, motion, focus), <see cref="NotificationSettings"/>
/// (the five switches and the pop-up) and <see cref="ScreenshotRetentionSettings"/> (screenshot
/// tidying). All three are small, portable choices a player would want copied to a new machine.
///
/// <para>
/// What is <em>not</em> here is the point as much as what is. Screenshot and log folder overrides
/// are machine-specific paths, not preferences, and copying them to a new machine would point the
/// companion at a folder that does not exist there. The TarkovTracker token and the group relay's
/// credentials are secrets: <see cref="SetupSettingsExport"/> has no field for either, so there is
/// no path by which an export can carry one.
/// </para>
/// </remarks>
public sealed record SetupSettingsSnapshot(
    WorkspacePreferences Appearance,
    NotificationSettings Notifications,
    ScreenshotRetentionSettings ScreenshotRetention)
{
    /// <summary>The shape <see cref="SetupSettingsExport"/> writes and the only one it reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>What a player who has changed nothing has: every domain's own default.</summary>
    public static SetupSettingsSnapshot Default { get; } = new(
        WorkspacePreferences.Default,
        NotificationSettings.Default,
        ScreenshotRetentionSettings.Default);

    /// <summary>The same snapshot with every value inside the range this build understands.</summary>
    public SetupSettingsSnapshot Normalized() => this with
    {
        Appearance = Appearance.Normalized(),
        ScreenshotRetention = ScreenshotRetention with { RetentionHours = ScreenshotRetention.SafeRetentionHours },
    };
}
