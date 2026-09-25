using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// [#279] The gallery's non-happy states: a page with nothing in it, a page still loading, a page
/// on old data with its relay gone, and a page whose load failed. Every shot before these was a
/// page on a runner that had just downloaded everything, so none of the messages a player reads
/// when something is missing had ever been photographed on Windows.
/// </summary>
/// <remarks>
/// Each state is reached through the path a player's machine takes to it, not drawn by hand:
/// <list type="bullet">
/// <item><c>empty</c>: the gallery wipes the database and config and launches offline, so the app
/// opens a new database with no catalog, exactly as a first launch without internet does.</item>
/// <item><c>loading</c>: <see cref="LoadHold"/> holds the page's own load at its first line.</item>
/// <item><c>degraded</c>: launched offline over the downloaded catalog (the real offline switch),
/// with the published data stamped six days old, and the relay's own "last heard … unreachable"
/// line on a squad. The two stamps are the only synthetic parts, as the squad scene's are.</item>
/// <item><c>error</c>: <see cref="LoadFaultInjection"/> makes the page's own load throw inside its
/// own try, so the catch, the message and the Retry are the real ones.</item>
/// </list>
/// Developer mode with <c>--gallery-scene</c> only, like every other scene.
/// </remarks>
internal sealed class GalleryStateScene(IServiceProvider services, MainWindowViewModel main, GallerySceneKind scene)
{
    /// <summary>The page loads the loading and error scenes hold or fail.</summary>
    public static IReadOnlyList<string> Surfaces { get; } = ["plan", "debrief", "stash"];

    /// <summary>How old the degraded scene says the catalog is.</summary>
    public static TimeSpan DegradedAge { get; } = TimeSpan.FromDays(6);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(150);

    public static bool IsState(GallerySceneKind scene) =>
        scene is GallerySceneKind.Empty or GallerySceneKind.Loading or GallerySceneKind.Degraded or GallerySceneKind.Error;

    /// <summary>Before anything loads: the loading scene holds the loads, the error scene fails them.</summary>
    public static void Arm(GallerySceneKind scene)
    {
        if (scene == GallerySceneKind.Loading)
        {
            LoadHold.Hold(Surfaces);
        }
        else if (scene == GallerySceneKind.Error)
        {
            LoadFaultInjection.Inject(Surfaces);
        }
    }

    /// <summary>The held or failed load behind the workspace on screen, or null for a page without one.</summary>
    public static string? SurfaceOf(object? workspace) => workspace switch
    {
        PlanWorkspaceViewModel => "plan",
        DebriefWorkspaceViewModel => "debrief",
        StashScanWorkspaceViewModel => "stash",
        _ => null,
    };

    /// <summary>The published data, stamped <see cref="DegradedAge"/> old; unchanged if it already is that old.</summary>
    public static RuntimeDataState Aged(RuntimeDataState data, DateTimeOffset now)
    {
        var stamp = now - DegradedAge;
        return data.UpdatedUtc is { } updated && updated <= stamp ? data : data with { UpdatedUtc = stamp };
    }

    /// <summary>
    /// What the group session publishes a minute and a half after the relay stopped answering:
    /// the squad it last heard, marked stale, with the relay's own "unreachable" phrase.
    /// </summary>
    public static GroupSnapshot LostRelay(DateTimeOffset now)
    {
        var quiet = TimeSpan.FromSeconds(95);
        GroupMemberView Mate(string name, RaidLifecycleState state) =>
            new(name, "customs", state, "PMC", null, null, null, [], []) { Since = quiet };
        var unreachable = new Phrase(GroupStatus.ServerUnreachable, "No such host is known. (relay.invalid:443)");
        return new GroupSnapshot(true, [Mate("Geo", RaidLifecycleState.InRaid), Mate("Riley", RaidLifecycleState.Menu)], string.Empty, now)
        {
            StaleSince = now - quiet,
        }.Saying(new Phrase(GroupStatus.LastHeard, unreachable, GroupSessionService.Ago(quiet)));
    }

