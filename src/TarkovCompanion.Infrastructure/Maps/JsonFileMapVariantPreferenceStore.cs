using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

public sealed class JsonFileMapVariantPreferenceStore(string settingsPath) : IMapVariantPreferenceStore
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAsync(string locationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferences = await ReadOrEmptyAsync(cancellationToken).ConfigureAwait(false);
            return preferences.GetValueOrDefault(locationId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string locationId, string variantKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferences = await ReadOrEmptyAsync(cancellationToken).ConfigureAwait(false);
            preferences[locationId] = variantKey;
            await WriteAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new SortedDictionary<string, string>(
                await ReadOrEmptyAsync(cancellationToken).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAllAsync(IReadOnlyDictionary<string, string> choices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choices);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(
                    choices.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadOrEmptyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = new FileStream(
            settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumSettingsBytes)
        {
            throw new InvalidDataException("Map variant settings exceed the local size limit.");
        }

        try
        {
            var document = await JsonSerializer
                .DeserializeAsync<PreferenceDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return new(document?.Defaults ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Map variant settings are not valid JSON.", exception);
        }
    }

    private async Task WriteAsync(
        IReadOnlyDictionary<string, string> preferences,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))
            ?? throw new InvalidOperationException("The map variant settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new PreferenceDocument(new Dictionary<string, string>(preferences, StringComparer.OrdinalIgnoreCase)),
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record PreferenceDocument(Dictionary<string, string> Defaults);
}
