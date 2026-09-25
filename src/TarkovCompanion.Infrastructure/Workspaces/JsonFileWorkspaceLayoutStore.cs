using System.Collections.Concurrent;
using System.Text.Json;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.Infrastructure.Workspaces;

/// <summary>
/// One small JSON file of chrome preferences, read once and written as they change.
/// </summary>
/// <remarks>
/// [V2 rough package 46] Synchronous on purpose. A rail that collapses when it is asked to and
/// remembers the choice is not worth an async seam through three view models; the file is a few
/// hundred bytes, it is read once at construction, and a write that fails leaves the choice
/// working for this session and forgotten by the next. Bounded like every other cache here, so a
/// file somebody has replaced with a gigabyte of something else cannot be loaded.
/// </remarks>
public sealed class JsonFileWorkspaceLayoutStore : IWorkspaceLayoutStore
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumEntries = 128;
    private const int MaximumValueLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 4,
    };

    private readonly string _path;
    private readonly ConcurrentDictionary<string, string> _values;
    private readonly Lock _writeGate = new();

    public JsonFileWorkspaceLayoutStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _path = settingsPath;
        _values = new(ReadOrEmpty(settingsPath), StringComparer.Ordinal);
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumValueLength || (_values.Count >= MaximumEntries && !_values.ContainsKey(key)))
        {
            return;
        }

        if (string.Equals(_values.GetValueOrDefault(key), value, StringComparison.Ordinal))
        {
            return;
        }

        _values[key] = value;
        lock (_writeGate)
        {
            Write(_path, _values);
        }
    }

    private static Dictionary<string, string> ReadOrEmpty(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumBytes)
            {
                return [];
            }

            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
            return values is null
                ? []
                : values
                    .Where(entry => entry.Value.Length <= MaximumValueLength)
                    .Take(MaximumEntries)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or NotSupportedException)
        {
            return [];
        }
    }

    private static void Write(string path, IDictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            // A remembered width is not worth an error anybody has to read.
        }
    }
}
