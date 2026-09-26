using System.Text.Json;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores the group settings the player chose, in <c>Config/group.json</c>.
/// </summary>
/// <remarks>
/// Readable and hand-editable like the shortcut file beside it, for the same reason: somebody
/// who wants to know what this application is configured to send should be able to open a file
/// and read it.
///
/// The group key is stored in plain text, and that is a deliberate choice stated rather than
/// hidden. It is a word a group of friends agreed between themselves, not a credential to
/// anything of value, and encrypting it here would need a key stored beside it, which protects
/// nobody and implies a guarantee this does not make.
///
/// An unreadable file falls back to sharing nothing. Failing closed is the only safe direction
/// for a setting that decides whether data leaves the machine.
/// </remarks>
public sealed class JsonFileGroupSettingsStore(string settingsPath) : IGroupSettingsStore
{
    private const int MaximumSettingsBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Phrase? _resetReason;

    public async Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null
                ? GroupSharingSettings.Off with { ResetReason = _resetReason }
                : new(
                    document.Enabled,
                    document.ServerUri,
                    document.DisplayName,
                    // Migration, silent and one way. Settings written before the room and the
                    // secret became one value carry both; the secret is the thing that was
                    // actually secret, so it becomes the key and the room name is dropped.
                    // Somebody who upgrades keeps working without retyping anything, and
                    // everyone in a group migrates to the same room because they all had the
                    // same secret.
                    document.Key ?? document.Secret,
                    document.ShareLoadout,
                    // [#780] On unless the player opted out. A file written before the opt-out
                    // existed carries only the old switch, which defaulted off and was rarely
                    // touched, so it reads as on: squad quest sync is on by default.
                    document.QuestsOptOut is { } optedOut ? !optedOut : true)
                {
                    // [#712 T7] On unless the player opted out; a file from before reads as on.
                    SharesReadyCheck = document.ReadyCheckOptOut != true,
                };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new GroupDocument(
                settings.IsEnabled,
                settings.ServerUri,
                settings.DisplayName,
                settings.Key,
                // Written as null so an upgraded file stops carrying the old pair. Reading
                // still accepts them, because a file written by an older build is exactly the
                // case migration exists for.
                null,
                null,
                settings.SharesLoadout,
                // Still written, so an older build reading this file keeps the player's choice.
                settings.SharesQuests)
            {
                QuestsOptOut = !settings.SharesQuests,
                ReadyCheckOptOut = settings.SharesReadyCheck ? null : true,
            };
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        // After the gate, so a listener that reads straight back is not queued behind this save.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    private async Task<GroupDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GroupDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            // Sharing nothing is the safe answer to a file that cannot be read — but saying
            // nothing about it is not. Silently off, with every field blank, is
            // indistinguishable from never having set it up, and that is exactly what a
            // truncated file used to produce.
            //
            // Moved aside rather than deleted: it is the only remaining record of what was
            // configured, and "your settings were reset" is easier to believe when the old
            // file is still there.
            if (exception is JsonException)
            {
                var aside = AtomicJsonFile.SetAside(settingsPath, DateTimeOffset.UtcNow);
                _resetReason = aside is null
                    ? new Phrase(GroupStatus.SettingsReset)
                    : new Phrase(GroupStatus.SettingsResetAside, Path.GetFileName(aside));
            }

            return null;
        }
    }

    private sealed record GroupDocument(
        bool Enabled,
        string? ServerUri,
        string? DisplayName,
        string? Key,
        string? Room,
        string? Secret,
        bool ShareLoadout,
        bool ShareQuests)
    {
        /// <summary>[#780] The explicit opt-out; null in a file written before it existed.</summary>
        public bool? QuestsOptOut { get; init; }

        /// <summary>[#712 T7] The ready check's opt-out; absent while it is on.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? ReadyCheckOptOut { get; init; }
    }
}
