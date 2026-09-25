namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>
    /// [#902] Setup's read-only line for the three sharing switches, named as Team names them.
    /// </summary>
    public static string SquadSharingSummary(bool sharing, bool loadout, bool quests) =>
        UiText.Format("Setup.SquadSharing.Summary", OnOff(sharing), OnOff(loadout), OnOff(quests));

    private static string OnOff(bool on) => UiText.Get(on ? "Setup.SquadSharing.On" : "Setup.SquadSharing.Off");
}
