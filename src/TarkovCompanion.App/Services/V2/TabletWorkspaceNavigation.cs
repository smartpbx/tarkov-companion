using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2;

/// <summary>Moves the V2 shell to one of the five workspaces a Control-mode tablet may request.</summary>
/// <remarks>
/// [#407] The paired command reducer already accepted every <see cref="WorkspaceKind"/>, but the
/// desktop handler only read Raid's map viewport. Keeping this translation as a closed switch is
/// intentional: a new protocol value must be reviewed and given a real V2 home before a tablet
/// can navigate to it, rather than falling through to an arbitrary or stale route.
/// </remarks>
internal sealed class TabletWorkspaceNavigation(
    Func<V2RouteId> currentRoute,
    Action<V2RouteId> navigate)
{
    private readonly Func<V2RouteId> _currentRoute = currentRoute ?? throw new ArgumentNullException(nameof(currentRoute));
    private readonly Action<V2RouteId> _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));

    public bool TryNavigate(WorkspaceKind workspace)
    {
        if (!TryRoute(workspace, out var route))
        {
            return false;
        }

        if (_currentRoute() != route)
        {
            _navigate(route);
        }

        return true;
    }

    internal static bool TryRoute(WorkspaceKind workspace, out V2RouteId route)
    {
        route = workspace switch
        {
            WorkspaceKind.Raid => V2Routes.Raid,
            WorkspaceKind.Intel => V2Routes.Items,
            WorkspaceKind.Plan => V2Routes.Plan,
            WorkspaceKind.Team => V2Routes.Team,
            WorkspaceKind.Debrief => V2Routes.Debrief,
            _ => default,
        };
        return route != default;
    }
}
