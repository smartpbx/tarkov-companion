namespace TarkovCompanion.App.Localization;

/// <summary>[#712 decision 4] The one line shown when the profile followed the game's mode.</summary>
public static partial class SetupText
{
    public static string ProfileFollowSwitched(string mode, string profile) => UiText.Format("Setup.Profiles.Follow.Switched", mode, profile);

    public static string ProfileFollowNoProfile(string mode) => UiText.Format("Setup.Profiles.Follow.NoProfile", mode);

    public static string ProfileFollowUndo => UiText.Get("Setup.Profiles.Follow.Undo");

    public static string ProfileFollowCreate => UiText.Get("Setup.Profiles.Follow.Create");

    public static string ProfileFollowDismiss => UiText.Get("Setup.Profiles.Follow.Dismiss");
}
