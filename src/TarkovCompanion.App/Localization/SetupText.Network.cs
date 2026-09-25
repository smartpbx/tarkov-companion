using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.Localization;

/// <summary>[#292] Setup › Data &amp; Privacy's Local only switch and the per-service rows.</summary>
public static partial class SetupText
{
    public static string NetworkHeading => UiText.Get("Setup.Network.Heading");
    public static string NetworkOn => UiText.Get("Setup.Network.On");
    public static string NetworkOff => UiText.Get("Setup.Network.Off");
    public static string NetworkLocalOnlyTitle => UiText.Get("Setup.Network.LocalOnly.Title");
    public static string NetworkLocalOnlyLine => UiText.Get("Setup.Network.LocalOnly.Line");
    public static string NetworkLocalOnlyForced => UiText.Get("Setup.Network.LocalOnly.Forced");
    public static string NetworkLocalOnlyReset => UiText.Get("Setup.Network.LocalOnly.Reset");
    public static string NetworkUpdateOff => UiText.Get("Setup.Network.UpdateOff");
    public static string NetworkTrackerOff => UiText.Get("Setup.Network.TrackerOff");

    public static string NetworkServiceTitle(NetworkService service) => UiText.Get($"Setup.Network.{service}.Title");

    /// <summary>One line on what this service sends off the PC.</summary>
    public static string NetworkServiceLine(NetworkService service) => UiText.Get($"Setup.Network.{service}.Line");

    /// <summary>"On", "Local only · off" or "Off": the state a row, and a client that did not connect, shows.</summary>
    public static string NetworkState(NetworkVerdict verdict) => verdict switch
    {
        NetworkVerdict.LocalOnly => UiText.Get("Setup.Network.StateLocalOnly"),
        NetworkVerdict.SwitchedOff => UiText.Get("Setup.Network.StateSwitchedOff"),
        _ => UiText.Get("Setup.Network.StateOn"),
    };
}
