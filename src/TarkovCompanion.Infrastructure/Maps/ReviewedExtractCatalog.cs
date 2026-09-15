using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>One map extract the primary feed omitted at the recorded review.</summary>
/// <param name="Id">A stable companion-owned identity, never confused with an upstream id.</param>
/// <param name="MapId">The canonical map slug.</param>
/// <param name="Name">The name shown by the game's extract panel.</param>
/// <param name="Position">Reviewed world X, height, and world Z coordinates, when published.</param>
/// <param name="Faction">The side that can use it: pmc, scav, shared, or unknown.</param>
/// <param name="Provenance">Pinned factual references and review metadata.</param>
public sealed record ReviewedExtractFact(
    string Id,
    string MapId,
    string Name,
    WorldPosition? Position,
    string Faction,
    DataProvenance Provenance);

/// <summary>
/// A small, reviewed repair layer for current extracts absent from the primary map payload.
/// </summary>
/// <remarks>
/// The primary feed remains authoritative and always wins by map-scoped normalized name. These
/// rows exist because the 2026-09-15 all-map sweep found eleven current extracts absent from that
/// feed, including Hideout Under the Landing Stage on Lighthouse. Nine positions are factual
/// coordinates read from one pinned MIT-licensed snapshot; two rows intentionally remain
/// unplotted because no reviewed world coordinate was available. Names and explicitly recorded
/// sides were checked against pinned EFT Wiki revisions. No source code, artwork, or marker asset
/// is copied.
///
/// Keeping the merge here, instead of sprinkling special cases through recognition and drawing,
/// gives the supplement an automatic retirement path: as soon as the primary feed publishes the
/// same map/name, its record replaces this one without a duplicate marker.
/// </remarks>
public static class ReviewedExtractCatalog
{
    private const string CoordinateRevision = "4944764f5f6c42d152dca6bd1b5371c4f6212a9e";

    private static readonly DateTimeOffset ReviewedUtc =
        new(2026, 9, 15, 19, 35, 40, TimeSpan.Zero);

    private static readonly IReadOnlyList<ReviewedExtractFact> ReviewedFacts =
        Array.AsReadOnly<ReviewedExtractFact>(
    [
        UnpositionedFact(
            "icebreaker",
            "helicopter",
            "Helicopter",
            "pmc",
            "Icebreaker",
            359388,
            new(2026, 9, 13, 20, 15, 20, TimeSpan.Zero)),
        PositionedFact(
            "lighthouse",
            "hideout-under-the-landing-stage",
            "Hideout Under the Landing Stage",
            133.068,
            -0.467,
            286.842,
            "scav",
            "Plugin/Resources/Maps/Lighthouse_TarkovData/Lighthouse_TarkovData.jsonc",
            "Lighthouse",
            359024,
            new(2026, 9, 13, 20, 1, 27, TimeSpan.Zero)),
        PositionedFact(
            "lighthouse",
            "industrial-zone-gates",
            "Industrial Zone Gates",
            -152.56,
            12.25,
            -794.1,
            "scav",
            "Plugin/Resources/Maps/Lighthouse_TarkovData/Lighthouse_TarkovData.jsonc",
            "Lighthouse",
            359024,
            new(2026, 9, 13, 20, 1, 27, TimeSpan.Zero)),
        PositionedFact(
            "lighthouse",
            "road-to-military-base-v-ex",
            "Road to Military Base V-Ex",
            -328.951263,
            16.73,
            -784.3581,
            "pmc",
            "Plugin/Resources/Maps/Lighthouse_TarkovData/Lighthouse_TarkovData.jsonc",
            "Lighthouse",
            359024,
            new(2026, 9, 13, 20, 1, 27, TimeSpan.Zero)),
        PositionedFact(
            "lighthouse",
            "side-tunnel-co-op",
            "Side Tunnel (Co-Op)",
            -68.3,
            6.83,
            318.11,
            "shared",
            "Plugin/Resources/Maps/Lighthouse_TarkovData/Lighthouse_TarkovData.jsonc",
            "Lighthouse",
            359024,
            new(2026, 9, 13, 20, 1, 27, TimeSpan.Zero)),
        PositionedFact(
            "lighthouse",
            "southern-road",
            "Southern Road",
            -295.8,
            11.86,
            420.6,
            "pmc",
            "Plugin/Resources/Maps/Lighthouse_TarkovData/Lighthouse_TarkovData.jsonc",
            "Lighthouse",
            359024,
            new(2026, 9, 13, 20, 1, 27, TimeSpan.Zero)),
        PositionedFact(
            "reserve",
            "d-2",
            "D-2",
            -121.479065,
            -17.0010071,
            172.24913,
            "pmc",
            "Plugin/Resources/Maps/Reserve_TarkovData/Reserve_TarkovData.jsonc",
            "Reserve",
            357731,
            new(2026, 9, 6, 19, 44, 51, TimeSpan.Zero)),
        PositionedFact(
            "shoreline",
            "railway-bridge",
            "Railway Bridge",
            -1029.29,
            -60.79,
            307.59,
            "pmc",
            "Plugin/Resources/Maps/Shoreline_TarkovData/Shoreline_TarkovData.jsonc",
            "Shoreline",
            357755,
            new(2026, 9, 8, 8, 13, 19, TimeSpan.Zero)),
        PositionedFact(
            "the-lab",
            "medical-block-elevator",
            "Medical Block Elevator",
            -112.423,
            -3.10999966,
            -343.986,
            "unknown",
            "Plugin/Resources/Maps/Labs_TarkovDev/Labs_TarkovDev.jsonc",
            "The_Lab",
            354844,
            new(2026, 8, 16, 20, 28, 45, TimeSpan.Zero)),
        UnpositionedFact(
            "terminal",
            "zubr-boat",
            "Zubr Boat",
            "pmc",
            "Terminal",
            359900,
            new(2026, 9, 13, 20, 25, 46, TimeSpan.Zero)),
        PositionedFact(
            "woods",
            "friendship-bridge-co-op",
            "Friendship Bridge (Co-Op)",
            93.17,
            16.57,
            -843.98,
            "shared",
            "Plugin/Resources/Maps/Woods_TarkovData/Woods_TarkovData.jsonc",
            "Woods",
            355184,
            new(2026, 8, 19, 1, 15, 9, TimeSpan.Zero)),
        ]);

