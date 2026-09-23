using System.Text.Json;
using TarkovCompanion.Application.Services.ReleaseExperience;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>Stores the last build seen and the build whose What's New banner was dismissed.</summary>
public sealed class JsonFileReleaseExperienceStateStore(string statePath) : IReleaseExperienceStateStore
{
    private const int MaximumStateBytes = 4 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ReleaseExperienceState?> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(statePath);
            if (!info.Exists || info.Length > MaximumStateBytes)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<ReleaseExperienceState>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(state?.LastSeenVersion) ? null : state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ReleaseExperienceState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(
                statePath,
                JsonSerializer.Serialize(state, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
