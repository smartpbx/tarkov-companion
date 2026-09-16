using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>Local raid marks, kept in <c>Config/raid-marks.json</c> between runs.</summary>
/// <remarks>
/// An unreadable file falls back to an empty board rather than to nothing loading at all: see
/// <see cref="JsonFileEftPathOverrideStore"/> for the same reasoning applied to game folders.
/// </remarks>
public sealed class JsonFileRaidMarkStore(string storePath, TimeProvider? timeProvider = null) : IRaidMarkStore
{
    private const int MaximumMarks = 500;
    private const long MaximumFileBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<RaidMark> _marks = [];
    private bool _loaded;

    public IReadOnlyList<RaidMark> Marks => _marks;

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
            _marks = document is null ? [] : [.. document.Marks.Select(ToMark).OfType<RaidMark>()];
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task<RaidMark> AddAsync(
        RaidMarkKind kind,
        string mapId,
        string? floorId,
        double x,
        double y,
        string? label,
        CancellationToken cancellationToken = default)
    {
        var mark = new RaidMark(
            Guid.NewGuid(),
            kind,
            new MapMarkState(mapId, floorId, x, y, label, null),
            _timeProvider.GetUtcNow());
        await MutateAsync(marks => marks.Add(mark), cancellationToken).ConfigureAwait(false);
        return mark;
    }

    public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default) =>
        MutateAsync(
            marks =>
            {
                var index = marks.FindIndex(mark => mark.Id == id);
                if (index < 0)
                {
                    return;
                }

                var current = marks[index];
                // MapMarkState re-declares X/Y as validated get-only properties (see its own
                // remark on why), which is exactly the shape a `with` expression cannot touch;
                // a new instance is required instead.
                marks[index] = current with
                {
                    State = new MapMarkState(
                        current.State.MapId,
                        current.State.FloorId,
                        x,
                        y,
                        current.State.Label,
                        current.State.ExpiresUtc),
                };
            },
            cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(marks => marks.RemoveAll(mark => mark.Id == id), cancellationToken);

    private async Task MutateAsync(Action<List<RaidMark>> mutate, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            mutate(_marks);
            if (_marks.Count > MaximumMarks)
            {
                // Oldest first: a mark placed minutes ago is more likely to still matter than
                // one placed a raid ago.
                _marks = [.. _marks.OrderByDescending(mark => mark.CreatedUtc).Take(MaximumMarks)];
            }

            await AtomicJsonFile.WriteAsync(
                storePath,
                JsonSerializer.Serialize(new MarkDocument([.. _marks.Select(ToRow)]), JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<MarkDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(storePath);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MarkDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Null for a row a hand-edited or schema-drifted file made invalid, so one bad row
    /// costs that mark rather than every mark the file held.</summary>
    private static RaidMark? ToMark(MarkRow row)
    {
        try
        {
            return new RaidMark(
                row.Id,
                row.Kind,
                new MapMarkState(row.MapId, row.FloorId, row.X, row.Y, row.Label, null),
                row.CreatedUtc);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static MarkRow ToRow(RaidMark mark) => new(
        mark.Id,
        mark.Kind,
        mark.State.MapId,
        mark.State.FloorId,
        mark.State.X,
        mark.State.Y,
        mark.State.Label,
        mark.CreatedUtc);

    private sealed record MarkDocument(IReadOnlyList<MarkRow> Marks);

    private sealed record MarkRow(
        Guid Id,
        RaidMarkKind Kind,
        string MapId,
        string? FloorId,
        double X,
        double Y,
        string? Label,
        DateTimeOffset CreatedUtc);
}
