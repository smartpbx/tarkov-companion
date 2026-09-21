using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>
/// A quest objective the player marked done by hand, on the map or its list, because they had
/// already finished it and the app had no other way to know.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="RecordedObjectiveState"/> on purpose (issue 571): that field is the
/// app's own belief about progress, fed by the Quests page, the game's logs and a TarkovTracker
/// sync, and folding a player's "I'm done looking at this" into it would let a map action quietly
/// rewrite what those already say. This is a narrower claim — hide it — that survives independently
/// of whether the app ever comes to agree.
/// </remarks>
public sealed record HandDoneObjective(
    Guid ProfileId,
    string TaskId,
    string ObjectiveId,
    DateTimeOffset MarkedDoneUtc);

/// <summary>The player's own "done" marks, kept between runs, one profile's worth at a time.</summary>
public interface IHandDoneObjectiveStore
{
    IReadOnlyList<HandDoneObjective> Entries { get; }

    /// <summary>Raised after a load, mark or clear changes <see cref="Entries"/>.</summary>
    event Action? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks this objective done by hand, replacing any earlier mark of the same one.</summary>
    Task MarkDoneAsync(Guid profileId, string taskId, string objectiveId, CancellationToken cancellationToken = default);

    /// <summary>Undoes a hand mark; harmless where there was none.</summary>
    Task MarkNotDoneAsync(Guid profileId, string objectiveId, CancellationToken cancellationToken = default);

    /// <summary>Every hand mark of this quest's objectives, cleared: the game reported it failed or restarted.</summary>
    Task ClearForTaskAsync(Guid profileId, string taskId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hides a done objective from a map scene, or — where the player asked to see them — draws it
/// exactly like a naturally completed one: dimmed, with its check.
/// </summary>
/// <remarks>
/// "Done" here is either the player's own hand mark, or progress the app already trusts saying
/// the objective is <see cref="RecordedObjectiveState.Completed"/> (issue 508 drew that dimmed on
/// the map forever; issue 571 is the player asking for it to leave instead, the same as a hand
/// mark, until "Show completed" asks to see it again). Applied after
/// <see cref="QuestObjectiveSceneBuilder"/> and <see cref="UserQuestMarkerScene"/> have already
/// decided letters and positions, so hiding an entry never renumbers another one — see
/// <see cref="QuestObjectiveLetterAssignment"/>.
/// </remarks>
public static class HandDoneObjectiveScene
{
    public static QuestObjectiveScene Apply(
        QuestObjectiveScene scene,
        IEnumerable<string> handDoneObjectiveIds,
        bool showCompleted)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(handDoneObjectiveIds);
        var handDone = new HashSet<string>(handDoneObjectiveIds, StringComparer.Ordinal);
        bool IsDone(QuestObjectiveEntry entry) =>
            handDone.Contains(entry.ObjectiveId) || entry.Objective.ObjectiveState == RecordedObjectiveState.Completed;

        if (!scene.Entries.Any(IsDone))
        {
            return scene;
        }

        if (showCompleted)
        {
            // Only a hand mark needs its scene objects touched: one the app already calls
            // Completed was built dimmed-with-a-check by QuestObjectiveSceneBuilder already
            // (issue 508), and re-touching it here would be re-deciding something this class has
            // no opinion of its own about.
            var objects = scene.Objects
                .Select(item => handDone.Contains(ObjectiveIdOf(item.Id)) && !item.IsCompleted ? WithCompleted(item) : item)
                .ToArray();
            return new(objects, scene.Entries);
        }

        var hiddenObjectIds = scene.Entries.Where(IsDone).SelectMany(entry => entry.ObjectIds).ToHashSet();
        return new(
            scene.Objects.Where(item => !hiddenObjectIds.Contains(item.Id)).ToArray(),
            scene.Entries.Where(entry => !IsDone(entry)).ToArray());
    }

    /// <summary>
    /// The objective a scene object belongs to, read straight out of its id: every id this ever
    /// touches is one <see cref="QuestObjectiveSceneBuilder"/> or <see cref="UserQuestMarkerScene"/>
    /// made, and both spell it <c>quest:{objectiveId}:…</c>.
    /// </summary>
    private static string ObjectiveIdOf(MapSceneObjectId id)
    {
        var value = id.Value;
        var first = value.IndexOf(':');
        if (first < 0)
        {
            return string.Empty;
        }

        var second = value.IndexOf(':', first + 1);
        return second < 0 ? value[(first + 1)..] : value[(first + 1)..second];
    }

    private static MapSceneObject WithCompleted(MapSceneObject item) => new(
        item.Id,
        item.LayerId,
        item.Kind,
        item.Truth,
        item.Label,
        item.Detail,
        item.Geometry,
        item.FloorIds,
        item.Provenance,
        item.Estimate,
        item.Faction,
        item.OfferState,
        item.HeadingDegrees,
        isCompleted: true);
}
