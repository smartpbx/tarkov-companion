using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.StashScan;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// [#279] Whether the page on screen has finished reading its data, for the <c>page</c> gallery
/// scene. Windows run 36032134540 photographed Intel › Ammo three times while it still said
/// "Reading the ammunition table…": the gallery had waited for a responsive window, not a loaded
/// page. A page not listed here has nothing asynchronous to wait for.
/// </summary>
internal static class GalleryPageReadiness
{
    public static (bool Loaded, string What) Of(V2ShellViewModel shell) => Of(
        shell.WorkspaceContent,
        shell.ShowsIntel && shell.IntelItem.Length > 0,
        shell.IntelHasAnswer,
        shell.ShowsSetupWorkspace,
        shell.SurfaceIsLoading);

    public static (bool Loaded, string What) Of(
        object? workspace,
        bool showsItem,
        bool itemAnswered,
        bool showsSetup,
        bool dataStateLoading)
    {
        if (showsItem && !itemAnswered)
        {
            return (false, "the item's intel");
        }

        return workspace switch
        {
            AmmoWorkspaceViewModel ammo => (ammo.HasLoaded, "the ammunition table"),
            CraftsBartersWorkspaceViewModel crafts => (crafts.HasLoaded, "crafts and barters"),
            // [#279, #872] Each is Loading until its first read ends, in success or failure.
            StashScanWorkspaceViewModel stash => (!stash.IsLoading, "the stash snapshots"),
            PlanWorkspaceViewModel plan => (!plan.IsLoading, "the quest board"),
            DebriefWorkspaceViewModel debrief => (!debrief.IsLoading, "the raid history"),
            _ when showsSetup => (!dataStateLoading, "the data state"),
            _ when showsItem => (true, "the item's intel"),
            _ => (true, "a page with nothing to load"),
        };
    }
}
