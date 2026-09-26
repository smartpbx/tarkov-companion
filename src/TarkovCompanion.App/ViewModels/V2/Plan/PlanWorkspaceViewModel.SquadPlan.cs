namespace TarkovCompanion.App.ViewModels.V2.Plan;

public sealed partial class PlanWorkspaceViewModel
{
    /// <summary>
    /// [#712 T7] Team's "Put on the Raid map": Plan's own "Open in Raid" for a quest map id, reading
    /// the board first when Plan has not been opened yet this session.
    /// </summary>
    internal async Task OpenMapInRaidAsync(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (GroupFor(mapId) is null)
        {
            await LoadAsync().ConfigureAwait(true);
        }

        // As on Plan, where the map's card is selected before its "Open in Raid": the selected
        // group is the one whose visit order Plan keeps feeding to the Raid route.
        if (GroupFor(mapId) is { } group)
        {
            SelectedGroup = group;
        }

        await OpenInRaidAsync(mapId).ConfigureAwait(true);
    }
}
