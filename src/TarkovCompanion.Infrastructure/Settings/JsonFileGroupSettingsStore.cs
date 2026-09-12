using System.Text.Json;
using TarkovCompanion.Application.Services.Group;

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

    public async Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null
                ? GroupSharingSettings.Off
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
                    document.ShareQuests);
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
                settings.SharesQuests);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllTextAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

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
            // Sharing nothing is the safe answer to a file that cannot be read.
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
        bool ShareQuests);
}
