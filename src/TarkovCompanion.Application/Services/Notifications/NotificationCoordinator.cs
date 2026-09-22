using System.Globalization;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Notifications;

/// <summary>
/// Decides which of the five notifications to raise, and refuses to raise anything else.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 43] All of the judgement lives here and none of it lives in the tray: hand
/// this two snapshots and it tells you what changed that was worth saying. That is why it takes a
/// record and returns a list rather than subscribing to anything — every rule below is a test with
/// no timer in it.
/// </para>
/// <para>
/// Three rules cut across all five. Nothing repeats: each one is keyed on the thing that caused it
/// (a mark's relay id, a raid's id, which endpoints failed, which build is waiting, one outage) and
/// fires once for it. Nothing fires for something the player did: a mark this player dropped is
/// never announced back to them. And a burst becomes one line — six marks in four seconds is "6
/// marks from Ferret", not six interruptions.
/// </para>
/// <para>
/// Four of the five are silent during a raid, and deliberately do not record that they were
/// suppressed: the condition is still true when the raid ends, so the notification arrives then
/// rather than being lost. Only <see cref="NotificationKind.SquadMark"/> fires mid-raid, because
/// it is the only one that changes what somebody does in the next ten seconds.
/// </para>
/// </remarks>
public sealed class NotificationCoordinator
{
    /// <summary>How long a burst of marks is gathered before it is said as one line.</summary>
    /// <remarks>
    /// Long enough that a squadmate double-tapping their ping key is one notification, short
    /// enough that a single mark is still news. Somebody marking three doorways in sequence is
    /// telling you one thing.
    /// </remarks>
    public static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromSeconds(4);

    /// <summary>A burst this size is said immediately rather than waiting out the window.</summary>
    private const int BurstCeiling = 5;

    private readonly TimeSpan _coalesceWindow;
    private readonly HashSet<long> _seenMarkIds = [];
    private readonly List<SquadMarkInput> _pendingMarks = [];
    private DateTimeOffset? _pendingSince;
    private Guid? _debriefedRaidId;
    private string? _announcedFailure;
    private string? _announcedUpdate;
    private bool _relayOutageAnnounced;
    private readonly HashSet<string> _seenSaleIds = new(StringComparer.Ordinal);
    private DateTimeOffset? _listeningSinceUtc;

    public NotificationCoordinator(TimeSpan? coalesceWindow = null) =>
        _coalesceWindow = coalesceWindow ?? DefaultCoalesceWindow;

    /// <summary>
    /// What is worth saying now, given the current state of everything.
    /// </summary>
    /// <remarks>
    /// Safe and expected to be called often — on every runtime state change, and on a tick so a
    /// gathered burst still comes out when nothing else happens. An observation that changes
    /// nothing returns nothing.
    /// </remarks>
    public IReadOnlyList<NotificationRequest> Observe(NotificationInputs inputs, NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(settings);

        var inRaid = inputs.RaidState == RaidLifecycleState.InRaid;
        _listeningSinceUtc ??= inputs.NowUtc;
        var raised = new List<NotificationRequest>();
        GatherSquadMarks(inputs, settings, inRaid);
        if (FlushSquadMarks(inputs.NowUtc) is { } marks)
        {
            raised.Add(marks);
        }

        if (DebriefReady(inputs, settings) is { } debrief)
        {
            raised.Add(debrief);
        }

        if (DataRefreshFailed(inputs, settings, inRaid) is { } data)
        {
            raised.Add(data);
        }

        if (UpdateReady(inputs, settings, inRaid) is { } update)
        {
            raised.Add(update);
        }

        if (RelayUnreachable(inputs, settings, inRaid) is { } relay)
        {
            raised.Add(relay);
        }

        if (FleaSold(inputs, settings, inRaid) is { } sold)
        {
            raised.Add(sold);
        }

        return raised;
    }

