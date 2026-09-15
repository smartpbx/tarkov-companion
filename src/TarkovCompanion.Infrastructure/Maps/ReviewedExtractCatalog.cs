using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

/// <summary>One map extract the primary feed omitted at the recorded review.</summary>
/// <param name="Id">A stable companion-owned identity, never confused with an upstream id.</param>
/// <param name="MapId">The canonical map slug.</param>
/// <param name="Name">The name shown by the game's extract panel.</param>
/// <param name="Position">Reviewed world X, height, and world Z coordinates.</param>
/// <param name="Faction">The side that can use it: pmc, scav, or shared.</param>
/// <param name="Provenance">The pinned coordinate source and independent current-list check.</param>
public sealed record ReviewedExtractFact(
    string Id,
    string MapId,
    string Name,
    WorldPosition Position,
    string Faction,
    DataProvenance Provenance);

/// <summary>
/// A small, reviewed repair layer for current extracts absent from the primary map payload.
/// </summary>
/// <remarks>
/// The primary feed remains authoritative and always wins by map-scoped normalized name. These
/// rows exist because the 2026-09-15 all-map sweep found nine current extracts absent from that
/// feed, including Hideout Under the Landing Stage on Lighthouse. Every position is a factual
/// coordinate read from one pinned MIT-licensed snapshot and every name/side was checked against
/// the current EFT Wiki extract list. No source code or map artwork is copied.
///
/// Keeping the merge here, instead of sprinkling special cases through recognition and drawing,
/// gives the supplement an automatic retirement path: as soon as the primary feed publishes the
/// same map/name, its record replaces this one without a duplicate marker.
/// </remarks>
public static class ReviewedExtractCatalog
{
    private const string CoordinateRevision = "389e23571d7d6fe8c3da354f80fdca9cd14e9098";

    private static readonly DateTimeOffset ReviewedUtc =
        new(2026, 9, 15, 16, 54, 41, TimeSpan.Zero);

    private static readonly DateTimeOffset CoordinateSourceUpdatedUtc =
        new(2026, 9, 15, 0, 50, 31, TimeSpan.Zero);

    private static readonly IReadOnlyList<ReviewedExtractFact> ReviewedFacts =
        Array.AsReadOnly<ReviewedExtractFact>(
    [
        Fact(
            "lighthouse",
            "hideout-under-the-landing-stage",
            "Hideout Under the Landing Stage",
            133.068,
            -0.467,
            286.842,
            "scav",
            "live-map/maps/lighthouse/Lighthouse_TarkovDev.jsonc",
            "Lighthouse"),
        Fact(
            "lighthouse",
            "industrial-zone-gates",
            "Industrial Zone Gates",
            -152.56,
            12.25,
            -794.1,
            "scav",
            "live-map/maps/lighthouse/Lighthouse_TarkovDev.jsonc",
            "Lighthouse"),
        Fact(
            "lighthouse",
            "road-to-military-base-v-ex",
            "Road to Military Base V-Ex",
            -328.951263,
            16.73,
            -784.3581,
            "pmc",
            "live-map/maps/lighthouse/Lighthouse_TarkovDev.jsonc",
            "Lighthouse"),
        Fact(
            "lighthouse",
            "side-tunnel-co-op",
            "Side Tunnel (Co-Op)",
            -68.3,
            6.83,
            318.11,
            "shared",
            "live-map/maps/lighthouse/Lighthouse_TarkovDev.jsonc",
            "Lighthouse"),
        Fact(
            "lighthouse",
            "southern-road",
            "Southern Road",
            -295.8,
            11.86,
            420.6,
            "pmc",
            "live-map/maps/lighthouse/Lighthouse_TarkovDev.jsonc",
            "Lighthouse"),
        Fact(
            "reserve",
            "d-2",
            "D-2",
            -121.479065,
            -17.0010071,
            172.24913,
            "pmc",
            "live-map/maps/reserve/Reserve_TarkovDev.jsonc",
            "Reserve"),
        Fact(
            "shoreline",
            "railway-bridge",
            "Railway Bridge",
            -1029.29,
            -60.79,
            307.59,
            "pmc",
            "live-map/maps/shoreline/shoreline_TarkovDev.jsonc",
            "Shoreline"),
        Fact(
            "the-lab",
            "medical-block-elevator",
            "Medical Block Elevator",
            -112.423,
            -3.10999966,
            -343.986,
            "pmc",
            "live-map/maps/labs/labs_TarkovDev.jsonc",
            "The_Lab"),
        Fact(
            "woods",
            "friendship-bridge-co-op",
            "Friendship Bridge (Co-Op)",
            93.17,
            16.57,
            -843.98,
            "shared",
            "live-map/maps/woods/Woods_TarkovDev.jsonc",
            "Woods"),
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

            merged.Add(new(
                fact.Id,
                resultMapId,
                fact.Name,
                new MapPoint(fact.Position.X, fact.Position.Z),
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
            if (!known.Add(Identity(fact.Name)))
            {
                continue;
            }

            merged.Add(new(
                MapFeatureKind.Extract,
                fact.Name,
                fact.Position,
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

    private static ReviewedExtractFact Fact(
        string mapId,
        string slug,
        string name,
        double x,
        double height,
        double z,
        string faction,
        string coordinatePath,
        string wikiPage)
    {
        var coordinateReference =
            $"https://github.com/SPT-Leaderboard/Website/blob/{CoordinateRevision}/{coordinatePath}";
        var listReference = $"https://escapefromtarkov.fandom.com/wiki/{wikiPage}";
        return new(
            $"reviewed:{mapId}:{slug}",
            mapId,
            name,
            new WorldPosition(x, height, z),
            faction,
            new DataProvenance(
                "reviewed extract supplement",
                ReviewedUtc,
                CoordinateSourceUpdatedUtc,
                $"{coordinateReference} | {listReference}",
                new Confidence(0.90)));
    }
}
