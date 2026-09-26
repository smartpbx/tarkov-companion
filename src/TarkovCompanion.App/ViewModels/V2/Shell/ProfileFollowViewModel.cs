using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// [#712 decision 4] The one line with Undo shown when the active profile followed the game's mode.
/// </summary>
/// <remarks>
/// The switch itself happens in <see cref="ProfileModeFollower"/>, off the UI thread, as the log is
/// read; this only words it. Undo goes back to the profile that was active before; with no profile
/// in the game's mode the button creates one instead, named after the mode.
/// </remarks>
public sealed class ProfileFollowViewModel : BindableViewModel, IDisposable
{
    private readonly ProfileModeFollower _follower;
    private readonly Action<Action> _post;
    private readonly Action<ProfileFollowNotice?> _onChanged;
    private ProfileFollowNotice? _notice;

    public ProfileFollowViewModel(ProfileModeFollower follower, Action<Action>? post = null)
    {
        _follower = follower ?? throw new ArgumentNullException(nameof(follower));
        _post = post ?? (static action => action());
        ActionCommand = new AsyncDelegateCommand(RunActionAsync);
        DismissCommand = new DelegateCommand(_follower.Dismiss);
        _onChanged = notice => _post(() => Show(notice));
        _follower.NoticeChanged += _onChanged;
        Show(_follower.Notice);
    }

    public bool IsVisible => _notice is not null;

    /// <summary>"The game is in Seasonal. Switched to Main."</summary>
    public string Line => _notice switch
    {
        { Kind: ProfileFollowKind.Switched } notice => SetupText.ProfileFollowSwitched(GameModeLabel.Of(notice.GameMode), notice.ProfileName ?? string.Empty),
        { } notice => SetupText.ProfileFollowNoProfile(GameModeLabel.Of(notice.GameMode)),
        null => string.Empty,
    };

    /// <summary>Undo after a switch; Create when no profile is in the game's mode.</summary>
    public string ActionLabel => _notice?.Kind == ProfileFollowKind.NoProfile ? SetupText.ProfileFollowCreate : SetupText.ProfileFollowUndo;

    /// <summary>A switch made from nothing (no profile was active) has nothing to undo to.</summary>
    public bool HasAction => _notice is { Kind: ProfileFollowKind.NoProfile } or { PreviousProfileId: not null };

    public string DismissName => SetupText.ProfileFollowDismiss;

    public ICommand ActionCommand { get; }

    public ICommand DismissCommand { get; }

    public void Dispose() => _follower.NoticeChanged -= _onChanged;

    private async Task RunActionAsync()
    {
        try
        {
            if (_notice is { Kind: ProfileFollowKind.NoProfile } notice)
            {
                await _follower.CreateForGameModeAsync(GameModeLabel.Of(notice.GameMode), CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await _follower.UndoAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            // The profile list changed underneath (a name clash, the profile archived meanwhile): the
            // line goes, and Setup's profile list still shows every profile to pick from by hand.
            _follower.Dismiss();
        }
    }

    private void Show(ProfileFollowNotice? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(HasAction));
    }
}