    /// <summary>
    /// Takes in the marks that are new, and drops the ones nobody should be told about.
    /// </summary>
    /// <remarks>
    /// Every unseen id is recorded whether or not it is announced, including this player's own and
    /// including ones that arrived outside a raid. Otherwise a mark dropped in the lobby would be
    /// "new" again the moment the raid started, and the first thing somebody would hear on landing
    /// is a replay of the last ten minutes.
    /// </remarks>
    private void GatherSquadMarks(NotificationInputs inputs, NotificationSettings settings, bool inRaid)
    {
        foreach (var mark in inputs.SquadMarks)
        {
            if (!_seenMarkIds.Add(mark.Id))
            {
                continue;
            }

            // Outside a raid this is not worth interrupting anybody for; the map already shows it.
            // Our own marks are never announced back to us, which is the whole of "nothing fires
            // for something the player did" that this application can actually tell.
            if (!inRaid ||
                !settings.SquadMark ||
                string.Equals(mark.By, inputs.PlayerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _pendingMarks.Add(mark);
            _pendingSince ??= inputs.NowUtc;
        }
    }

    private NotificationRequest? FlushSquadMarks(DateTimeOffset nowUtc)
    {
        if (_pendingMarks.Count == 0 || _pendingSince is not { } since)
        {
            return null;
        }

        if (_pendingMarks.Count < BurstCeiling && nowUtc - since < _coalesceWindow)
        {
            return null;
        }

        var gathered = _pendingMarks.ToArray();
        _pendingMarks.Clear();
        _pendingSince = null;
        var names = gathered
            .Select(mark => mark.By)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var noun = gathered.Length == 1
            ? gathered[0].IsPing ? "ping" : "mark"
            : "marks";
        var title = gathered.Length == 1
            ? names.Length == 1
                ? string.Create(CultureInfo.CurrentCulture, $"{names[0]} dropped a {noun}")
                : string.Create(CultureInfo.CurrentCulture, $"A squadmate dropped a {noun}")
            : names.Length == 1
                ? string.Create(CultureInfo.CurrentCulture, $"{gathered.Length} {noun} from {names[0]}")
                : string.Create(CultureInfo.CurrentCulture, $"{gathered.Length} {noun} from {names.Length} squadmates");
        return new(
            NotificationKind.SquadMark,
            title,
            "On the raid map.",
            gathered.Length,
            nowUtc);
    }

    private NotificationRequest? DebriefReady(NotificationInputs inputs, NotificationSettings settings)
    {
        if (inputs.RaidState != RaidLifecycleState.PostRaid ||
            inputs.RaidId is not { } raidId ||
            _debriefedRaidId == raidId)
        {
            return null;
        }

        // Recorded even when the notification is switched off, because the raid did end: turning
        // the setting on later must not produce a debrief notice for a raid from this morning.
        _debriefedRaidId = raidId;
        return settings.DebriefReady
            ? new(
                NotificationKind.DebriefReady,
                "Raid over",
                "The debrief is ready.",
                1,
                inputs.NowUtc)
            : null;
    }

    private NotificationRequest? DataRefreshFailed(NotificationInputs inputs, NotificationSettings settings, bool inRaid)
    {
        var failed = inputs.FailedDataEndpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (failed.Length == 0)
        {
            // A clean refresh clears the memory, so the same endpoint failing again tomorrow is
            // news again rather than a repeat.
            _announcedFailure = null;
            return null;
        }

        var signature = string.Join('|', failed);
        if (signature == _announcedFailure || inRaid || !settings.DataRefreshFailed)
        {
            // Nothing recorded while in a raid: the endpoints are still failing when it ends, so
            // this fires then instead of being swallowed.
            return null;
        }

        _announcedFailure = signature;
        return new(
            NotificationKind.DataRefreshFailed,
            "Game data did not refresh",
            string.Create(CultureInfo.CurrentCulture, $"{Join(failed)} did not answer. The local copy still stands."),
            failed.Length,
            inputs.NowUtc);
    }

    private NotificationRequest? UpdateReady(NotificationInputs inputs, NotificationSettings settings, bool inRaid)
    {
        if (string.IsNullOrWhiteSpace(inputs.UpdateReadyBuild))
        {
            _announcedUpdate = null;
            return null;
        }

        if (string.Equals(inputs.UpdateReadyBuild, _announcedUpdate, StringComparison.Ordinal) ||
            inRaid ||
            !settings.UpdateReady)
        {
            return null;
        }

        _announcedUpdate = inputs.UpdateReadyBuild;
        return new(
            NotificationKind.UpdateReady,
            "Update ready",
            string.Create(CultureInfo.CurrentCulture, $"{inputs.UpdateReadyBuild} is downloaded and waiting in Setup › Updates."),
            1,
            inputs.NowUtc);
    }

    private NotificationRequest? RelayUnreachable(NotificationInputs inputs, NotificationSettings settings, bool inRaid)
    {
        var unreachable = inputs.IsSharing && inputs.RelayStaleSince is not null;
        if (!unreachable)
        {
            _relayOutageAnnounced = false;
            return null;
        }

        if (_relayOutageAnnounced || inRaid || !settings.RelayUnreachable)
        {
            return null;
        }

        _relayOutageAnnounced = true;
        return new(
            NotificationKind.RelayUnreachable,
            "Squad relay unreachable",
            "Sharing is on but the relay stopped answering. Positions on the map are the last good read.",
            1,
            inputs.NowUtc);
    }

    /// <summary>
    /// Every flea sale since the companion started listening, said once, after the raid.
    /// </summary>
    /// <remarks>
    /// The startup replay reads the whole session's log again, so a sale from this morning
    /// arrives looking brand new. Only the game's own timestamp on the line tells it apart: a
    /// sale written before this coordinator first looked is recorded and never announced, and so
    /// is one whose timestamp could not be read, because a stale "sold" is worse than a missed
    /// one when Intel › Flea lists them all anyway. During a raid nothing is recorded, so the
    /// sales that came in are said together when it ends.
    /// </remarks>
    private NotificationRequest? FleaSold(NotificationInputs inputs, NotificationSettings settings, bool inRaid)
    {
        if (inRaid)
        {
            return null;
        }

        var offers = 0;
        var items = 0;
        foreach (var sale in inputs.FleaSales)
        {
            if (string.IsNullOrWhiteSpace(sale.OfferId) || !_seenSaleIds.Add(sale.OfferId))
            {
                continue;
            }

            if (sale.WrittenUtc is not { } written || written < _listeningSinceUtc)
            {
                continue;
            }

            offers++;
            items += Math.Max(1, sale.Count);
        }

        if (offers == 0 || !settings.FleaSold)
        {
            return null;
        }

        var title = offers == 1
            ? "Flea offer sold"
            : string.Create(CultureInfo.CurrentCulture, $"{offers} flea offers sold");
        var body = items == 1
            ? "1 item. Intel › Flea lists it."
            : string.Create(CultureInfo.CurrentCulture, $"{items} items. Intel › Flea lists them.");
        return new(NotificationKind.FleaSold, title, body, offers, inputs.NowUtc);
    }

    /// <summary>"items", "items and tasks", "items, tasks and barters".</summary>
    private static string Join(IReadOnlyList<string> values) => values.Count switch
    {
        0 => string.Empty,
        1 => values[0],
        _ => string.Join(", ", values.Take(values.Count - 1)) + " and " + values[^1],
    };
}