    public async Task RunAsync(GalleryReadiness readiness, CancellationToken cancellationToken)
    {
        var name = scene.ToString().ToLowerInvariant();
        try
        {
            if (main.PreviewShell is not { } shell)
            {
                throw new InvalidOperationException("no V2 shell");
            }

            var detail = scene switch
            {
                GallerySceneKind.Empty => await EmptyAsync(shell, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Loading => await LoadingAsync(shell, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Degraded => await DegradedAsync(shell, cancellationToken).ConfigureAwait(true),
                _ => await GallerySceneRunner.PageLoadedAsync(shell, cancellationToken).ConfigureAwait(true),
            };
            readiness.AfterStep = (condition, token) => Dispatcher.UIThread.InvokeAsync(() => AfterStepAsync(shell, condition, token));
            readiness.Ready($"{name} {shell.CurrentAddress}: {detail}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readiness.Failed($"{name} {main.PreviewShell?.CurrentAddress}: {exception.Message}");
        }
    }

    private async Task<string> EmptyAsync(V2ShellViewModel shell, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IRuntimeStateStore>();
        await GallerySceneRunner.WaitForAsync(
            () => store.Current.DatabaseReady && !shell.SurfaceIsLoading,
            Timeout,
            "new database opened",
            cancellationToken).ConfigureAwait(true);
        var loaded = await GallerySceneRunner.PageLoadedAsync(shell, cancellationToken).ConfigureAwait(true);
        return $"{loaded}, {store.Current.Data.ItemCount} items, {shell.Surface.Kind}";
    }

    private static async Task<string> LoadingAsync(V2ShellViewModel shell, CancellationToken cancellationToken)
    {
        var surface = SurfaceOf(shell.WorkspaceContent)
            ?? throw new InvalidOperationException("this page has no load the scene can hold");
        await GallerySceneRunner.WaitForAsync(() => LoadHold.IsWaiting(surface), Timeout, $"the {surface} load at its hold", cancellationToken)
            .ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        return $"the {surface} load is held";
    }

    private async Task<string> DegradedAsync(V2ShellViewModel shell, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IRuntimeStateStore>();
        var clock = services.GetRequiredService<TimeProvider>();
        await GallerySceneRunner.WaitForAsync(
            () => store.Current is { DatabaseReady: true, Data: { ItemCount: > 0, Availability: not DataAvailability.Refreshing } },
            Timeout,
            "the cached catalog",
            cancellationToken).ConfigureAwait(true);

        // No relay on a verification machine: the session's "not sharing" would replace the lost one.
        await services.GetRequiredService<GroupSessionService>().DisposeAsync().ConfigureAwait(true);
        void Apply() => store.Update(snapshot => snapshot with
        {
            Data = Aged(snapshot.Data, clock.GetUtcNow()),
            Group = snapshot.Group.StaleSince is null ? LostRelay(clock.GetUtcNow()) : snapshot.Group,
        });
        Apply();
        // A later publication (the offline poll, a projection) would put today's stamp back.
        store.Changed += (_, _) =>
        {
            var current = store.Current;
            if (Aged(current.Data, clock.GetUtcNow()) != current.Data || current.Group.StaleSince is null)
            {
                Dispatcher.UIThread.Post(Apply);
            }
        };
        // Only pages drawn from game data turn the banner offline; Team and Setup show it in their own lines.
        await GallerySceneRunner.WaitForAsync(
            () => store.Current.Data.UpdatedUtc <= clock.GetUtcNow() - DegradedAge + TimeSpan.FromMinutes(1) &&
                store.Current.Group.StaleSince is not null,
            Timeout,
            "old data and a lost relay published",
            cancellationToken).ConfigureAwait(true);
        var map = string.Empty;
        if (shell.ShowsRaidCockpit)
        {
            // Offline over a cached catalog the map should still draw. Waited for, not required:
            // the picture then says which it was, and the detail names it.
            var raid = services.GetRequiredService<RaidCockpitViewModel>();
            try
            {
                await GallerySceneRunner.WaitForAsync(
                    () => raid.Renderer is { BackgroundImage: not null } && raid.MapExtracts.Count > 0,
                    TimeSpan.FromSeconds(60),
                    "map drawn offline",
                    cancellationToken).ConfigureAwait(true);
                map = ", map drawn";
            }
            catch (InvalidOperationException exception)
            {
                map = $", {exception.Message}";
            }
        }

        var loaded = await GallerySceneRunner.PageLoadedAsync(shell, cancellationToken).ConfigureAwait(true);
        return $"{loaded}, {store.Current.Data.ItemCount} items, {shell.Surface.Kind}{map}";
    }

    /// <summary>
    /// "release": the held loads go on, or the failing ones stop failing, and the answer comes once
    /// the page has read its data, so the gallery can show the state does not last forever.
    /// "settled": the page has read its data.
    /// </summary>
    private static async Task<string> AfterStepAsync(V2ShellViewModel shell, string condition, CancellationToken cancellationToken)
    {
        switch (condition)
        {
            case "release":
                LoadHold.ReleaseAll();
                LoadFaultInjection.Clear();
                await GallerySceneRunner.WaitForAsync(() => !LoadHold.AnyWaiting, Timeout, "held loads released", cancellationToken)
                    .ConfigureAwait(true);
                break;
            case "settled":
                break;
            default:
                throw new InvalidOperationException($"no gallery condition named '{condition}'");
        }

        return $"{condition}: " + await GallerySceneRunner.PageLoadedAsync(shell, cancellationToken).ConfigureAwait(true);
    }
}
