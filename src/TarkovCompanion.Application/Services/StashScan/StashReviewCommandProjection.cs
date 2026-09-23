using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// The effective manual choices in an append-only stash review log. Undo is another durable
/// record whose origin names the command it cancels; captured evidence is never rewritten.
/// </summary>
public sealed record StashReviewCommandState(
    IReadOnlySet<string> PinnedItemKeys,
    IReadOnlySet<string> IgnoredItemKeys,
    IReadOnlySet<string> RescanContainerPaths,
    IReadOnlySet<Guid> MergedSnapshotIds,
    IReadOnlyList<StashReviewCommand> ActiveCommands)
{
    public static StashReviewCommandState Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<Guid>(),
        []);

    public StashReviewCommand? LatestUndoable => ActiveCommands
        .Where(IsUndoable)
        .OrderBy(command => command.CreatedUtc)
        .ThenBy(command => command.CommandId)
        .LastOrDefault();

    public static bool IsUndoable(StashReviewCommand command) => command.Action is
        StashReviewActionKind.Pin or
        StashReviewActionKind.Ignore or
        StashReviewActionKind.Rescan or
        StashReviewActionKind.MergeEntries;
}

public static class StashReviewCommandProjection
{
    public const string WorkspaceOrigin = "v2.stash-workspace";
    public const string SnapshotMergeOrigin = "v2.stash-workspace.merge-snapshots";
    private const string UndoOriginPrefix = "v2.stash-workspace.undo:";

    public static StashReviewCommandState Project(IReadOnlyList<StashReviewCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var undone = commands
            .Select(command => UndoTarget(command.OriginIdentifier))
            .Where(commandId => commandId is not null)
            .Select(commandId => commandId!.Value)
            .ToHashSet();
        var active = commands
            .Where(command => UndoTarget(command.OriginIdentifier) is null && !undone.Contains(command.CommandId))
            .OrderBy(command => command.CreatedUtc)
            .ThenBy(command => command.CommandId)
            .ToArray();
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var rescans = new HashSet<string>(StringComparer.Ordinal);
        var merged = new HashSet<Guid>();

        foreach (var command in active)
        {
            switch (command.Action)
            {
                case StashReviewActionKind.Pin:
                    pinned.Add(command.TargetItemKeys[0]);
                    break;
                case StashReviewActionKind.Unpin:
                    pinned.Remove(command.TargetItemKeys[0]);
                    break;
                case StashReviewActionKind.Ignore:
                    ignored.Add(command.TargetItemKeys[0]);
                    break;
                case StashReviewActionKind.Unignore:
                    ignored.Remove(command.TargetItemKeys[0]);
                    break;
                case StashReviewActionKind.Rescan:
                    rescans.Add(command.TargetItemKeys[0]);
                    break;
                case StashReviewActionKind.MergeEntries when
                    string.Equals(command.OriginIdentifier, SnapshotMergeOrigin, StringComparison.Ordinal):
                    foreach (var target in command.TargetItemKeys)
                    {
                        if (Guid.TryParseExact(target, "D", out var snapshotId))
                        {
                            merged.Add(snapshotId);
                        }
                    }

                    break;
            }
        }

        return new(pinned, ignored, rescans, merged, active);
    }

    public static StashReviewCommand Undo(
        StashReviewCommand command,
        DateTimeOffset createdUtc,
        Guid? commandId = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!StashReviewCommandState.IsUndoable(command))
        {
            throw new ArgumentException("That stash review command cannot be undone.", nameof(command));
        }

        var inverse = command.Action switch
        {
            StashReviewActionKind.Pin => StashReviewActionKind.Unpin,
            StashReviewActionKind.Ignore => StashReviewActionKind.Unignore,
            StashReviewActionKind.MergeEntries => StashReviewActionKind.SplitEntry,
            _ => StashReviewActionKind.Rescan,
        };
        return new StashReviewCommand(
            commandId ?? Guid.NewGuid(),
            command.SnapshotId,
            inverse,
            [command.CommandId.ToString("D")],
            createdUtc,
            $"{UndoOriginPrefix}{command.CommandId:D}",
            reason: $"Undid {ActionLabel(command.Action)}.");
    }

    public static string ActionLabel(StashReviewActionKind action) => action switch
    {
        StashReviewActionKind.MergeEntries => "snapshot merge",
        StashReviewActionKind.Rescan => "region rescan",
        _ => action.ToString().ToLowerInvariant(),
    };

    public static bool IsUndoRecord(StashReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return UndoTarget(command.OriginIdentifier) is not null;
    }

    private static Guid? UndoTarget(string originIdentifier) =>
        originIdentifier.StartsWith(UndoOriginPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(originIdentifier[UndoOriginPrefix.Length..], "D", out var commandId)
            ? commandId
            : null;
}
