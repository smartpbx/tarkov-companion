using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Keeps the last few screenshot tidies in one small file, so "what did it delete, and did anything fail"
/// survives a restart (#309).
/// </summary>
/// <remarks>
/// Twenty entries and no file names. It is a ledger of counts and reasons, and a file that cannot be read
/// is an empty ledger rather than an error: the record of a tidy must never be the reason the next one, or
/// the settings page, fails. Writes go to a temporary file first, so a kill cannot leave half a ledger.
/// </remarks>
public sealed class JsonFileScreenshotTidyLedger(string path) : IScreenshotTidyLedger
{
    /// <summary>The most entries kept; older ones fall off the front.</summary>
    public const int MaximumEntries = 20;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    public IReadOnlyList<TidyLedgerEntry> Read()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    public void Append(TidyLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var entries = ReadUnlocked().Append(entry).TakeLast(MaximumEntries).ToArray();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private TidyLedgerEntry[] ReadUnlocked()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TidyLedgerEntry[]>(File.ReadAllText(path), Options) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }
}
