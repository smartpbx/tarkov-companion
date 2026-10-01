namespace TarkovCompanion.App.Localization;

/// <summary>[#983] The Ping tool and the line that says where each ping went.</summary>
public static partial class RaidText
{
    public static string ModePing => UiText.Get("Raid.Mode.Ping");
    public static string PingModeHint => UiText.Get("Raid.Ping.ModeHint");
    public static string PingHint => UiText.Get("Raid.Ping.Hint");
    public static string PingNotSaved => UiText.Get("Raid.Ping.NotSaved");
    public static string PingNoMap => UiText.Get("Raid.Ping.NoMap");
    public static string PingSending(string kind) => UiText.Format("Raid.Ping.Sending", kind);
    public static string PingSent(string kind) => UiText.Format("Raid.Ping.Sent", kind);
    public static string PingRelayOffline(string kind) => UiText.Format("Raid.Ping.RelayOffline", kind);
    public static string PingNoSquad(string kind) => UiText.Format("Raid.Ping.NoSquad", kind);
    public static string PingSharingOff(string kind) => UiText.Format("Raid.Ping.SharingOff", kind);
    public static string PingJustMe(string kind) => UiText.Format("Raid.Ping.JustMe", kind);
    public static string PingUnplaceable(string kind) => UiText.Format("Raid.Ping.Unplaceable", kind);
}
