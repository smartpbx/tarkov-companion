using System.Collections.Concurrent;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// Records review corrections for the lifetime of this process only.
/// </summary>
/// <remarks>
/// Nothing yet applies a <see cref="StashReviewCommand"/> back onto a snapshot's recognition —
/// that requires a durable command log and a replay/apply step, neither of which exists. Rather
/// than leave stash corrections unavailable, this sink accepts and lists them honestly as
/// pending and not yet applied; a durable store is deferred to polish.
/// </remarks>
public sealed class InMemoryStashReviewCommandSink : IStashReviewCommandSink
{
    private readonly ConcurrentDictionary<string, List<StashReviewCommand>> _bySnapshot = new(StringComparer.Ordinal);

    public Task AppendAsync(StashReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var commands = _bySnapshot.GetOrAdd(command.SnapshotId, static _ => []);
        lock (commands)
        {
            commands.Add(command);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<StashReviewCommand> List(string snapshotId)
    {
        if (!_bySnapshot.TryGetValue(snapshotId, out var commands))
        {
            return [];
        }

        lock (commands)
        {
            return commands.ToArray();
        }
    }
}
