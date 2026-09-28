using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.TestChecklist;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public sealed partial class V2ShellViewModel
{
    /// <summary>
    /// Builds Setup › Test checklist when the flag offers it: the shipped items, this PC's results,
    /// and Go there through the same address navigation as the palette's <c>#/raid</c>.
    /// </summary>
    private void AttachTestChecklist(AppDataPaths paths, TimeProvider? clock)
    {
        if (SetupWorkspace is null || !V2SetupWorkspaceViewModel.OffersTestChecklist)
        {
            return;
        }

        SetupWorkspace.AttachTestChecklist(new TestChecklistViewModel(
            TestChecklistAssets.Load(),
            TestChecklistResultsStore.In(paths),
            AppBuildIdentity.Current.Version,
            clock,
            address =>
            {
                var result = Router.NavigateToAddress(address);
                Act(result);
                return result.Succeeded;
            }));
    }
}
