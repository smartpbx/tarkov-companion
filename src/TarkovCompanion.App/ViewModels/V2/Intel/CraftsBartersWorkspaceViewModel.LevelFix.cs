using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>
/// [#902 P10] "I can do this now" on a new profile emptied the list with "No crafts or barters
/// match." and a count of unknown rows, and nothing said that the missing input is the player's
/// own trader loyalty (Plan › Level and loyalty) and station levels (Hideout). The empty state now
/// says so and links to each page that is actually missing a level.
/// </summary>
public sealed partial class CraftsBartersWorkspaceViewModel
{
    private Action<V2RouteId>? _navigate;
    private ICommand? _setTraderLevels;
    private ICommand? _setHideoutLevels;

    /// <summary>Attached by the shell after construction, as the Team page's links are.</summary>
    public void AttachNavigation(Action<V2RouteId> navigate)
    {
        _navigate = navigate;
        OnPropertyChanged(nameof(ShowsSetTraderLevels));
        OnPropertyChanged(nameof(ShowsSetHideoutLevels));
    }

    /// <summary>A barter left out because its trader's loyalty is not on record.</summary>
    public bool ShowsSetTraderLevels => ShowsLevelFix(IntelTradeKind.Barter);

    /// <summary>A craft left out because its station's level is not on record.</summary>
    public bool ShowsSetHideoutLevels => ShowsLevelFix(IntelTradeKind.Craft);

    public ICommand SetTraderLevelsCommand => _setTraderLevels ??= new DelegateCommand(() => _navigate?.Invoke(V2Routes.Plan));

    public ICommand SetHideoutLevelsCommand => _setHideoutLevels ??= new DelegateCommand(() => _navigate?.Invoke(V2Routes.Hideout));

    private bool ShowsLevelFix(IntelTradeKind kind) =>
        _navigate is not null
        && ShowsEmpty
        && _readyNowOnly
        && SearchMatches().Any(row => row.Kind == kind && row.Readiness == IntelTradeReadiness.Unknown);
}
