using TarkovCompanion.Application.Services.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

public sealed partial class RaidCockpitViewModel
{
    private SituationService? _situation;

    /// <summary>[#712 0-2] Hands the situation the objective route this map follows, for NEXT (ADR 0022).</summary>
    internal void AttachSituation(SituationService situation) =>
        _situation = situation ?? throw new System.ArgumentNullException(nameof(situation));

    /// <summary>The route as the planner ordered it, or none once it is closed or off this map.</summary>
    private void PublishSituationPlan(ObjectiveRouteFollowerResult? result) =>
        _situation?.SetPlan(result is null
            ? null
            : new SituationPlan(result.LocationId, result.Origin.Label, result.Route.Steps, _timeProvider.GetUtcNow()));
}
