using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>The eight states every workspace has in the #265 state matrix.</summary>
public enum V2SurfaceStateKind
{
    Ready = 1,
    Loading,
    Empty,
    Offline,
    Stale,
    Partial,
    Denied,
    Failed,
}

public enum V2Announcement
{
    None = 1,
    Polite,
    Assertive,
}

/// <summary>
/// How one state looks, reads and behaves, independent of which page it is on.
/// </summary>
/// <param name="Kind">The state.</param>
/// <param name="WordingKey">The shell's word for it.</param>
/// <param name="DesignSystemKey">The #266 wording key for the same state, where #266 defines one.</param>
/// <param name="StateClass">The #266 primitive class that draws it.</param>
/// <param name="Glyph">The manifest's redundant glyph cue, so no state depends on colour alone.</param>
/// <param name="Border">The manifest's redundant border cue.</param>
/// <param name="Background">How a background transition is announced: once, politely, or not at all.</param>
/// <param name="PlayerAction">How it is announced when the player's own action caused it.</param>
/// <param name="FocusesOnPlayerAction">Whether a player-caused transition moves focus to the recovery.</param>
public sealed record V2SurfaceStatePolicy(
    V2SurfaceStateKind Kind,
    string WordingKey,
    string? DesignSystemKey,
    string StateClass,
    string Glyph,
    string Border,
    V2Announcement Background,
    V2Announcement PlayerAction,
    bool FocusesOnPlayerAction);

/// <summary>A recovery a state offers. A navigation never claims to have resolved anything.</summary>
public sealed record V2RecoveryAction(string Id, string LabelKey, V2RouteId? Route);

/// <summary>A resolved state: its policy, the evidence for it, and what still works.</summary>
public sealed record V2SurfaceState(
    V2SurfaceStatePolicy Policy,
    string Detail,
    string RemainderKey,
    IReadOnlyList<V2RecoveryAction> Recovery)
{
    public V2SurfaceStateKind Kind => Policy.Kind;

    /// <summary>What makes this the same problem when it is said again; the sentence unless one is given.</summary>
    /// <remarks>
    /// Two of these sentences carry an age ("synced 12 min ago"), which changes every minute while
    /// the problem does not. Remembering a dismissed banner by its sentence brought it back each
    /// time the age ticked over.
    /// </remarks>
    public string Identity { get; init; } = string.Empty;

    public string StableIdentity => string.Concat(Kind.ToString(), "\u001f", Identity.Length > 0 ? Identity : Detail);
}

public static class V2SurfaceStatePolicies
{
    /// <summary>
    /// One row per state.
    /// </summary>
    /// <remarks>
    /// Rule 2 of the state matrix is that no state is shown as another, so each row's word and glyph
    /// together are unique and a test holds them to it. Colour is a third cue on top, never the
    /// only one. Loading and Empty have no #266 manifest entry yet; their cues are the shell's own
    /// until the manifest grows them.
    /// </remarks>
    public static IReadOnlyList<V2SurfaceStatePolicy> All { get; } =
    [
        new(V2SurfaceStateKind.Ready, "V2.Shell.State.Ready", "V2.String.Availability.Ready", "v2-state-ready", "check", "solid",
            V2Announcement.None, V2Announcement.Polite, false),
        new(V2SurfaceStateKind.Loading, "V2.Shell.State.Loading", null, "v2-state-unknown", "hourglass", "dashed",
            V2Announcement.Polite, V2Announcement.Polite, false),
        new(V2SurfaceStateKind.Empty, "V2.Shell.State.Empty", null, "v2-state-unknown", "circle", "dotted",
            V2Announcement.None, V2Announcement.None, false),
        new(V2SurfaceStateKind.Offline, "V2.Shell.State.Offline", "V2.String.Availability.Offline", "v2-state-offline", "link-off", "dashed",
            V2Announcement.Polite, V2Announcement.Polite, false),
        new(V2SurfaceStateKind.Stale, "V2.Shell.State.Stale", "V2.String.Freshness.Stale", "v2-state-warning", "clock", "dotted",
            V2Announcement.Polite, V2Announcement.Polite, false),
        new(V2SurfaceStateKind.Partial, "V2.Shell.State.Partial", "V2.String.Completeness.Partial", "v2-state-warning", "half-circle", "double",
            V2Announcement.None, V2Announcement.None, false),
        new(V2SurfaceStateKind.Denied, "V2.Shell.State.Denied", "V2.String.Availability.Denied", "v2-state-error", "lock", "solid",
            V2Announcement.Polite, V2Announcement.Assertive, false),
        new(V2SurfaceStateKind.Failed, "V2.Shell.State.Failed", "V2.String.Availability.Failed", "v2-state-error", "cross", "solid",
            V2Announcement.Polite, V2Announcement.Assertive, true),
    ];

