using System.Globalization;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One map's line in the loot-spawn coverage report.</summary>
public sealed record LootCoverageRowViewModel(string MapName, string Summary);

/// <summary>
/// Setup's answer to "how much of each map's loot-spawn data can the map actually place?".
/// </summary>
/// <remarks>
/// [Issue 318] Reads the last verified import's own coverage counts for every map. It reads the
/// durable store and nothing else, so it says what the layer is working from, offline included.
/// </remarks>
public sealed class LootCoverageViewModel : BindableViewModel
{
    private readonly ILootSpawnSourcePublicationStore _store;
    private readonly Func<IReadOnlyList<MapLocation>> _locations;
    // [Issue 563] Optional: only the runtime source (not the durable store) knows about a refresh
    // attempt that quarantined and published nothing. Null in call sites that predate it.
    private readonly IHighValueLootRuntimeSource? _runtimeSource;
    private IReadOnlyList<LootCoverageRowViewModel> _rows = [];
    private string _status = "Not measured yet.";
    private int _generation;

    public LootCoverageViewModel(
        ILootSpawnSourcePublicationStore store,
        Func<IReadOnlyList<MapLocation>> locations,
        IHighValueLootRuntimeSource? runtimeSource = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _runtimeSource = runtimeSource;
    }

    public string Title => "Loot spawns on the map";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public IReadOnlyList<LootCoverageRowViewModel> Rows
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

    /// <summary>Reads the stored import again. The newest call wins if two overlap.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var bundle = await _store.ReadLastKnownGoodAsync(cancellationToken).ConfigureAwait(true);
            if (bundle is null)
            {
                Publish(
                    generation,
                    "No loot-spawn data imported yet. It arrives with the next data sync." + LastErrorNote(),
                    []);
                return;
            }

            var names = _locations().ToDictionary(location => location.Id, location => location.Name, StringComparer.OrdinalIgnoreCase);
            var report = LootSpawnCoverageReport.From(bundle);
            var rows = report
                .Select(row => new LootCoverageRowViewModel(
                    names.TryGetValue(row.MapId, out var name) ? name : row.MapId,
                    Summarize(row)))
                .ToArray();
            var positioned = report.Sum(row => row.Positioned);
            var published = report.Sum(row => row.Published);
            Publish(
                generation,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{positioned:N0} of {published:N0} published spawn records have a position · " +
                    $"data through {LocalTime.Date(bundle.Identity.DataThroughUtc)} · " +
                    $"imported {LocalTime.Date(bundle.Identity.ImportedUtc)}.") + LastErrorNote(),
                rows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(generation, $"Could not read the loot-spawn data: {exception.Message}", []);
        }
    }

    private void Publish(int generation, string status, IReadOnlyList<LootCoverageRowViewModel> rows)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        Status = status;
        Rows = rows;
    }

    /// <summary>
    /// " Last import attempt failed (&lt;when&gt;): &lt;reason&gt;." when the most recent refresh did not
    /// publish, else empty. [Issue 563] A quarantined refresh used to leave no trace here at all;
    /// this is the "last error" Setup > Data now shows beside the migration/backup card.
    /// </summary>
    private string LastErrorNote()
    {
        if (_runtimeSource?.LastRefreshOutcome is not { } outcome ||
            outcome.Disposition is LootSpawnSourceImportDisposition.Published or LootSpawnSourceImportDisposition.PublishedPartial)
        {
            return string.Empty;
        }

        var reason = outcome.Diagnostics.Count > 0
            ? outcome.Diagnostics[0].Detail
            : "the refresh did not publish a usable snapshot.";
        return string.Create(
            CultureInfo.CurrentCulture,
            $" Last import attempt failed ({LocalTime.Moment(outcome.AttemptedUtc)}): {reason}");
    }

    /// <summary>"812 of 900 positioned · 610 on a known floor · 88 map-only · 41 left out", leaving out what is zero.</summary>
    internal static string Summarize(LootSpawnMapCoverageRow row)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.CurrentCulture, $"{row.Positioned:N0} of {row.Published:N0} positioned"),
            string.Create(CultureInfo.CurrentCulture, $"{row.FloorResolved:N0} on a known floor"),
        };
        if (row.Unresolved > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{row.Unresolved:N0} map-only"));
        }

        if (row.LeftOut > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{row.LeftOut:N0} left out"));
        }

        return string.Join(" · ", parts);
    }
}
