using System.Text.Json;
using TarkovCompanion.Application.Services.ReleaseExperience;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// [#314] Config/feature-flags.json: <c>{ "draw-mode": false }</c>. Only flags the player changed
/// are in it; a missing file means every flag follows its ring.
/// </summary>
/// <remarks>
/// Synchronous on purpose: it is read once, before any view exists, and is a few bytes. A file
/// that is not a JSON object reads as empty rather than stopping the application from starting.
/// </remarks>
public sealed class JsonFileFeatureFlagOverrideStore(string path) : IFeatureFlagOverrideStore
{
    public const string FileName = "feature-flags.json";
    private const int MaximumBytes = 16 * 1024;

    public IReadOnlyDictionary<string, bool?> Read()
    {
        var entries = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return entries;
        }

        if (info.Length > MaximumBytes)
        {
            throw new InvalidDataException($"{FileName} is larger than {MaximumBytes} bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{FileName} is not a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                entries[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON.", exception);
        }

        return entries;
    }

    public void Save(IReadOnlyDictionary<string, bool> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var json = JsonSerializer.Serialize(
            overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            new JsonSerializerOptions { WriteIndented = true });
        AtomicJsonFile.Write(path, json);
    }
}
