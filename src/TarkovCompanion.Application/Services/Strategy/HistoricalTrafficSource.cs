using System.Globalization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Application.Services.Strategy;

/// <summary>The governed publication that is installed on this machine, if there is one.</summary>
/// <remarks>
/// The store that verifies and installs packages lives in Infrastructure; this is the one thing
/// the runtime needs from it, so the runtime can be tested without a directory of signed files.
/// </remarks>
public interface ITrafficPublicationSource
{
    /// <summary>The verified publication, or null when nothing is installed (or nothing verifies).</summary>
    Task<TrafficModelPublication?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>The game build the player is running, where something has actually observed it.</summary>
/// <remarks>
/// Traffic compatibility names the exact game version and never treats a wipe as a proxy for it, so
/// "unknown" has to be an answer. Returning null makes the traffic layer say so instead of guessing.
/// </remarks>
public interface IGameVersionSource
{
    Task<string?> GetAsync(CancellationToken cancellationToken);
}

public sealed record HistoricalTrafficRow(string Label, string Kind, double Relative);

/// <summary>What the Raid cockpit shows about historical traffic, and why.</summary>
public sealed record HistoricalTrafficView(
    HistoricalTrafficRuntimeStatus Status,
    string Notice,
    IReadOnlyList<HistoricalTrafficRow> Rows,
    TrafficPredictionReceipt? Receipt)
{
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// Puts the installed governed snapshot in front of <see cref="HistoricalTrafficRuntimeService"/>
/// for the map the cockpit is showing.
/// </summary>
/// <remarks>
/// It existed as two halves that nothing joined: a store that installed and verified snapshots, and
/// a runtime that refused honestly without one. The cockpit took the runtime "registered but not
/// evaluated", so its traffic line was the fixed words "no installed model yet" on every machine,
/// including one that had a model. This is the join, and it is the only place a scope is built.
///
/// The scope is exact. Map comes from the cockpit, mode and wipe from the active profile, game
/// version from <see cref="IGameVersionSource"/>; when any of them is not known the answer is
/// "no model for this", never a nearby one. The cohort is the one thing the app cannot observe
/// about itself, because it names the population a model describes rather than something the
/// player is, so it is read from the manifest: the cell's only cohort, or "all-players" when the
/// manifest holds several, and unavailable when it holds several and none is that.
/// </remarks>
public sealed class HistoricalTrafficSource
{
    /// <summary>The cohort taken when a manifest offers more than one for the same cell.</summary>
    public const string DefaultCohortId = "all-players";

    /// <summary>Wipes last months and models are rebuilt per wipe, so two months is "current".</summary>
    public static readonly TimeSpan DefaultMaximumModelAge = TimeSpan.FromDays(60);

    private const int MaximumZoneRows = 5;
    private const int MaximumCorridorRows = 3;

    public const string NoModelNotice = "Historical traffic — estimate, no installed model yet";

    private readonly ITrafficPublicationSource _publications;
    private readonly HistoricalTrafficRuntimeService _runtime;
    private readonly IGameVersionSource _gameVersion;
    private readonly IProfileRuntimeContextService? _profile;
    private readonly IMapDataService? _maps;
    private readonly TimeProvider _timeProvider;

    public HistoricalTrafficSource(
        ITrafficPublicationSource publications,
        HistoricalTrafficRuntimeService runtime,
        IGameVersionSource gameVersion,
        IProfileRuntimeContextService? profile,
        IMapDataService? maps,
        TimeProvider? timeProvider = null)
    {
        _publications = publications ?? throw new ArgumentNullException(nameof(publications));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gameVersion = gameVersion ?? throw new ArgumentNullException(nameof(gameVersion));
        _profile = profile;
        _maps = maps;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <param name="mapId">The map the cockpit is showing.</param>
    /// <param name="raid">The current raid; its clock is used only when it is a raid on that map.</param>
    public async Task<HistoricalTrafficView> EvaluateAsync(
        string? mapId,
        RaidSnapshot? raid,
        CancellationToken cancellationToken)
    {
        var publication = await _publications.GetAsync(cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return Unavailable(HistoricalTrafficRuntimeStatus.NoInstalledModel, NoModelNotice);
        }

        if (string.IsNullOrWhiteSpace(mapId))
        {
            return Unavailable(HistoricalTrafficRuntimeStatus.IncompatibleModel, "Historical traffic — estimate, no map chosen");
        }

        var context = _profile?.Current.ActiveProfile?.Context;
        var gameVersion = await _gameVersion.GetAsync(cancellationToken).ConfigureAwait(false);
        if (context is null || context.Mode == ProfileGameMode.Unknown)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.IncompatibleModel,
                "Historical traffic — estimate, no model for this profile");
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.IncompatibleModel,
                "Historical traffic — estimate, game version not known yet");
        }

        if (!TryResolveScope(publication, mapId, gameVersion, context, out var scope))
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.IncompatibleModel,
                "Historical traffic — estimate, no model for this map and game version");
        }

        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var duration = await RaidLengthAsync(mapId, raid, cancellationToken).ConfigureAwait(false);
        var request = new HistoricalTrafficRuntimeRequest(
            scope,
            ClockFor(mapId, raid, duration, nowUtc),
            duration ?? TimeSpan.FromMinutes(40),
            nowUtc,
            DefaultMaximumModelAge);
        var result = _runtime.Evaluate(publication, request);
        return Present(result);
    }

    private static bool TryResolveScope(
        TrafficModelPublication publication,
        string mapId,
        string gameVersion,
        ProfileContext context,
        out TrafficCompatibilityScope scope)
    {
        scope = null!;
        try
        {
            var cohorts = publication.Manifest.CompatibilityScopes
                .Where(candidate =>
                    string.Equals(candidate.MapId, mapId, StringComparison.Ordinal) &&
                    string.Equals(candidate.GameVersion, gameVersion, StringComparison.Ordinal) &&
                    candidate.GameMode == context.Mode &&
                    string.Equals(candidate.WipeId, context.WipeSeason.Value, StringComparison.Ordinal))
                .ToArray();
            var chosen = cohorts.Length == 1
                ? cohorts[0]
                : cohorts.FirstOrDefault(candidate => candidate.CohortId == DefaultCohortId);
            if (chosen is null)
            {
                return false;
            }

            scope = chosen;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<TimeSpan?> RaidLengthAsync(string mapId, RaidSnapshot? raid, CancellationToken cancellationToken)
    {
        if (_maps is null || raid is null || !string.Equals(raid.MapId, mapId, StringComparison.Ordinal))
        {
            return null;
        }

        var map = await _maps.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
        return RaidTimer.LengthFor(raid.Side, map?.PmcRaidDuration, map?.ScavRaidDuration);
    }

    /// <summary>
    /// The raid clock as a reading, only for a raid on the map being shown, in progress.
    /// </summary>
    /// <remarks>
    /// The clock is null rather than invented: without one the runtime says the raid phase is
    /// unknown, which is true, instead of showing "early raid" for a player who is in the menu.
    /// </remarks>
    private static RaidClockReading? ClockFor(string mapId, RaidSnapshot? raid, TimeSpan? duration, DateTimeOffset nowUtc)
    {
        if (raid is null || raid.State != RaidLifecycleState.InRaid ||
            !string.Equals(raid.MapId, mapId, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (raid.RaidClock is { } clock && raid.RaidClockReadUtc is { } readUtc && readUtc <= nowUtc)
            {
                return new RaidClockReading(clock, RaidClockBasis.ObservedOnExtractScreen, readUtc.ToUniversalTime());
            }

            if (raid.StartedUtc is { } started && duration is { } total && started <= nowUtc)
            {
                var remaining = total - (nowUtc - started);
                return new RaidClockReading(
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                    RaidClockBasis.CountedFromRaidStart,
                    nowUtc);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A clock reading the contract refuses (an observed hour or more is a misread) is
            // no clock, not a reason to show a phase.
        }

        return null;
    }

    private static HistoricalTrafficView Present(HistoricalTrafficRuntimeResult result)
    {
        if (!result.HasPredictions)
        {
            return Unavailable(result.Status, result.Status switch
            {
                HistoricalTrafficRuntimeStatus.NoInstalledModel => NoModelNotice,
                HistoricalTrafficRuntimeStatus.RaidPhaseUnknown =>
                    "Historical traffic — estimate, starts once the raid clock is known",
                HistoricalTrafficRuntimeStatus.NoPhaseCoverage =>
                    "Historical traffic — estimate, no coverage for this part of the raid",
                _ => "Historical traffic — estimate, no model for this map and game version",
            });
        }

        var rows = result.Zones
            .Take(MaximumZoneRows)
            .Select(zone => new HistoricalTrafficRow(
                Humanize(zone.Estimate.Value!.ZoneId),
                "zone",
                zone.Estimate.Value.RelativeIntensity))
            .Concat(result.Corridors
                .Take(MaximumCorridorRows)
                .Select(corridor => new HistoricalTrafficRow(
                    Humanize(corridor.Estimate.Value!.CorridorId),
                    "route",
                    corridor.Estimate.Value.RelativePressure)))
            .ToArray();
        var dataThrough = result.Manifest!.DataThroughUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var qualifier = result.Status == HistoricalTrafficRuntimeStatus.Partial ? "partial, " : string.Empty;
        return new HistoricalTrafficView(
            result.Status,
            $"Historical traffic — estimate, {qualifier}data through {dataThrough}",
            rows,
            result.Receipt);
    }

    private static HistoricalTrafficView Unavailable(HistoricalTrafficRuntimeStatus status, string notice) =>
        new(status, notice, [], null);

    /// <summary>"factory-gate" -> "Factory gate", for ids the model names in kebab case.</summary>
    private static string Humanize(string id)
    {
        var spaced = id.Replace('-', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0
            ? id
            : char.ToUpper(spaced[0], CultureInfo.InvariantCulture) + spaced[1..];
    }
}
