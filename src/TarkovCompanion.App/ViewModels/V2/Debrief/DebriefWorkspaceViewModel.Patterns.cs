using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>
/// The selected raid's map, as the player's own history tells it (#712 T8, 2-4): the last raids'
/// deaths and where, the exit they use most, and their measured pace. One line, own raids only.
/// </summary>
public sealed partial class DebriefWorkspaceViewModel
{
    /// <summary>Raids per map read for trails; the newest are the ones that say how the player moves now.</summary>
    private const int PatternTrailRaids = 40;

    /// <summary>A death is "near" an exit only this close to the raid's last screenshot.</summary>
    private const double PatternPlaceMetres = 150;

    private string _personalPatternLine = string.Empty;
    private object? _paceMeasuredFor;
    private WalkPace? _personalPace;

    public string PersonalPatternLine
    {
        get => _personalPatternLine;
        private set
        {
            if (SetProperty(ref _personalPatternLine, value))
            {
                OnPropertyChanged(nameof(HasPersonalPatternLine));
            }
        }
    }

    public bool HasPersonalPatternLine => PersonalPatternLine.Length > 0;

    /// <summary>The player's measured pace, or null while too few trails are recorded.</summary>
    public WalkPace? PersonalPace => _personalPace;

    private async Task RefreshPersonalPatternAsync(CancellationToken cancellationToken)
    {
        if (_selected?.MapId is not { Length: > 0 } mapId)
        {
            PersonalPatternLine = string.Empty;
            return;
        }

        var records = _allRecords;
        var raids = records
            // Finished raids only: one still running has no outcome yet and would count as "not a death".
            .Where(record => !record.IsArchived && record.Raid.EndedUtc is not null)
            .Select(record => new PersonalRaid(
                record.Raid.Id,
                record.Raid.MapId,
                record.Side,
                record.Raid.StartedUtc,
                record.Raid.Outcome,
                record.UsedExtract))
            .ToArray();
        var trails = await _raidHistoryService.ListTrailsForMapAsync(mapId, PatternTrailRaids, cancellationToken).ConfigureAwait(true);
        var last = trails
            .Where(trail => trail.Positions.Count > 0)
            .GroupBy(trail => trail.RaidId)
            .ToDictionary(group => group.Key, group => group.First().Positions[^1].Position);
        var map = _maps is null ? null : await _maps.GetAsync(mapId, cancellationToken).ConfigureAwait(true);

        if (!ReferenceEquals(_paceMeasuredFor, records))
        {
            // Pace is the player's, not the map's: every map's trails count.
            var all = new List<IReadOnlyList<ScreenshotPosition>>();
            foreach (var id in records.Select(record => record.Raid.MapId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var mapTrails = string.Equals(id, mapId, StringComparison.OrdinalIgnoreCase)
                    ? trails
                    : await _raidHistoryService.ListTrailsForMapAsync(id, PatternTrailRaids, cancellationToken).ConfigureAwait(true);
                all.AddRange(mapTrails.Select(trail => trail.Positions));
            }

            _personalPace = Application.Services.Personal.PersonalPace.Measure(all);
            _paceMeasuredFor = records;
        }

        var parts = new List<string>();
        if (PersonalPatterns.Describe(
                raids,
                mapId,
                raidId => last.TryGetValue(raidId, out var at) ? at : null,
                at => NearestExitName(map, at)) is { } pattern)
        {
            var deaths = pattern.DeathPlace is { } place
                ? DebriefText.PatternDeathsNear(pattern.Deaths, place, pattern.DeathsAtPlace)
                : DebriefText.PatternDeaths(pattern.Deaths);
            parts.Add($"{DebriefText.PatternRaids(pattern.Raids, MapLabel(mapId))}: {deaths}, {DebriefText.PatternOut(pattern.Extracted)}");
        }

        var side = records.FirstOrDefault(record => record.Raid.Id == _selected.Id)?.Side;
        if (PersonalExits.Favourite(PersonalExits.Uses(raids, mapId, side)) is { } favourite)
        {
            parts.Add(DebriefText.PatternExit(favourite.Name, favourite.Uses));
        }

        parts.Add(_personalPace is { } pace
            ? DebriefText.PatternPace(pace.MetresPerSecond, pace.Legs)
            : DebriefText.PatternPaceFixed);
        PersonalPatternLine = string.Join(" · ", parts);
    }

    private static string? NearestExitName(MapDefinition? map, WorldPosition at)
    {
        if (map is null)
        {
            return null;
        }

        // The catalog projects world X and world Z; its Y is the world Z (SqliteMapDefinitionCache).
        return map.Extracts
            .Where(extract => extract.Position is not null)
            .Select(extract => (extract.Name, Metres: Math.Sqrt(
                Math.Pow(extract.Position!.Value.X - at.X, 2) + Math.Pow(extract.Position!.Value.Y - at.Z, 2))))
            .Where(candidate => candidate.Metres <= PatternPlaceMetres)
            .OrderBy(candidate => candidate.Metres)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }
}
