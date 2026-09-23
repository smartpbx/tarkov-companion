using TarkovCompanion.App.ViewModels.V2.Team;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

public sealed partial class PlanObjectiveRowViewModel
{
    /// <summary>[#780] "Squad has it too · Geo" where a squadmate also has this quest active.</summary>
    public string SquadLabel => _owner?.SquadLabelFor(Task.TaskId) ?? string.Empty;

    public bool HasSquadLabel => SquadLabel.Length > 0;

    internal void NotifySquad()
    {
        OnPropertyChanged(nameof(SquadLabel));
        OnPropertyChanged(nameof(HasSquadLabel));
    }
}

public sealed partial class PlanWorkspaceViewModel
{
    private SquadQuestFeed? _squadQuests;

    private void AttachSquadQuests(SquadQuestFeed? feed)
    {
        if (feed is null)
        {
            return;
        }

        _squadQuests = feed;
        feed.Changed += () =>
        {
            foreach (var row in _groups.SelectMany(group => group.Objectives))
            {
                row.NotifySquad();
            }
        };
    }

    /// <summary>"Squad has it too · Geo, Riley", or empty when no squadmate has the quest active.</summary>
    internal string SquadLabelFor(string taskId) =>
        _squadQuests?.SquadmatesOn(taskId) is { Count: > 0 } names
            ? $"Squad has it too · {string.Join(", ", names)}"
            : string.Empty;
}
