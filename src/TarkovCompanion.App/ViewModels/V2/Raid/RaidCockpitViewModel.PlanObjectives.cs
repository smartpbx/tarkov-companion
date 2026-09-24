using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#802] Plan's "Open in Raid" and "Show in Raid": the objectives layer on, one objective selected.</summary>
public sealed partial class RaidCockpitViewModel
{
    private string? _pendingPlanObjectiveId;
    private string? _pendingPlanObjectiveLocationId;
    private bool _revealQuestLayerPending;

    /// <summary>
    /// Turns the objectives layer on where it was off, through the same toggles the Layers menu
    /// uses, and selects <paramref name="objectiveId"/> as soon as the map has it.
    /// </summary>
    /// <remarks>
    /// The objective usually is not on the map yet: the quest was put on it a moment ago and the
    /// quest layer is still being read, or the map is still loading. So the selection waits for the
    /// rebuild that brings it, and is dropped if the player moves to another map first.
    /// </remarks>
    internal void RevealQuestObjectives(string? objectiveId)
    {
        if (_map.RenderModel?.Overlays.FirstOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives) is { IsVisible: false })
        {
            _map.ToggleOverlay(MapOverlayKind.QuestObjectives, true);
        }

        _revealQuestLayerPending = true;
        _pendingPlanObjectiveId = objectiveId;
        _pendingPlanObjectiveLocationId = _map.SelectedLocation?.Id;
        ApplyPendingPlanObjective();
        _rebuildRequest.Request();
    }

    /// <summary>Called at the top of every objective list refresh, which follows every scene rebuild.</summary>
    private void ApplyPendingPlanObjective()
    {
        if (_revealQuestLayerPending && Renderer is { } renderer)
        {
            _revealQuestLayerPending = false;
            if (!QuestPinsShown())
            {
                renderer.SetLayerVisibility(MapSceneAssembler.IdFor(MapOverlayKind.QuestObjectives), true);
            }
        }

        if (_pendingPlanObjectiveId is not { } objectiveId)
        {
            return;
        }

        if (!string.Equals(_map.SelectedLocation?.Id, _pendingPlanObjectiveLocationId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingPlanObjectiveId = null;
            return;
        }

        if (Renderer is not null && _questScene.Entries.Any(entry => entry.ObjectiveId == objectiveId))
        {
            _pendingPlanObjectiveId = null;
            SelectObjective(objectiveId);
        }
    }
}
