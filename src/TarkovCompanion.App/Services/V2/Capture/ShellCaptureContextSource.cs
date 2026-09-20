using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// The capture context as the V2 shell knows it, for every way a frame can arrive: an armed
/// request, the screenshot watcher, and a picture the player pasted, dropped or picked.
/// </summary>
/// <remarks>
/// One place builds it so the three cannot disagree. The router is bound by the capture bridge
/// once the shell exists rather than injected, because the shell is built on the main window's
/// view model, which is built on the raid observer, which asks this for a context: injected, that
/// is a cycle. Until it is bound (and under the V1 interface, where it never is) the answer is
/// what the runtime alone knows, which is what the watcher used to submit.
/// </remarks>
public sealed class ShellCaptureContextSource(
    IRuntimeStateStore runtime,
    IProfileRuntimeContextService? profiles = null) : ICaptureContextSource
{
    private readonly IRuntimeStateStore _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private V2ShellRouter? _router;

    public void Bind(V2ShellRouter router) =>
        Volatile.Write(ref _router, router ?? throw new ArgumentNullException(nameof(router)));

    public CaptureContextMetadata Describe(string? initiatingDevice = null)
    {
        var router = Volatile.Read(ref _router);
        var navigation = router?.Context;
        var active = profiles?.Current.ActiveProfile;
        return new(
            activeWorkspace: navigation?.WorkspaceId ?? router?.Current.Location.Route.Value,
            // The profile's stable id, never its display name: a capture context that named
            // the player would put their handle into every report.
            activeProfile: active?.Context.Identity.ProfileId.ToString("D") ?? navigation?.ProfileId,
            activeMap: _runtime.Current.Raid.MapId ?? navigation?.MapId,
            activePlan: navigation?.PlanId,
            selectedEntity: navigation?.SelectedEntity ?? router?.Current.SelectedEntity,
            priorScan: navigation?.PriorScan,
            initiatingDevice: initiatingDevice ?? V2NavigationContext.ThisDesktop,
            profileContext: active?.Context);
    }
}
