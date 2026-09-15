using System.Text.Json;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>The measured repair set for current extracts omitted by the primary payload.</summary>
public sealed class ReviewedExtractCatalogTests
{
    [Fact]
    public void Sweep_records_eleven_unique_gaps_across_seven_maps_with_complete_evidence()
    {
        var facts = ReviewedExtractCatalog.Facts;

        Assert.Equal(11, facts.Count);
        Assert.Equal(7, facts.Select(fact => fact.MapId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(11, facts.Select(fact => fact.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            11,
            facts.Select(fact => $"{fact.MapId}\n{fact.Name}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            [
                "icebreaker|Helicopter|pmc",
                "lighthouse|Hideout Under the Landing Stage|scav",
                "lighthouse|Industrial Zone Gates|scav",
                "lighthouse|Road to Military Base V-Ex|pmc",
                "lighthouse|Side Tunnel (Co-Op)|shared",
                "lighthouse|Southern Road|pmc",
                "reserve|D-2|pmc",
                "shoreline|Railway Bridge|pmc",
                "the-lab|Medical Block Elevator|unknown",
                "terminal|Zubr Boat|pmc",
                "woods|Friendship Bridge (Co-Op)|shared",
            ],
            facts.Select(fact => $"{fact.MapId}|{fact.Name}|{fact.Faction}"));
        Assert.All(facts, fact =>
        {
            Assert.StartsWith("reviewed:", fact.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(fact.Name));
            Assert.Contains(fact.Faction, new[] { "pmc", "scav", "shared", "unknown" });
            if (fact.Position is { } position)
            {
                Assert.True(double.IsFinite(position.X));
                Assert.True(double.IsFinite(position.Y));
                Assert.True(double.IsFinite(position.Z));
            }

            Assert.Equal("reviewed extract supplement", fact.Provenance.Source);
            Assert.NotNull(fact.Provenance.SourceUpdatedUtc);
            Assert.NotNull(fact.Provenance.Confidence);
            Assert.Equal(fact.Position is null ? 0.80 : 0.90, fact.Provenance.Confidence!.Value.Value, 3);
            Assert.NotNull(fact.Provenance.Reference);
            Assert.Contains("escapefromtarkov.fandom.com/wiki/", fact.Provenance.Reference!);
            Assert.Contains("?oldid=", fact.Provenance.Reference!);
            if (fact.Position is not null)
            {
                Assert.Contains("acidphantasm/SPT-DynamicMaps", fact.Provenance.Reference!);
                Assert.Contains("4944764f5f6c42d152dca6bd1b5371c4f6212a9e", fact.Provenance.Reference!);
            }
        });
    }

    [Fact]
    public void Retained_normalized_sweep_reproduces_the_exact_gap_set_and_variant_collapse()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CoverageFixturePath()));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var primarySource = root.GetProperty("primarySource");
        Assert.Equal(17, primarySource.GetProperty("recordCount").GetInt32());
        Assert.Equal(152, primarySource.GetProperty("extractRowCountBeforeIdentityCollapse").GetInt32());
        Assert.Equal(
            "c53a327dcdcff67a49b9fe2d150731edb4c1611dfc2f75db2ce97444dee2ef77",
            root.GetProperty("canonicalCatalog").GetProperty("sha256").GetString());

        var primaryRecords = root.GetProperty("primaryRecords").EnumerateArray().ToArray();
        Assert.Equal(17, primaryRecords.Length);
        Assert.Equal(
            [
                "ground-zero-21|ground-zero",
                "ground-zero-tutorial|ground-zero",
                "night-factory|factory",
                "the-lab-dark|the-lab",
            ],
            primaryRecords
                .Select(record =>
                    $"{record.GetProperty("mapId").GetString()}|{record.GetProperty("canonicalMapId").GetString()}")
                .Where(mapping =>
                {
                    var separator = mapping.IndexOf('|', StringComparison.Ordinal);
                    return !string.Equals(
                        mapping[..separator],
                        mapping[(separator + 1)..],
                        StringComparison.Ordinal);
                })
                .OrderBy(mapping => mapping, StringComparer.Ordinal)
                .ToArray());

        var referenceMaps = root.GetProperty("referenceMaps").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "customs",
                "factory",
                "ground-zero",
                "icebreaker",
                "interchange",
                "lighthouse",
                "reserve",
                "shoreline",
                "streets-of-tarkov",
                "terminal",
                "the-lab",
                "the-labyrinth",
                "woods",
            ],
            referenceMaps
                .Select(map => map.GetProperty("canonicalMapId").GetString()!)
                .OrderBy(mapId => mapId, StringComparer.Ordinal)
                .ToArray());
        Assert.All(referenceMaps, map =>
        {
            var revision = map.GetProperty("revision").GetInt64();
            Assert.Contains(
                $"oldid={revision}",
                map.GetProperty("url").GetString()!,
                StringComparison.Ordinal);
        });

        var primaryByCanonicalMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var record in primaryRecords)
        {
            var canonicalMapId = record.GetProperty("canonicalMapId").GetString()!;
            if (!primaryByCanonicalMap.TryGetValue(canonicalMapId, out var names))
            {
                names = new(StringComparer.Ordinal);
                primaryByCanonicalMap.Add(canonicalMapId, names);
            }

            foreach (var name in record.GetProperty("extractNames").EnumerateArray())
            {
                names.Add(CoverageIdentity(name.GetString()!));
            }
        }

        var computedGaps = referenceMaps
            .SelectMany(map =>
            {
                var canonicalMapId = map.GetProperty("canonicalMapId").GetString()!;
                var primaryNames = primaryByCanonicalMap[canonicalMapId];
                return map.GetProperty("extractNames")
                    .EnumerateArray()
                    .Select(name => name.GetString()!)
                    .Where(name => !primaryNames.Contains(CoverageIdentity(name)))
                    .Select(name => $"{canonicalMapId}|{name}");
            })
            .OrderBy(gap => gap, StringComparer.Ordinal)
            .ToArray();
        var expectedGaps = root.GetProperty("expectedGaps")
            .EnumerateArray()
            .Select(gap =>
                $"{gap.GetProperty("canonicalMapId").GetString()}|{gap.GetProperty("name").GetString()}")
            .OrderBy(gap => gap, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedGaps, computedGaps);
        Assert.Equal(
            root.GetProperty("expectedGaps")
                .EnumerateArray()
                .Select(gap =>
                    $"{gap.GetProperty("canonicalMapId").GetString()}|{gap.GetProperty("name").GetString()}|{gap.GetProperty("side").GetString()}")
                .OrderBy(gap => gap, StringComparer.Ordinal)
                .ToArray(),
            ReviewedExtractCatalog.Facts
                .Select(fact => $"{fact.MapId}|{fact.Name}|{fact.Faction}")
                .OrderBy(gap => gap, StringComparer.Ordinal)
                .ToArray());

        Assert.All(root.GetProperty("equivalentNameCases").EnumerateArray(), equivalent =>
        {
            var declaredIdentity = equivalent.GetProperty("normalizedIdentity").GetString();
            Assert.Equal(declaredIdentity, CoverageIdentity(equivalent.GetProperty("referenceName").GetString()!));
            Assert.Equal(declaredIdentity, CoverageIdentity(equivalent.GetProperty("primaryName").GetString()!));
        });
    }

    [Fact]
    public void Lighthouse_landing_stage_has_the_reviewed_scav_position()
    {
        var stage = Assert.Single(ReviewedExtractCatalog.Facts, fact =>
            fact.MapId == "lighthouse" && fact.Name == "Hideout Under the Landing Stage");

        Assert.Equal("scav", stage.Faction);
        Assert.True(stage.Position.HasValue);
        Assert.Equal(133.068, stage.Position!.Value.X, 3);
        Assert.Equal(-0.467, stage.Position!.Value.Y, 3);
        Assert.Equal(286.842, stage.Position!.Value.Z, 3);
    }

    [Fact]
    public void Unverified_lab_side_is_not_presented_as_a_pmc_fact()
    {
        var medical = Assert.Single(ReviewedExtractCatalog.Facts, fact =>
            fact.MapId == "the-lab" && fact.Name == "Medical Block Elevator");

        Assert.Equal("unknown", medical.Faction);
        var definition = Assert.Single(ReviewedExtractCatalog.MergeDefinitions(
            "the-lab",
            "lab-id",
            Array.Empty<MapExtract>()));
        Assert.Equal("Faction unverified", definition.Conditions);
    }

    [Fact]
    public void Dark_lab_uses_the_explicit_canonical_variant_mapping()
    {
        var definitions = ReviewedExtractCatalog.MergeDefinitions(
            "the-lab-dark",
            "the-lab-dark",
            Array.Empty<MapExtract>());

        var medical = Assert.Single(definitions);
        Assert.Equal("Medical Block Elevator", medical.Name);
        Assert.Equal("the-lab-dark", medical.MapId);
        Assert.Equal("Faction unverified", medical.Conditions);
    }

    [Theory]
    [InlineData("night-factory", "factory")]
    [InlineData("ground-zero-21", "groundzero")]
    [InlineData("ground-zero-tutorial", "groundzero")]
    [InlineData("the-lab-dark", "thelab")]
    public void Runtime_variant_mapping_matches_the_retained_sweep(
        string primaryMapId,
        string expectedCanonicalIdentity)
    {
        Assert.Equal(
            expectedCanonicalIdentity,
            ReviewedExtractCatalog.CanonicalMapIdentity(primaryMapId));
    }

    [Fact]
    public void A_dark_lab_primary_record_still_supersedes_the_canonical_supplement()
    {
        var primary = new MapExtract(
            "primary:medical",
            "the-lab-dark",
            "MEDICAL BLOCK ELEVATOR",
            new MapPoint(1, 2),
            null,
            new DataProvenance("fixture", DateTimeOffset.UnixEpoch));

        var definitions = ReviewedExtractCatalog.MergeDefinitions(
            "the-lab-dark",
            "the-lab-dark",
            [primary]);

        Assert.Same(primary, Assert.Single(definitions));
    }

    [Theory]
    [InlineData("icebreaker", "Helicopter")]
    [InlineData("terminal", "Zubr Boat")]
    public void Extracts_without_reviewed_coordinates_are_listed_but_not_plotted(
        string mapId,
        string expectedName)
    {
        var definition = Assert.Single(ReviewedExtractCatalog.MergeDefinitions(
            mapId,
            mapId,
            Array.Empty<MapExtract>()));
        var features = ReviewedExtractCatalog.MergeFeatures(
            mapId,
            Array.Empty<MapFeature>());

        Assert.Equal(expectedName, definition.Name);
        Assert.Null(definition.Position);
        Assert.Equal("PMC only", definition.Conditions);
        Assert.Empty(features);
    }

    [Fact]
    public void Definition_merge_adds_every_lighthouse_gap_with_map_scoped_ids()
    {
        var primaryProvenance = new DataProvenance(
            "fixture",
            new DateTimeOffset(2026, 9, 15, 8, 24, 16, TimeSpan.Zero));
        var primary = new[]
        {
            new MapExtract(
                "primary:grotto",
                "requested-id",
                "Scav Hideout at the Grotto",
                new MapPoint(1, 2),
                "Scav only",
                primaryProvenance),
        };

        var merged = ReviewedExtractCatalog.MergeDefinitions("lighthouse", "requested-id", primary);

        Assert.Equal(6, merged.Count);
        Assert.All(merged, extract => Assert.Equal("requested-id", extract.MapId));
        var stage = Assert.Single(merged, extract => extract.Name == "Hideout Under the Landing Stage");
        Assert.Equal("Scav only", stage.Conditions);
        Assert.Equal(133.068, stage.Position!.Value.X, 3);
        Assert.Equal(286.842, stage.Position!.Value.Y, 3);
        Assert.Equal("reviewed extract supplement", stage.Provenance.Source);
    }

    [Fact]
    public void A_primary_name_wins_even_when_its_punctuation_or_case_differs()
    {
        var primaryPosition = new WorldPosition(7, 8, 9);
        var primary = new[]
        {
            new MapFeature(
                MapFeatureKind.Extract,
                "SIDE TUNNEL co-op",
                primaryPosition,
                "pmc",
                "Primary fact"),
        };

        var merged = ReviewedExtractCatalog.MergeFeatures("lighthouse", primary);

        Assert.Equal(5, merged.Count);
        var tunnel = Assert.Single(merged, feature =>
            feature.Name.Contains("SIDE TUNNEL", StringComparison.Ordinal));
        Assert.Equal(primaryPosition, tunnel.Position);
        Assert.Null(tunnel.Provenance);
    }

    private static string CoverageIdentity(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static string CoverageFixturePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "fixtures",
                "extract-catalog",
                "coverage-2026-09-15.normalized.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The retained extract coverage fixture was not found.");
    }
}
