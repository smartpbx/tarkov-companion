using System.Text.Json;
using TarkovCompanion.Application.Services.Notifications;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores which notifications are on, in <c>Config/notifications.json</c>.
/// </summary>
/// <remarks>
/// [V2 rough package 43] An unreadable file falls back to the defaults — all five on, no pop-up —
/// rather than to silence. The direction matters: failing to silence would leave somebody who
/// turned these on being told nothing at all, with no way to tell that from a quiet evening, and
/// the only setting here that can put something on screen by itself already defaults to off.
/// </remarks>
public sealed class JsonFileNotificationSettingsStore(string settingsPath) : INotificationSettingsStore
{
    private const int MaximumSettingsBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<NotificationSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null
                ? NotificationSettings.Default
                : new()
                {
                    SquadMark = document.SquadMark,
                    DebriefReady = document.DebriefReady,
                    DataRefreshFailed = document.DataRefreshFailed,
                    UpdateReady = document.UpdateReady,
                    RelayUnreachable = document.RelayUnreachable,
                    ShowsDesktopPopup = document.ShowsDesktopPopup,
                    FleaSold = document.FleaSold,
                    QuietHours = document.QuietHours,
                    QuietFromHour = Math.Clamp(document.QuietFromHour, 0, 23),
                    QuietToHour = Math.Clamp(document.QuietToHour, 0, 23),
                };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new NotificationDocument(
                settings.SquadMark,
                settings.DebriefReady,
                settings.DataRefreshFailed,
                settings.UpdateReady,
                settings.RelayUnreachable,
                settings.ShowsDesktopPopup,
                settings.FleaSold,
                settings.QuietHours,
                settings.QuietFromHour,
                settings.QuietToHour);
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NotificationDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NotificationDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every switch written out, with the defaults repeated.
    /// </summary>
    /// <remarks>
    /// Defaulted per property rather than relying on the record's own defaults, because a file
    /// written by an older build has no key for a switch added later and JSON would supply
    /// <c>false</c> — silently turning off a notification nobody chose to turn off.
    /// </remarks>
    private sealed record NotificationDocument(
        bool SquadMark = true,
        bool DebriefReady = true,
        bool DataRefreshFailed = true,
        bool UpdateReady = true,
        bool RelayUnreachable = true,
        bool ShowsDesktopPopup = false,
        bool FleaSold = true,
        bool QuietHours = false,
        int QuietFromHour = 23,
        int QuietToHour = 8);
}
