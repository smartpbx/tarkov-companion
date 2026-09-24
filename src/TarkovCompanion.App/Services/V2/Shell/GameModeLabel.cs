using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>The one name each game mode is shown by.</summary>
/// <remarks>
/// The top bar printed the legacy enum's own name, "Regular", while Setup's profiles said "PvP"
/// for the same profile; the legacy mode is the active profile's mode translated
/// (ProfileScopedPlayerProfileService), so both are named here from the profile's words.
/// </remarks>
public static class GameModeLabel
{
    public static string Of(ProfileGameMode mode) => mode switch
    {
        // [#314] From the string tables: Debrief shows these beside every raid.
        ProfileGameMode.Pvp => UiText.Get("Common.Mode.Pvp"),
        ProfileGameMode.Pve => UiText.Get("Common.Mode.Pve"),
        ProfileGameMode.Seasonal => UiText.Get("Common.Mode.Seasonal"),
        _ => SetupText.ProfilesModeUnset,
    };

    public static string Of(GameMode mode) => mode switch
    {
        GameMode.Pve => Of(ProfileGameMode.Pve),
        GameMode.PvpSeason => Of(ProfileGameMode.Seasonal),
        _ => Of(ProfileGameMode.Pvp),
    };

    /// <summary>A mode stored as text (raid history keeps the legacy enum's name, "Regular").</summary>
    public static string OfStored(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? string.Empty
        : Enum.TryParse<GameMode>(stored, ignoreCase: true, out var legacy) && Enum.IsDefined(legacy) ? Of(legacy)
        : Enum.TryParse<ProfileGameMode>(stored, ignoreCase: true, out var profile) && Enum.IsDefined(profile) ? Of(profile)
        : stored;
}
