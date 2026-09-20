using System.Globalization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One map's line in the quest coverage report.</summary>
public sealed record QuestCoverageRowViewModel(string MapName, string Summary);

/// <summary>
/// Setup's answer to "which quest objectives can the map actually show?", per map.
/// </summary>
/// <remarks>
/// [Issue 379] About three in ten map-bound objectives have no zone in the quest data, so a Raid
/// map that shows only the ones that do is silently incomplete. This puts the gap where it can be
/// read, and shows how much of it the player has closed with their own markers. It reads the synced
/// catalog and does nothing else: it is a report, not a repair.
/// </remarks>
public sealed class QuestCoverageViewModel : BindableViewModel
{
    private readonly IQuestCatalog _catalog;
    private readonly IUserQuestMarkStore _markers;
    private readonly IMapDataService _maps;
    private readonly Func<IReadOnlyList<MapLocation>> _locations;
    private readonly IProfileRuntimeContextService _profile;
    private IReadOnlyList<QuestCoverageRowViewModel> _rows = [];
    private string _status = "Not measured yet.";
    private int _generation;

    public QuestCoverageViewModel(
        IQuestCatalog catalog,
        IUserQuestMarkStore markers,
        IMapDataService maps,
        Func<IReadOnlyList<MapLocation>> locations,
        IProfileRuntimeContextService profile)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public string Title => "Quest objectives on the map";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public IReadOnlyList<QuestCoverageRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
            }
        }
    }

    public bool HasRows => _rows.Count > 0;

    /// <summary>Measures the synced catalog again. The newest call wins if two overlap.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var scope = _profile.Current.CatalogScope;
            if (scope is null)
            {
                Publish(generation, "Choose a game mode in Setup to see this.", []);
                return;
            }

            var catalog = await _catalog.GetAsync(scope.GameMode, scope.Language, cancellationToken).ConfigureAwait(true);
            if (catalog is null)
            {
                Publish(generation, "No quest data synced yet.", []);
                return;
            }

            await _markers.LoadAsync(cancellationToken).ConfigureAwait(true);
            var locations = _locations();
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in locations)
            {
                names[location.Id] = location.Name;
                keys[location.Id] = location.Id;
                if (!string.IsNullOrWhiteSpace(location.SourceId))
                {
                    keys[location.SourceId] = location.Id;
                }

                foreach (var alternate in location.Variants.SelectMany(variant => variant.AlternateLocationIds))
                {
                    keys[alternate] = location.Id;
                }

                // Quests name a map by the game's own id, which the map catalog does not carry.
                var definition = await _maps.GetAsync(location.Id, cancellationToken).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(definition?.GameId))
                {
                    keys[definition.GameId] = location.Id;
                }
            }

            var report = QuestObjectiveCoverage.Measure(
                catalog,
                _markers.Markers,
                catalogId => keys.TryGetValue(catalogId, out var key) ? key : null);
            // A map the catalog names and the app has no map for is an id nobody can read, so those
            // are one "Other maps" line rather than a row of hexadecimal each.
            var known = report.Where(row => names.ContainsKey(row.MapId)).ToArray();
            var unknown = report.Except(known).ToArray();
            var rows = known
                .Select(row => new QuestCoverageRowViewModel(names[row.MapId], Summarize(row)))
                .Concat(unknown.Length == 0
                    ? []
                    : [new QuestCoverageRowViewModel(
                        "Other maps",
                        Summarize(new QuestMapCoverage(
                            "other",
                            unknown.Sum(row => row.Objectives),
                            unknown.Sum(row => row.Placed),
                            unknown.Sum(row => row.CandidatesOnly),
                            unknown.Sum(row => row.NoLocation),
                            unknown.Sum(row => row.PlacedByPlayer))))])
                .ToArray();
            var total = report.Sum(row => row.Objectives);
            var drawable = report.Sum(row => row.Placed + row.CandidatesOnly);
            Publish(
                generation,
                total == 0
                    ? "The synced quests name no map objectives."
                    : string.Create(
                        CultureInfo.CurrentCulture,
                        $"{drawable:N0} of {total:N0} map objectives have a place in the quest data ({(double)drawable / total:P0})."),
                rows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(generation, $"Could not measure quest coverage: {exception.Message}", []);
        }
    }

    private void Publish(int generation, string status, IReadOnlyList<QuestCoverageRowViewModel> rows)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        Status = status;
        Rows = rows;
    }

    /// <summary>"96 of 120 placed · 24 with no place · 3 placed by you", leaving out what is zero.</summary>
    internal static string Summarize(QuestMapCoverage row)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.CurrentCulture, $"{row.Placed + row.CandidatesOnly:N0} of {row.Objectives:N0} placed"),
        };
        if (row.NoLocation > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{row.NoLocation:N0} with no place"));
        }

        if (row.PlacedByPlayer > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{row.PlacedByPlayer:N0} placed by you"));
        }

        return string.Join(" · ", parts);
    }
}