    public static V2SurfaceStatePolicy For(V2SurfaceStateKind kind) =>
        All.FirstOrDefault(policy => policy.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined surface state.");
}

/// <summary>
/// Reads a page's state from facts the runtime already has.
/// </summary>
/// <remarks>
/// Only what can be derived honestly. There is no runtime fact today that means "permission
/// denied" for a page — the folder refusal lives in free text — so no page resolves to Denied
/// from a guess at that text; the state exists, draws and announces correctly, and is reached
/// once #281's diagnostics give it a typed cause.
/// </remarks>
public static class V2SurfaceStateResolver
{
    public static V2SurfaceState Resolve(V2RouteDefinition route, ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(culture);

        if (route.Id == V2Routes.Stash)
        {
            return State(
                V2SurfaceStateKind.Empty,
                V2ShellText.Get("V2.Shell.Detail.NoStashScan"),
                "V2.Shell.Remainder.Stash",
                new V2RecoveryAction("open-capture", "V2.Shell.Action.OpenCapture", null));
        }

        if (route.Id == V2Routes.Tablet)
        {
            return State(
                V2SurfaceStateKind.Empty,
                V2ShellText.Get("V2.Shell.Detail.NoTablet"),
                "V2.Shell.Remainder.Tablet",
                new V2RecoveryAction("manage-pairing", "V2.Shell.Action.ManagePairing", null));
        }

        return route.UsesGameData ? ForGameData(snapshot, nowUtc, culture) : Ready();
    }

    public static V2SurfaceState ForGameData(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = snapshot.Data;
        var sync = new V2RecoveryAction("sync", "V2.Shell.Action.SyncNow", null);
        var setup = new V2RecoveryAction("open-setup", "V2.Shell.Action.OpenSetup", V2Routes.Setup);

        return data.Availability switch
        {
            DataAvailability.DemoFixture => Ready(),
            DataAvailability.Error => State(V2SurfaceStateKind.Failed, data.Detail, "V2.Shell.Remainder.Local", sync, setup),
            DataAvailability.Refreshing when data.ItemCount == 0 =>
                State(V2SurfaceStateKind.Loading, data.Detail, "V2.Shell.Remainder.Local"),
            DataAvailability.Unavailable when snapshot.IsOffline =>
                State(V2SurfaceStateKind.Offline, data.Detail, "V2.Shell.Remainder.Local", setup),
            DataAvailability.Unavailable when !snapshot.DatabaseReady =>
                State(V2SurfaceStateKind.Loading, data.Detail, "V2.Shell.Remainder.Local"),
            DataAvailability.Unavailable => State(V2SurfaceStateKind.Empty, data.Detail, "V2.Shell.Remainder.Local", sync),
            _ when data.ItemCount == 0 => State(V2SurfaceStateKind.Empty, data.Detail, "V2.Shell.Remainder.Local", sync),
            _ when snapshot.IsOffline => State(
                V2SurfaceStateKind.Offline,
                V2ShellText.Format("V2.Shell.Detail.OfflineCached", culture, data.ItemCount, Age(data, nowUtc, culture)),
                "V2.Shell.Remainder.Cached",
                setup) with { Identity = "V2.Shell.Detail.OfflineCached" },
            _ when data.UpdatedUtc is null => State(
                V2SurfaceStateKind.Partial,
                V2ShellText.Format("V2.Shell.Detail.UndatedData", culture, data.ItemCount),
                "V2.Shell.Remainder.Cached",
                sync),
            _ when nowUtc - data.UpdatedUtc > V2Readiness.GameDataStaleAfter => State(
                V2SurfaceStateKind.Stale,
                V2ShellText.Format("V2.Shell.Detail.StaleData", culture, Age(data, nowUtc, culture)),
                "V2.Shell.Remainder.Cached",
                sync) with { Identity = "V2.Shell.Detail.StaleData" },
            _ => Ready(),
        };
    }

    public static V2SurfaceState State(V2SurfaceStateKind kind, string detail, string remainderKey, params V2RecoveryAction[] recovery) =>
        new(V2SurfaceStatePolicies.For(kind), detail, remainderKey, recovery);

    private static V2SurfaceState Ready() => State(V2SurfaceStateKind.Ready, string.Empty, "V2.Shell.Remainder.All");

    private static string Age(RuntimeDataState data, DateTimeOffset nowUtc, CultureInfo culture) =>
        data.UpdatedUtc is { } updated ? V2ShellText.Age(updated, nowUtc, culture) : V2ShellText.Get("V2.Shell.Detail.NeverSynced");
}
