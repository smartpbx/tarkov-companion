using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>One catalog row exported next to the local icon corpus.</summary>
internal sealed record CorpusItem(
    string Id,
    string Name,
    string ShortName,
    ItemCategory Category,
    int Width,
    int Height,
    long? Average24HourRoubles,
    bool FleaEligible,
    long? BestTraderRoubles,
    Uri ImageUri);

/// <summary>
/// The machine-local, never-committed icon corpus: json.tarkov.dev's own grid images plus the
/// catalog facts that go with them.
/// </summary>
/// <remarks>
/// Game art is not ours to commit, so it lives outside every checkout, the same way the
/// screenshot corpus does. The directory holds <c>&lt;itemId&gt;-grid-image.webp</c> files and one
/// <c>items.json</c> exported from a synced database. Everything that needs it skips cleanly when
/// it is absent, so CI never depends on it.
/// </remarks>
internal sealed class IconCorpus
{
    public const string EnvironmentVariable = "TARKOV_ICON_CORPUS";
    public const string DefaultDirectory = "/root/orca/recognition-corpus/icons";

    private readonly string _directory;

    private IconCorpus(string directory, IReadOnlyList<CorpusItem> items)
    {
        _directory = directory;
        Items = items;
        ById = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CorpusItem> Items { get; }

    public IReadOnlyDictionary<string, CorpusItem> ById { get; }

    public string Directory => _directory;

    public static IconCorpus? TryLoad()
    {
        var directory = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? DefaultDirectory;
        var catalogPath = Path.Combine(directory, "items.json");
        if (!File.Exists(catalogPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        var items = new List<CorpusItem>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            if (!File.Exists(Path.Combine(directory, id + "-grid-image.webp")) ||
                !Uri.TryCreate(row.GetProperty("imageUrl").GetString(), UriKind.Absolute, out var imageUri))
            {
                continue;
            }

            items.Add(new(
                id,
                row.GetProperty("name").GetString()!,
                row.GetProperty("shortName").GetString()!,
                Enum.TryParse<ItemCategory>(row.GetProperty("category").GetString(), out var category)
                    ? category
                    : ItemCategory.Unknown,
                row.GetProperty("width").GetInt32(),
                row.GetProperty("height").GetInt32(),
                OptionalLong(row, "avg24h"),
                row.GetProperty("fleaEligible").GetInt32() != 0,
                OptionalLong(row, "bestTraderRub"),
                imageUri));
        }

        return items.Count == 0 ? null : new(directory, items);
    }

    public byte[] ReadIcon(string itemId) => File.ReadAllBytes(Path.Combine(_directory, itemId + "-grid-image.webp"));

    public IItemRepository CreateRepository(DateTimeOffset observedUtc) => new Repository(this, observedUtc);

    private static long? OptionalLong(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private sealed class Repository(IconCorpus corpus, DateTimeOffset observedUtc) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(corpus.ById.TryGetValue(itemId, out var item)
                ? new ItemDefinition(
                    item.Id,
                    item.Name,
                    item.ShortName,
                    string.Empty,
                    item.Category,
                    new ItemDimensions(item.Width, item.Height),
                    item.FleaEligible,
                    null,
                    item.ImageUri.AbsoluteUri,
                    null,
                    null,
                    null,
                    new HashSet<string>(StringComparer.Ordinal),
                    Provenance())
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(corpus.ById.TryGetValue(itemId, out var item)
                ? new ItemPriceSnapshot(
                    item.FleaEligible ? item.Average24HourRoubles : null,
                    item.BestTraderRoubles is { } trader
                        ? [new TraderOffer("corpus-trader", "Best trader", trader, Provenance())]
                        : [],
                    item.Average24HourRoubles,
                    null,
                    null,
                    Provenance())
                : null);

        private DataProvenance Provenance() => new("json.tarkov.dev", observedUtc, observedUtc);
    }
}
