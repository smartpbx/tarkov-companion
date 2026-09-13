using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores the game folders the player named, in <c>Config/game-folders.json</c>.
/// </summary>
/// <remarks>
/// An unreadable file falls back to guessing rather than to nothing. A corrupt settings file
/// should cost the player their override, which they can retype, and not their raid tracking.
/// </remarks>
public sealed class JsonFileEftPathOverrideStore(string settingsPath) : IEftPathOverrideStore
{
    private const int MaximumSettingsBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<EftPathOverrides> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null
                ? EftPathOverrides.None
                : new EftPathOverrides(document.ScreenshotRoot, document.LogRoot).Normalized();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(EftPathOverrides overrides, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = overrides.Normalized();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllTextAsync(
                settingsPath,
                JsonSerializer.Serialize(
                    new FolderDocument(normalized.ScreenshotRoot, normalized.LogRoot),
                    JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FolderDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FolderDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    private sealed record FolderDocument(string? ScreenshotRoot, string? LogRoot);
}