    /// <summary>The complete reviewed set; exposed read-only for coverage and provenance audits.</summary>
    public static IReadOnlyList<ReviewedExtractFact> Facts => ReviewedFacts;

    /// <summary>Adds reviewed gaps to extract definitions used by screenshot recognition.</summary>
    public static IReadOnlyList<MapExtract> MergeDefinitions(
        string catalogMapId,
        string resultMapId,
        IReadOnlyList<MapExtract> primary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogMapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultMapId);
        ArgumentNullException.ThrowIfNull(primary);

        var known = primary
            .Select(extract => Identity(extract.Name))
            .ToHashSet(StringComparer.Ordinal);
        var merged = new List<MapExtract>(primary);
        foreach (var fact in ForMap(catalogMapId))
        {
            if (!known.Add(Identity(fact.Name)))
            {
                continue;
            }

            MapPoint? position = fact.Position is { } reviewedPosition
                ? new MapPoint(reviewedPosition.X, reviewedPosition.Z)
                : null;
            merged.Add(new(
                fact.Id,
                resultMapId,
                fact.Name,
                position,
                DescribeFaction(fact.Faction),
                fact.Provenance));
        }

        return merged;
    }

    /// <summary>Adds reviewed gaps to fixed markers used by the desktop map.</summary>
    public static IReadOnlyList<MapFeature> MergeFeatures(
        string mapId,
        IReadOnlyList<MapFeature> primary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(primary);

        var known = primary
            .Where(feature => feature.Kind == MapFeatureKind.Extract)
            .Select(feature => Identity(feature.Name))
            .ToHashSet(StringComparer.Ordinal);
        var merged = new List<MapFeature>(primary);
        foreach (var fact in ForMap(mapId))
        {
            if (fact.Position is not { } position)
            {
                continue;
            }

            if (!known.Add(Identity(fact.Name)))
            {
                continue;
            }

            merged.Add(new(
                MapFeatureKind.Extract,
                fact.Name,
                position,
                fact.Faction,
                DescribeFaction(fact.Faction))
            {
                Provenance = fact.Provenance,
            });
        }

        return merged;
    }

    private static IEnumerable<ReviewedExtractFact> ForMap(string mapId)
    {
        var wanted = Identity(mapId);
        return ReviewedFacts.Where(fact => Identity(fact.MapId) == wanted);
    }

    /// <summary>
    /// Map-scoped identity deliberately ignores case, punctuation, spacing, and apostrophes.
    /// </summary>
    /// <remarks>
    /// The primary has changed punctuation without changing an exit — for example Co-op/Co-Op
    /// and Smuggler's/Smugglers'. Treating those as different would draw the repair beside the
    /// newly fixed upstream marker forever.
    /// </remarks>
    private static string Identity(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
            }
        }

        return result.ToString();
    }

    private static string DescribeFaction(string faction) => faction switch
    {
        "pmc" => "PMC only",
        "scav" => "Scav only",
        "shared" => "Either side",
        _ => "Faction unverified",
    };

    private static ReviewedExtractFact PositionedFact(
        string mapId,
        string slug,
        string name,
        double x,
        double height,
        double z,
        string faction,
        string coordinatePath,
        string wikiPage,
        long wikiRevision,
        DateTimeOffset latestSourceUpdatedUtc)
    {
        var coordinateReference =
            $"https://github.com/acidphantasm/SPT-DynamicMaps/blob/{CoordinateRevision}/{coordinatePath}";
        var listReference = WikiReference(wikiPage, wikiRevision);
        return new(
            $"reviewed:{mapId}:{slug}",
            mapId,
            name,
            new WorldPosition(x, height, z),
            faction,
            new DataProvenance(
                "reviewed extract supplement",
                ReviewedUtc,
                latestSourceUpdatedUtc,
                $"{coordinateReference} | {listReference}",
                new Confidence(0.90)));
    }

    private static ReviewedExtractFact UnpositionedFact(
        string mapId,
        string slug,
        string name,
        string faction,
        string wikiPage,
        long wikiRevision,
        DateTimeOffset sourceUpdatedUtc) =>
        new(
            $"reviewed:{mapId}:{slug}",
            mapId,
            name,
            null,
            faction,
            new DataProvenance(
                "reviewed extract supplement",
                ReviewedUtc,
                sourceUpdatedUtc,
                WikiReference(wikiPage, wikiRevision),
                new Confidence(0.80)));

    private static string WikiReference(string wikiPage, long revision) =>
        $"https://escapefromtarkov.fandom.com/wiki/{wikiPage}?oldid={revision}";
}
