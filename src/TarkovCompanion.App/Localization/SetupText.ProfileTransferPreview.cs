using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>[#314] The part of a profile an import preview line names.</summary>
    public static string ProfileArea(ProfileBundleArea area) => PhraseText.Say(area);

    /// <summary>[#314] Why the file cannot go into the active profile, or null when it can.</summary>
    public static string? ProfileTransferRefusal(ProfileBundleRefusal? refusal) => refusal switch
    {
        null => null,
        { TargetName: null } => UiText.Get("Setup.ProfileTransfer.NoActiveProfile"),
        _ => UiText.Format(
            "Setup.ProfileTransfer.OtherMode",
            RefusalMode(refusal.FileMode),
            refusal.TargetName,
            RefusalMode(refusal.TargetMode ?? ProfileGameMode.Unknown)),
    };

    private static string RefusalMode(ProfileGameMode mode) => mode == ProfileGameMode.Unknown
        ? UiText.Get("Setup.ProfileTransfer.UnknownMode")
        : GameModeLabel.Of(mode);
}
