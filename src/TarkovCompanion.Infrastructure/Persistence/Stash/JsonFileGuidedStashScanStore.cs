using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Infrastructure.Persistence.Stash;

/// <summary>
/// Keeps an unfinished guided stash scan in one small file, so closing the application halfway
/// through a scroll does not lose the screenshots already taken.
/// </summary>
/// <remarks>
/// The file holds the same pixel-free recognition the snapshot store would, serialized with the
/// same contract options - never an image, and never the process-local content digest. It is
/// written to a sibling and moved into place, so a crash mid-write leaves the previous copy. A
/// file that no longer reads is set aside under a <c>.unreadable</c> name rather than deleted:
/// losing a scan quietly is the one thing this exists to prevent.
/// </remarks>
public sealed class JsonFileGuidedStashScanStore(
    string path,
    ILogger<JsonFileGuidedStashScanStore>? logger = null) : IGuidedStashScanPendingStore
{
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A file path is required.", nameof(path))
        : path;

    private readonly ILogger<JsonFileGuidedStashScanStore> _logger = logger ?? NullLogger<JsonFileGuidedStashScanStore>.Instance;

    public async Task<GuidedStashScanPending?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer
                .DeserializeAsync<GuidedStashScanPending>(stream, V2ContractJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            _logger.LogWarning(exception, "An unfinished stash scan could not be read back and was set aside.");
            try
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }
            catch (IOException)
            {
                // Left in place; the next save replaces it.
            }

            return null;
        }
    }

    public async Task SaveAsync(GuidedStashScanPending pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, pending, V2ContractJson.Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
