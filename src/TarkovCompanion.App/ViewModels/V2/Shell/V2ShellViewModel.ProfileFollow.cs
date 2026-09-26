namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>[#712 decision 4] The shell shows the "profile followed the game" line over every page.</summary>
public sealed partial class V2ShellViewModel
{
    /// <summary>Null where Setup's profiles or the follower are not composed (lightweight shell tests).</summary>
    public ProfileFollowViewModel? ProfileFollow => SetupWorkspace?.Profiles?.Follow;
}
