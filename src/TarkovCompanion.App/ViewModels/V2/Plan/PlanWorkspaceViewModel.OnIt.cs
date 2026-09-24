using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>[#802] "On it" and "Show in Raid" on a Plan objective row.</summary>
public sealed partial class PlanObjectiveRowViewModel
{
    private ICommand? _onItCommand;
    private ICommand? _showInRaidCommand;

    /// <summary>The quest is not on the Raid map yet: offer "On it".</summary>
    public bool CanPutOnIt => PlanQuestOnIt.CanPutOnIt(Task);

    /// <summary>The quest is on the Raid map and this objective names a map: offer "Show in Raid".</summary>
    public bool CanShowInRaid => PlanQuestOnIt.ShowsInRaid(Task) && Objective.MapIds.Count > 0;

    public ICommand OnItCommand => _onItCommand ??= new AsyncDelegateCommand(() => _owner.PutOnItAsync([Task]));

    public ICommand ShowInRaidCommand => _showInRaidCommand ??= new AsyncDelegateCommand(
        () => _owner.ShowOnMapAsync(_owner.MapIdShowing(this), Objective.ObjectiveId));
}

public sealed partial class PlanWorkspaceViewModel
{
    /// <summary>Records each quest Active and pinned, then reads the board and the map's quest layer again.</summary>
    internal Task PutOnItAsync(IReadOnlyList<Core.Domain.Quests.QuestSummaryReadModel> tasks) => tasks.Count == 0
        ? System.Threading.Tasks.Task.CompletedTask
        : MutateAsync(async scope =>
        {
            foreach (var task in tasks)
            {
                await PlanQuestOnIt.ApplyAsync(_commandService, scope, task, CancellationToken.None).ConfigureAwait(true);
            }
        });

    /// <summary>The map a row is shown under: the selected group's, where the row is in it, else its first.</summary>
    internal string MapIdShowing(PlanObjectiveRowViewModel row) =>
        SelectedGroup is { MapId: { } mapId } group && group.Objectives.Contains(row)
            ? mapId
            : row.Objective.MapIds[0];

    /// <summary>
    /// "Open in Raid": puts a searched-for handful of untracked quests on the map first, then opens
    /// the Raid map on this map with its route and the first of those objectives selected.
    /// </summary>
    /// <remarks>
    /// [#802] This used to carry only the visit order. A quest found with the search while its
    /// state was Unknown -- every quest, with no game log -- was not on the Raid objectives layer,
    /// so the Raid map opened with nothing on it.
    /// </remarks>
    private async Task OpenInRaidAsync(string mapId)
    {
        var putOn = GroupFor(mapId) is { } before
            ? PlanQuestOnIt.ForOpenInRaid(before.Objectives.Select(row => row.Task))
            : [];
        await PutOnItAsync(putOn).ConfigureAwait(true);
        var putOnIds = putOn.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        var group = GroupFor(mapId);
        var target = group?.VisitOrder.FirstOrDefault(row => putOnIds.Contains(row.Task.TaskId)) ??
            group?.VisitOrder.FirstOrDefault(row => PlanQuestOnIt.ShowsInRaid(row.Task));
        _raidCockpit?.SetObjectiveRoute(mapId, group?.Route);
        await ShowOnMapAsync(mapId, target?.Objective.ObjectiveId).ConfigureAwait(true);
    }

    private PlanMapGroupViewModel? GroupFor(string mapId) => Groups.FirstOrDefault(candidate =>
        string.Equals(candidate.MapId, mapId, StringComparison.OrdinalIgnoreCase));
}
