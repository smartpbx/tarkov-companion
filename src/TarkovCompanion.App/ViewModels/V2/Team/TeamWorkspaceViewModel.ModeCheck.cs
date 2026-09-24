using System.Windows.Input;
using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.ViewModels.V2.Team;

public sealed partial class TeamWorkspaceViewModel
{
    private readonly GroupModeWarnings _modeWarnings = new();
    private string _modeWarning = string.Empty;
    private ICommand? _dismissModeWarningCommand;

    /// <summary>
    /// [#269] "Sam is on PvE; this profile is PvP. Quests are not shared across modes." Said once:
    /// dismissed, it stays quiet until somebody new on another mode joins.
    /// </summary>
    public string ModeWarning
    {
        get => _modeWarning;
        private set
        {
            if (SetProperty(ref _modeWarning, value))
            {
                OnPropertyChanged(nameof(HasModeWarning));
            }
        }
    }

    public bool HasModeWarning => ModeWarning.Length > 0;

    public ICommand DismissModeWarningCommand => _dismissModeWarningCommand ??= new DelegateCommand(() =>
    {
        _modeWarnings.Dismiss(_group.MyGameMode, _group.Members);
        ModeWarning = string.Empty;
    });

    private void RefreshModeWarning(GroupSnapshot group) =>
        ModeWarning = group.IsSharing ? _modeWarnings.Current(group.MyGameMode, group.Members) ?? string.Empty : string.Empty;
}
