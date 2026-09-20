using System.Text.Json;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>The player's own quest objective markers, kept in <c>Config/user-quest-markers.json</c>.</summary>
/// <remarks>
/// A file of their own, not part of the quest catalog's tables: a sync replaces the catalog, and a
/// marker is a note the player wrote, so it has to outlive that. An unreadable file falls back to
/// no markers rather than to nothing loading, like <see cref="JsonFileRaidMarkStore"/>; one bad row
/// costs that marker and no other.
/// </remarks>
public sealed class JsonFileUserQuestMarkStore(string storePath, TimeProvider? timeProvider = null) : IUserQuestMarkStore
{
    private const int MaximumMarkers = 2_000;
    private const int MaximumIdLength = 128;
    private const long MaximumFileBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<UserQuestMarker> _markers = [];
    private bool _loaded;

    public IReadOnlyList<UserQuestMarker> Markers => _markers;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            _markers = document is null ? [] : [.. document.Markers.Where(IsValid)];
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public Task PlaceAsync(
        string objectiveId,
        string mapId,
        string? floorId,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        var marker = new UserQuestMarker(objectiveId, mapId, floorId, x, y, _timeProvider.GetUtcNow().ToUniversalTime());
        if (!IsValid(marker))
        {
            throw new ArgumentException("A quest marker needs an objective, a map and a finite position.");
        }

        return MutateAsync(
            markers =>
            {
                markers.RemoveAll(existing => Same(existing, objectiveId, mapId));
                markers.Add(marker);
            },
            cancellationToken);
    }

    public Task RemoveAsync(string objectiveId, string mapId, CancellationToken cancellationToken = default) =>
        MutateAsync(markers => markers.RemoveAll(existing => Same(existing, objectiveId, mapId)), cancellationToken);

    private static bool Same(UserQuestMarker marker, string objectiveId, string mapId) =>
        string.Equals(marker.ObjectiveId, objectiveId, StringComparison.Ordinal) &&
        string.Equals(marker.MapId, mapId, StringComparison.OrdinalIgnoreCase);

    private static bool IsValid(UserQuestMarker marker) =>
        !string.IsNullOrWhiteSpace(marker.ObjectiveId) && marker.ObjectiveId.Length <= MaximumIdLength &&
        !string.IsNullOrWhiteSpace(marker.MapId) && marker.MapId.Length <= MaximumIdLength &&
        (marker.FloorId is null || marker.FloorId.Length <= MaximumIdLength) &&
        double.IsFinite(marker.X) && double.IsFinite(marker.Y);

    private async Task MutateAsync(Action<List<UserQuestMarker>> mutate, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            mutate(_markers);
            if (_markers.Count > MaximumMarkers)
            {
                // Oldest first: the marker placed most recently is the one most likely to matter.
                _markers = [.. _markers.OrderByDescending(marker => marker.PlacedUtc).Take(MaximumMarkers)];
            }

            await AtomicJsonFile.WriteAsync(
                storePath,
                JsonSerializer.Serialize(new MarkerDocument([.. _markers]), JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<MarkerDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(storePath);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MarkerDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record MarkerDocument(IReadOnlyList<UserQuestMarker> Markers);
}
