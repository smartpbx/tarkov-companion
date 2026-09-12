using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

public sealed class RaidStateService(bool developerMode = false) : IRaidStateService
{
    private readonly bool _developerMode = developerMode;

    public RaidSnapshot Current { get; private set; } = new(
        null,
        RaidLifecycleState.Unknown,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        Confidence.Unknown,
        null,
        [],
        false);

    public RaidSnapshot Apply(RaidEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind == RaidEvidenceKind.Simulator && !_developerMode)
        {
            return Current;
        }

        var observedUtc = evidence.ObservedUtc.ToUniversalTime();
        if (observedUtc < Current.UpdatedUtc)
        {
            return Current;
        }

        var targetState = evidence.SuggestedState ?? Current.State;
        var enteringNewRaid = targetState == RaidLifecycleState.LoadingRaid
            && Current.State != RaidLifecycleState.LoadingRaid;
        var enteringRaid = targetState == RaidLifecycleState.InRaid && Current.State != RaidLifecycleState.InRaid;
        var clearingRaid = targetState == RaidLifecycleState.Menu;
        var isManual = evidence.Kind == RaidEvidenceKind.ManualOverride && evidence.MapId is not null
            ? true
            : enteringNewRaid || clearingRaid
                ? false
                : Current.IsManualMapOverride;
        var mapId = Current.IsManualMapOverride && evidence.Kind != RaidEvidenceKind.ManualOverride && !enteringNewRaid
            ? Current.MapId
            : evidence.MapId ?? (clearingRaid ? null : Current.MapId);

        Current = Current with
        {
            RaidId = enteringRaid ? Guid.NewGuid() : clearingRaid || enteringNewRaid ? null : Current.RaidId,
            State = targetState,
            MapId = mapId,
            StartedUtc = enteringRaid ? observedUtc : clearingRaid || enteringNewRaid ? null : Current.StartedUtc,
            UpdatedUtc = observedUtc,
            Confidence = evidence.Confidence,
            LastKnownPosition = enteringNewRaid || clearingRaid ? null : Current.LastKnownPosition,
            ActiveExtracts = enteringNewRaid || clearingRaid ? [] : Current.ActiveExtracts,
            IsManualMapOverride = isManual,
            // A raid keeps the side it started with; evidence that cannot tell does not
            // overwrite what an earlier, better-informed line already established.
            Side = evidence.Side ?? (clearingRaid || enteringNewRaid ? null : Current.Side),
        };

        return Current;
    }

    /// <summary>
    /// Records where the player was, from a screenshot the player chose to take.
    /// </summary>
    /// <remarks>
    /// Ordering is judged against the last position and not against the raid's own clock. A
    /// live raid writes log lines every few seconds, so the raid clock is almost always ahead
    /// of the screenshot that just landed; comparing against it silently discarded every
    /// position taken during a raid, which is the only time positions exist. A screenshot
    /// older than one already recorded is still refused, which is what the guard was for.
    /// </remarks>
    public RaidSnapshot ApplyPosition(ScreenshotPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var observedUtc = position.Timestamp.ToUniversalTime();
        if (Current.LastKnownPosition is { } previous && observedUtc < previous.Timestamp.ToUniversalTime())
        {
            return Current;
        }

        if (Current.State != RaidLifecycleState.InRaid)
        {
            Apply(new RaidEvidence(
                RaidEvidenceKind.ScreenshotFilename,
                observedUtc,
                null,
                RaidLifecycleState.InRaid,
                new Confidence(0.80),
                "A normal screenshot filename provides last-known raid position evidence."));
        }

        Current = Current with
        {
            LastKnownPosition = position,
            // The raid clock only ever moves forward. A screenshot that is genuinely older
            // than the last log line records its position without rewinding the raid.
            UpdatedUtc = observedUtc > Current.UpdatedUtc ? observedUtc : Current.UpdatedUtc,
            Confidence = new Confidence(Math.Max(Current.Confidence.Value, 0.80)),
        };
        return Current;
    }

    public RaidSnapshot ApplyExtracts(IReadOnlyList<ActiveExtract> extracts, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        observedUtc = observedUtc.ToUniversalTime();
        if (observedUtc < Current.UpdatedUtc)
        {
            return Current;
        }

        if (Current.State != RaidLifecycleState.InRaid)
        {
            Apply(new RaidEvidence(
                RaidEvidenceKind.ManualOverride,
                observedUtc,
                null,
                RaidLifecycleState.InRaid,
                new Confidence(0.85),
                "An extract-list observation provides raid-state evidence."));
        }

        Current = Current with
        {
            ActiveExtracts = extracts.ToArray(),
            UpdatedUtc = observedUtc,
            Confidence = new Confidence(Math.Max(Current.Confidence.Value, 0.85)),
        };
        return Current;
    }
}
