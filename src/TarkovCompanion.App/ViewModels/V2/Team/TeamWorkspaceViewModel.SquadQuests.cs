using TarkovCompanion.App.Localization;
using Avalonia.Media;
using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>[#780] One quest on a member's list, with how far along they are.</summary>
public sealed record TeamSquadQuestRowViewModel(string Name, string Progress, bool IsShared)
{
    public bool HasProgress => Progress.Length > 0;
}

/// <summary>[#780] One member's quests, in their squad colour.</summary>
public sealed record TeamSquadQuestMemberViewModel(
    string Name,
    IBrush? Colour,
    string Summary,
    IReadOnlyList<TeamSquadQuestRowViewModel> Quests)
{
    public bool HasColour => Colour is not null;
}

public sealed partial class TeamWorkspaceViewModel
{
    private SquadQuestFeed? _squadQuests;

    /// <summary>[#780] Every member's active quests with progress, this player's first.</summary>
    public IReadOnlyList<TeamSquadQuestMemberViewModel> SquadQuests { get; private set; } = [];

    public bool HasSquadQuests => SquadQuests.Count > 0;

    /// <summary>"2 shared" when members overlap; empty otherwise.</summary>
    public string SquadQuestsSummary { get; private set; } = string.Empty;

    private void AttachSquadQuests(SquadQuestFeed? feed)
    {
        if (feed is null)
        {
            return;
        }

        _squadQuests = feed;
        feed.Changed += RefreshSquadQuests;
        RefreshSquadQuests();
    }

    private void RefreshSquadQuests()
    {
        if (_squadQuests is not { } feed)
        {
            return;
        }

        SquadQuests = BuildSquadQuests(feed.Picture, name => _raidCockpit?.SquadColorFor(name));
        SquadQuestsSummary = feed.Picture.SharedTaskIds.Count == 0 ? string.Empty : TeamText.SharedQuestCount(feed.Picture.SharedTaskIds.Count);
        OnPropertyChanged(nameof(SquadQuests));
        OnPropertyChanged(nameof(HasSquadQuests));
        OnPropertyChanged(nameof(SquadQuestsSummary));
    }

    /// <summary>The rows for a resolved picture; shared quests first within each member.</summary>
    internal static IReadOnlyList<TeamSquadQuestMemberViewModel> BuildSquadQuests(
        SquadQuestPicture picture,
        Func<string, string?> colourFor)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return
        [
            .. picture.Members
                .Where(member => member.Quests.Count > 0)
                .OrderBy(member => member.IsSelf ? 0 : 1)
                .Select(member => new TeamSquadQuestMemberViewModel(
                    member.Name,
                    member.IsSelf || colourFor(member.Name) is not { } hex || !Color.TryParse(hex, out var colour)
                        ? null
                        : new SolidColorBrush(colour),
                    TeamText.QuestCount(member.Quests.Count),
                    [
                        .. member.Quests
                            .OrderBy(quest => picture.SharedTaskIds.Contains(quest.TaskId) ? 0 : 1)
                            .ThenBy(quest => quest.Name, StringComparer.CurrentCulture)
                            .Select(quest => new TeamSquadQuestRowViewModel(
                                quest.Name,
                                Progress(quest),
                                picture.SharedTaskIds.Contains(quest.TaskId))),
                    ])),
        ];
    }

    /// <summary>"2/5 done"; empty where nothing is open or the member sent no objectives.</summary>
    /// <remarks>
    /// Nothing open is not claimed as "done": a pinned quest the member has not started is on the
    /// list with no open objectives too, and the wire does not say which of the two it is.
    /// </remarks>
    internal static string Progress(SquadQuest quest) =>
        !quest.ReportsObjectives || quest.ObjectiveCount == 0 || quest.OpenCount == 0
            ? string.Empty
            : TeamText.QuestProgress(quest.DoneCount, quest.ObjectiveCount);
}
