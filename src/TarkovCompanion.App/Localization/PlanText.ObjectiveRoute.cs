using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.Localization;

/// <summary>The objective route's words (#314): the planner gives each step a reason code.</summary>
public static partial class PlanText
{
    /// <summary>"Nearest unvisited objective from player position", "2-opt moved it from step 3…"…</summary>
    public static string ObjectiveRouteReason(ObjectiveRouteReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return reason.Kind switch
        {
            ObjectiveRouteReasonKind.NearestFrom => UiText.Format("Plan.ObjectiveRoute.NearestFrom", reason.Previous),
            ObjectiveRouteReasonKind.MovedByTwoOpt => UiText.Format("Plan.ObjectiveRoute.MovedByTwoOpt", reason.Step),
            ObjectiveRouteReasonKind.KeptByTwoOpt => UiText.Format("Plan.ObjectiveRoute.KeptByTwoOpt", reason.Step),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason.Kind, "No words for this route reason."),
        };
    }

    /// <summary>The words the route's layer, line and steps are drawn with, in the interface language.</summary>
    public static ObjectiveRouteSceneWords ObjectiveRouteWords() => new(
        UiText.Get("Plan.ObjectiveRoute.Layer"),
        UiText.Get("Plan.ObjectiveRoute.LineTitle"),
        UiText.Get("Plan.ObjectiveRoute.LineDetail"),
        ObjectiveRouteReason);
}
