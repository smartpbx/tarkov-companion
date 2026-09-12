using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores how long screenshots are kept, in <c>Config/screenshots.json</c>.
/// </summary>
/// <remarks>
/// An unreadable file falls back to the default rather than to off, which is the opposite of
/// how the group settings beside it fail. The directions differ because the risks do: failing
/// open there would send data somewhere, while failing closed here would let the folder grow
/// without limit, which is the thing this exists to prevent. Nothing is destroyed either way,
/// because everything goes to the recycle bin.
/// </remarks>
public sealed class JsonFileScreenshotRetentionStore(string settingsPath) : IScreenshotRetentionStore
{
    private const int MaximumSettingsBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null
                ? ScreenshotRetentionSettings.Default
                : new(document.Enabled, document.RetentionHours);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new RetentionDocument(settings.IsEnabled, settings.SafeRetentionHours);
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

    private async Task<RetentionDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RetentionDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    private sealed record RetentionDocument(bool Enabled, int RetentionHours);
}
