using System.Text.Json;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>The player's own "done" marks, kept in <c>Config/hand-done-objectives.json</c>.</summary>
/// <remarks>
/// A file of its own for the same reason <see cref="JsonFileUserQuestMarkStore"/> is: it is a
/// player's own note, not part of anything a sync replaces. An unreadable file falls back to no
/// marks rather than to nothing loading; one bad row costs that mark and no other.
/// </remarks>
public sealed class JsonFileHandDoneObjectiveStore(string storePath, TimeProvider? timeProvider = null) : IHandDoneObjectiveStore
{
    private const int MaximumMarks = 4_000;
    private const int MaximumIdLength = 128;
    private const long MaximumFileBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<HandDoneObjective> _marks = [];
    private bool _loaded;

    public IReadOnlyList<HandDoneObjective> Entries => _marks;

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
            _marks = document is null ? [] : [.. document.Marks.Where(IsValid)];
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public Task MarkDoneAsync(Guid profileId, string taskId, string objectiveId, CancellationToken cancellationToken = default)
    {
        var mark = new HandDoneObjective(profileId, taskId, objectiveId, _timeProvider.GetUtcNow().ToUniversalTime());
        if (!IsValid(mark))
        {
            throw new ArgumentException("A hand-done mark needs a profile, a task and an objective.");
        }

        return MutateAsync(
            marks =>
            {
                marks.RemoveAll(existing => Same(existing, profileId, objectiveId));
                marks.Add(mark);
            },
            cancellationToken);
    }

    public Task MarkNotDoneAsync(Guid profileId, string objectiveId, CancellationToken cancellationToken = default) =>
        MutateAsync(marks => marks.RemoveAll(existing => Same(existing, profileId, objectiveId)), cancellationToken);

    public Task ClearForTaskAsync(Guid profileId, string taskId, CancellationToken cancellationToken = default) =>
        MutateAsync(
            marks => marks.RemoveAll(existing =>
                existing.ProfileId == profileId && string.Equals(existing.TaskId, taskId, StringComparison.Ordinal)),
            cancellationToken);

    private static bool Same(HandDoneObjective mark, Guid profileId, string objectiveId) =>
        mark.ProfileId == profileId && string.Equals(mark.ObjectiveId, objectiveId, StringComparison.Ordinal);

    private static bool IsValid(HandDoneObjective mark) =>
        mark.ProfileId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(mark.TaskId) && mark.TaskId.Length <= MaximumIdLength &&
        !string.IsNullOrWhiteSpace(mark.ObjectiveId) && mark.ObjectiveId.Length <= MaximumIdLength;

    private async Task MutateAsync(Action<List<HandDoneObjective>> mutate, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            mutate(_marks);
            if (_marks.Count > MaximumMarks)
            {
                // Oldest first: the mark placed most recently is the one most likely to matter.
                _marks = [.. _marks.OrderByDescending(mark => mark.MarkedDoneUtc).Take(MaximumMarks)];
            }

            await AtomicJsonFile.WriteAsync(
                storePath,
                JsonSerializer.Serialize(new MarkDocument([.. _marks]), JsonOptions),
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

    private sealed record MarkDocument(IReadOnlyList<HandDoneObjective> Marks);
}
