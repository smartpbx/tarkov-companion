using System.Text.Json;
using TarkovCompanion.Application.Services.Loadouts;
using TarkovCompanion.Core.Domain.Loadouts;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Saved kits, in <c>Config/loadouts.json</c>.
/// </summary>
/// <remarks>
/// Fails to an empty list rather than to an error, like the shell-layout store beside it: the
/// worst outcome of an unreadable file is a page that offers no saved kits, which a player can
/// recover from by saving one. The file is capped at
/// <see cref="LoadoutPreset.MaximumPresets"/> kits, oldest dropped first, because it is read every
/// time the page opens.
/// </remarks>
public sealed class JsonFileLoadoutPresetStore(string settingsPath) : ILoadoutPresetStore
{
    private const int MaximumSettingsBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<LoadoutPreset>> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LoadoutPreset preset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .Where(saved => !Same(saved.Name, preset.Name))
                .Append(preset)
                .OrderByDescending(saved => saved.SavedUtc)
                .Take(LoadoutPreset.MaximumPresets)
                .ToArray();
            await WriteAsync(kept, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = (await ReadAsync(cancellationToken).ConfigureAwait(false))
                .Where(saved => !Same(saved.Name, name))
                .ToArray();
            await WriteAsync(kept, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<LoadoutPreset>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return [];
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<PresetDocument>(text, JsonOptions);
            if (document is null || document.SchemaVersion > LoadoutPreset.SchemaVersion)
            {
                return [];
            }

            return
            [
                .. (document.Presets ?? [])
                    .Where(preset => LoadoutPreset.IsUsableName(preset.Name))
                    .Select(preset => new LoadoutPreset(
                        preset.Name!.Trim(),
                        preset.SavedUtc,
                        [.. (preset.Items ?? []).Where(item =>
                                !string.IsNullOrWhiteSpace(item.Slot) && !string.IsNullOrWhiteSpace(item.ItemId))
                            .Select(item => new LoadoutPresetItem(item.Slot!, item.ItemId!, item.Name ?? item.ItemId!))]))
                    .OrderByDescending(preset => preset.SavedUtc)
                    .Take(LoadoutPreset.MaximumPresets),
            ];
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return [];
        }
    }

    private async Task WriteAsync(IReadOnlyList<LoadoutPreset> presets, CancellationToken cancellationToken)
    {
        var document = new PresetDocument(
            LoadoutPreset.SchemaVersion,
            [.. presets.Select(preset => new PresetRecord(
                preset.Name,
                preset.SavedUtc,
                [.. preset.Items.Select(item => new PresetItemRecord(item.Slot, item.ItemId, item.Name))]))]);
        await AtomicJsonFile.WriteAsync(
            settingsPath,
            JsonSerializer.Serialize(document, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record PresetDocument(int SchemaVersion, IReadOnlyList<PresetRecord>? Presets);

    private sealed record PresetRecord(string? Name, DateTimeOffset SavedUtc, IReadOnlyList<PresetItemRecord>? Items);

    private sealed record PresetItemRecord(string? Slot, string? ItemId, string? Name);
}
