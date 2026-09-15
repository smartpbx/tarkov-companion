using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>The measured repair set for current extracts omitted by the primary payload.</summary>
public sealed class ReviewedExtractCatalogTests
{
    [Fact]
    public void Sweep_records_nine_unique_gaps_across_five_maps_with_complete_evidence()
    {
        var facts = ReviewedExtractCatalog.Facts;

        Assert.Equal(9, facts.Count);
        Assert.Equal(5, facts.Select(fact => fact.MapId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(9, facts.Select(fact => fact.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            9,
            facts.Select(fact => $"{fact.MapId}\n{fact.Name}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            [
                "lighthouse|Hideout Under the Landing Stage|scav",
                "lighthouse|Industrial Zone Gates|scav",
                "lighthouse|Road to Military Base V-Ex|pmc",
                "lighthouse|Side Tunnel (Co-Op)|shared",
                "lighthouse|Southern Road|pmc",
                "reserve|D-2|pmc",
                "shoreline|Railway Bridge|pmc",
                "the-lab|Medical Block Elevator|pmc",
                "woods|Friendship Bridge (Co-Op)|shared",
            ],
            facts.Select(fact => $"{fact.MapId}|{fact.Name}|{fact.Faction}"));
        Assert.All(facts, fact =>
        {
            Assert.StartsWith("reviewed:", fact.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(fact.Name));
            Assert.Contains(fact.Faction, new[] { "pmc", "scav", "shared" });
            Assert.True(double.IsFinite(fact.Position.X));
            Assert.True(double.IsFinite(fact.Position.Y));
            Assert.True(double.IsFinite(fact.Position.Z));
            Assert.Equal("reviewed extract supplement", fact.Provenance.Source);
            Assert.NotNull(fact.Provenance.SourceUpdatedUtc);
            Assert.NotNull(fact.Provenance.Confidence);
            Assert.Equal(0.90, fact.Provenance.Confidence!.Value.Value, 3);
            Assert.NotNull(fact.Provenance.Reference);
            Assert.Contains("389e23571d7d6fe8c3da354f80fdca9cd14e9098", fact.Provenance.Reference!);
            Assert.Contains("escapefromtarkov.fandom.com/wiki/", fact.Provenance.Reference!);
        });
    }

    [Fact]
    public void Lighthouse_landing_stage_has_the_reviewed_scav_position()
    {
        var stage = Assert.Single(ReviewedExtractCatalog.Facts, fact =>
            fact.MapId == "lighthouse" && fact.Name == "Hideout Under the Landing Stage");

        Assert.Equal("scav", stage.Faction);
        Assert.Equal(133.068, stage.Position.X, 3);
        Assert.Equal(-0.467, stage.Position.Y, 3);
        Assert.Equal(286.842, stage.Position.Z, 3);
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
}
