using System.Globalization;
using TarkovCompanion.Application.Services.Strategy.Prior;

namespace TarkovCompanion.App.Localization;

/// <summary>A suggested route's words (#314): the planner gives the numbers and a reason code.</summary>
public static partial class RaidText
{
    public static string RouteMinutes(int low, int high) => UiText.Format("Raid.Route.Minutes", low, high);

    /// <summary>One reason the lower-contact route is suggested; every <see cref="TrafficRouteReasonKind"/> has words.</summary>
    public static string RouteReason(TrafficRouteReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var culture = CultureInfo.CurrentCulture;
        string Share(double value) => value.ToString("P0", culture);
        string Metres(double value) => value.ToString("0", culture);
        return reason.Kind switch
        {
            TrafficRouteReasonKind.DirectIsLowest => UiText.Get("Raid.Route.DirectIsLowest"),
            TrafficRouteReasonKind.LengthAndContact => UiText.Format("Raid.Route.LengthAndContact", Metres(reason.Metres), Share(reason.Share)),
            TrafficRouteReasonKind.AvoidsPeak => UiText.Format(
                "Raid.Route.Avoids",
                reason.Place is { } place ? UiText.Format("Raid.Route.Convergence", PlaceName(place)) : UiText.Get("Raid.Route.BusiestStretch"),
                Share(reason.OtherShare),
                Share(reason.Share)),
            TrafficRouteReasonKind.ContactAgainstDirect => UiText.Format("Raid.Route.ContactAgainstDirect", Share(reason.Share), Share(reason.OtherShare)),
            TrafficRouteReasonKind.Longer => UiText.Format(
                "Raid.Route.Longer",
                Metres(Math.Max(0, reason.Metres - reason.OtherMetres)),
                Metres(reason.Metres),
                Metres(reason.OtherMetres)),
            TrafficRouteReasonKind.StillCrosses => UiText.Format(
                "Raid.Route.StillCrosses",
                reason.Place is { } near ? UiText.Format("Raid.Route.Near", PlaceName(near)) : UiText.Get("Raid.Route.AtItsBusiest"),
                Share(reason.Share)),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason.Kind, "No words for this route reason."),
        };
    }

    /// <summary>A hotspot's place in a reason; the planner gives the empty string for one with no name near it.</summary>
    private static string PlaceName(string place) => place.Length == 0 ? UiText.Get("Raid.Traffic.UnnamedArea") : place;
}

/// <summary>"~3–5 min" for a route, in the interface language.</summary>
public static class TrafficRouteText
{
    public static string MinutesLabel(this TrafficRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return RaidText.RouteMinutes(route.MinutesLow, route.MinutesHigh);
    }
}
