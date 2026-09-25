using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// What the player has said about this raid: how much risk they will carry loot through, and
/// the phase when they would rather say it than have it counted.
/// </summary>
/// <remarks>
/// Risk is a preference and nothing on a screen can supply it, so it is the player's and starts
/// at the ordinary setting. [#902 P8] It is kept between runs under <c>page.loot</c>. The phase is
/// not: it belongs to one raid, and a choice made in the last raid ("Extracting") carried into
/// every later one. <see cref="ObserveRaid"/> clears it once a different raid is seen.
/// </remarks>
public sealed class LootScanRaidPreference
{
    private readonly PageState _state;
    private RecommendationRaidRisk _risk = RecommendationRaidRisk.Low;
    private RecommendationRaidPhase? _phase;
    private bool _phaseRaidKnown;
    private Guid? _phaseRaid;

    public LootScanRaidPreference(IWorkspaceLayoutStore? store = null)
    {
        _state = new(store, WorkspaceLayoutKeys.PageLoot);
        _risk = _state.Enum("risk", RecommendationRaidRisk.Low);
    }

    public event EventHandler? Changed;

    public RecommendationRaidRisk Risk
    {
        get => _risk;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_risk != value)
            {
                _risk = value;
                _state.SetEnum("risk", value, RecommendationRaidRisk.Low);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Null counts the phase from the raid clock; a value is the player's own call.</summary>
    public RecommendationRaidPhase? Phase
    {
        get => _phase;
        set
        {
            if (value is { } phase && !Enum.IsDefined(phase))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (_phase != value)
            {
                _phase = value;
                _phaseRaidKnown = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Ties a chosen phase to the raid it was chosen in, and returns it to the counted phase when
    /// a different raid is seen. True when that happened.
    /// </summary>
    /// <remarks>
    /// The first raid seen after the choice is the one it belongs to, so a phase picked on a result
    /// from the raid in progress stays for that raid, and the next raid starts counted again.
    /// </remarks>
    public bool ObserveRaid(Guid? raidId)
    {
        if (_phase is null)
        {
            return false;
        }

        if (!_phaseRaidKnown)
        {
            _phaseRaidKnown = true;
            _phaseRaid = raidId;
            return false;
        }

        if (_phaseRaid == raidId)
        {
            return false;
        }

        Phase = null;
        return true;
    }
}

/// <summary>
/// The raid phase and risk a loot decision is weighed against.
/// </summary>
/// <remarks>
/// <para>
/// The engine refuses an economic take-or-leave without them, and the Loot Scan passed null, so
/// nothing was ever decided on value. The phase is counted the way the rest of the app counts
/// it: the raid clock read off a screenshot where there is one, otherwise the time since the
/// game confirmed the raid against the map's own length, in thirds. Outside a raid, or on a map
/// whose length for this side is not known, the phase is unread and says so; it is never
/// assumed to be early.
/// </para>
/// <para>
/// "Extracting" is never counted. Nothing the companion may read says the player is leaving, so
/// it exists only as the player's own choice.
/// </para>
/// </remarks>
public sealed class LootScanRaidContextSource(
    IRaidStateService raidState,
    IMapDataService maps,
    LootScanRaidPreference preference)
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion loot scan", "loot-scan-raid-context-1");

    private readonly IRaidStateService _raidState = raidState ?? throw new ArgumentNullException(nameof(raidState));
    private readonly IMapDataService _maps = maps ?? throw new ArgumentNullException(nameof(maps));
    private readonly LootScanRaidPreference _preference = preference ?? throw new ArgumentNullException(nameof(preference));

    public async Task<RecommendationRaidContext> ReadAsync(DateTimeOffset evaluatedUtc, CancellationToken cancellationToken)
    {
        var chosen = new EvidenceProvenance(
            EvidenceSourceClass.UserEntered,
            "loot-scan/raid-preference",
            evaluatedUtc,
            EvidenceConfidence.Certain,
            Producer);
        _preference.ObserveRaid(_raidState.Current.RaidId);
        var risk = new EvidencedValue<RecommendationRaidRisk?>(
            "raid.risk",
            _preference.Risk,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "raid.risk.chosen"),
            chosen);
        if (_preference.Phase is { } picked)
        {
            return new(
                new(
                    "raid.phase",
                    picked,
                    new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "raid.phase.chosen"),
                    chosen),
                risk);
        }

        return new(await CountPhaseAsync(evaluatedUtc, cancellationToken).ConfigureAwait(false), risk);
    }

    private async Task<EvidencedValue<RecommendationRaidPhase?>> CountPhaseAsync(
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var raid = _raidState.Current;
        if (raid.State != RaidLifecycleState.InRaid || raid.MapId is null)
        {
            return Unread("raid.phase.not-in-raid", evaluatedUtc);
        }

        TimeSpan? length = null;
        try
        {
            var map = await _maps.GetAsync(raid.MapId, cancellationToken).ConfigureAwait(false);
            length = RaidTimer.LengthFor(raid.Side, map?.PmcRaidDuration, map?.ScavRaidDuration);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A map that cannot be read has no length, which is the unread case below.
        }

        if (length is not { } total || total <= TimeSpan.Zero)
        {
            return Unread("raid.phase.length-unknown", evaluatedUtc);
        }

        (TimeSpan Clock, DateTimeOffset ReadUtc)? observed =
            raid.RaidClock is { } clock && raid.RaidClockReadUtc is { } readUtc && readUtc <= evaluatedUtc
                ? (clock, readUtc)
                : null;
        var remaining = RaidTimer.ResolveForRaid(
            observed,
            raid.StartedUtc,
            total,
            raid.Side,
            startSetByHand: false,
            nowUtc: evaluatedUtc);
        if (remaining.Remaining is not { } left || left > total)
        {
            return Unread("raid.phase.clock-unknown", evaluatedUtc);
        }

        var progress = (total - left).TotalSeconds / total.TotalSeconds;
        var phase = progress switch
        {
            < 1d / 3d => RecommendationRaidPhase.Early,
            < 2d / 3d => RecommendationRaidPhase.Middle,
            _ => RecommendationRaidPhase.Late,
        };
        var observedBasis = remaining.Basis == RaidTimeBasis.Observed;
        var basisUtc = observedBasis ? observed!.Value.ReadUtc : raid.StartedUtc ?? evaluatedUtc;
        if (basisUtc > evaluatedUtc)
        {
            basisUtc = evaluatedUtc;
        }

        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            $"loot-scan/raid-phase/{(observedBasis ? "clock-read" : "counted")}",
            evaluatedUtc,
            EvidenceConfidence.Certain,
            Producer,
            generatedUtc: evaluatedUtc,
            inputs:
            [
                new(
                    observedBasis ? EvidenceSourceClass.GameWrittenScreenshot : EvidenceSourceClass.GameWrittenLog,
                    observedBasis ? "raid-clock" : "raid-start",
                    basisUtc,
                    EvidenceConfidence.Certain,
                    Producer),
                new(
                    EvidenceSourceClass.PublicStructuredData,
                    $"json.tarkov.dev/maps/{raid.MapId}",
                    basisUtc,
                    EvidenceConfidence.Certain,
                    Producer),
            ]);
        return new(
            "raid.phase",
            phase,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "raid.phase.counted"),
            provenance);
    }

    private static EvidencedValue<RecommendationRaidPhase?> Unread(string code, DateTimeOffset evaluatedUtc) =>
        new(
            "raid.phase",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, code),
            new(EvidenceSourceClass.GameWrittenLog, "raid-state", evaluatedUtc, EvidenceConfidence.Unscored, Producer));
}
