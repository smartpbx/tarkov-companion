using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.RecognitionCorpus;
using Xunit;

namespace TarkovCompanion.RecognitionCorpusTests;

/// <summary>
/// Pins the deterministic contract bytes. The split units, the plan lock, and the run-plan bytes
/// below were recomputed independently of this tool from the checked-in synthetic manifest, so a
/// change to hashing, ordering, canonical decimals, or line endings fails here instead of silently
/// re-splitting a corpus or invalidating every issued plan. Re-pin only for a deliberate,
/// versioned contract change.
/// </summary>
public sealed class CorpusGoldenTests
{
    public const string RunId = "run-golden-synthetic-0001";
    public const string ProducerId = "producer-golden-synthetic";
    public const string ProducerVersion = "1.0.0-golden";
    private const string PlanLock = "562e9e2a941e25b1abeb7b20ff41258773d8dfe3043ea6bfc5730ba645d33161";
    private const string RunPlanSha256 = "3a2c988ac13d6b56301c85656b648d3f47b0bd437bc45bf20937073bd4647cc4";

    private static readonly string[] SplitUnits =
    [
        "sample-golden-ammo-1-0010 9ef3ed49f4ccbbdd036436b63c45b2bdd9a791dc74a69404bb20fa2383f84065 Test",
        "sample-golden-ammo-2-0003 462e33291f0f3c58793cd261f49d2e630ebf2014ad41b3c02a9a8b062075aa9d Test",
        "sample-golden-ammo-3-0002 3e014fd5a00885ee28a2d04b96205db0c339184cc32187128b3a215bc58a3847 Test",
        "sample-golden-ammo-4-0010 e6ce634a5c9353b9043bea27fa11d39e1971dc9621dd9949b2c320c507fcc20e Test",
        "sample-golden-ammo-5-0005 e62a50c0f336db62b2b374fd6642c6bb4346dfa564a283e062245f87e43838d0 Test",
        "sample-golden-loot-crop-0007 4a3ae9467bf69decce1eecc604b4ad05ed35ef465c3bc4f859bcc92cab29d741 Test",
        "sample-golden-loot-orig-0007 4a3ae9467bf69decce1eecc604b4ad05ed35ef465c3bc4f859bcc92cab29d741 Test",
        "sample-golden-loot-s1-0007 6774240496c345bf533c1e92ede94c69ded4878e6432d4dddac5ae19b34f11e7 Test",
        "sample-golden-loot-s2-0011 ccd52922a12722ed387b1c7e4f2d7c9b8beed52c9bdf05030fd9baca069f40c7 Test",
        "sample-golden-loot-s3-0008 003846de4167c90c6434b4947f7028c7f1fe467001ba45fcb59aa99b72b4af7c Test",
        "sample-golden-loot-s4-0013 1620d3c177b0b7d4527678187c998837a1987f6313222fcfbc5ae6b34b85ebb7 Test",
        "sample-golden-loot-seq-f0-0002 eb305a14d10032e5aeadb91cdcb455b84e407c7fde37eedb5e88f075d7732f93 Test",
        "sample-golden-loot-seq-f1-0002 eb305a14d10032e5aeadb91cdcb455b84e407c7fde37eedb5e88f075d7732f93 Test",
        "sample-golden-train-1-0001 1643f56cfd7cf9503693c532488613410ec9b5399e90a268d669e56caece214f Train",
        "sample-golden-train-2-0000 965413dbe0d5573c582eff7ae7c28ff2817200665305d7be8431bc85cbc76458 Train",
        "sample-golden-tune-1-0018 5ca5042abf49a997d5f6855ded9f5921f1ab0d4507dc0cf790718431a0995782 Tune",
    ];

    [Fact]
    public void GoldenSplitUnitsAndAssignmentsArePinnedAndOrderIndependent()
    {
        var manifest = Manifest();
        Assert.Empty(CorpusValidation.ValidateManifest(manifest, CorpusFixtures.GoldenScoredUtc));

        var actual = SplitPlanner.BuildUnits(manifest.Samples)
            .SelectMany(unit => unit.Value.Select(sample => $"{sample.SampleId} {unit.Key} {SplitPlanner.StableAssignment(unit.Key)}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(SplitUnits, actual);
        var pinnedUnits = SplitUnits.Select(line => line.Split(' ')).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        Assert.All(manifest.Samples, sample => Assert.Equal(pinnedUnits[sample.SampleId], sample.DeclaredSplitUnitId));

        var reversed = SplitPlanner.Assign(manifest.Samples.Reverse().ToArray());
        Assert.Equal(SplitPlanner.Assign(manifest.Samples).OrderBy(pair => pair.Key, StringComparer.Ordinal), reversed.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void GoldenRunPlanLockAndBytesArePinned()
    {
        var manifest = Manifest();
        var plan = PrivateRunPlanner.Create(manifest, RunId, ProducerId, ProducerVersion, CorpusFixtures.GoldenScoredUtc);
        Assert.Equal(PlanLock, plan.PlanLock);

        var json = CorpusJson.SerializeRunPlan(plan);
        CorpusFixtures.AssertGolden("synthetic-run-plan.v1.json", json);
        Assert.Equal(RunPlanSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        Assert.Empty(CorpusValidation.ValidateRunPlanInterchange(json, ManifestJson(), CorpusFixtures.GoldenScoredUtc));

        var parsed = CorpusJson.ParseRunPlan(json);
        Assert.Empty(parsed.Errors);
        Assert.Equal(json, CorpusJson.SerializeRunPlan(Assert.IsType<RunPlan>(parsed.Value)));

        // Manifest order is not plan order, and "1.250" is the same scale as "1.25": neither may
        // move a byte of the plan or its lock.
        var reordered = manifest with { Samples = manifest.Samples.Skip(5).Concat(manifest.Samples.Take(5)).ToArray() };
        Assert.Equal(json, CorpusJson.SerializeRunPlan(
            PrivateRunPlanner.Create(reordered, RunId, ProducerId, ProducerVersion, CorpusFixtures.GoldenScoredUtc)));
        var respelled = CorpusJson.ParseManifest(ManifestJson()
            .Replace("\"uiScale\": 1.250", "\"uiScale\": 1.25", StringComparison.Ordinal)
            .Replace("\"overlapWithPrevious\": 0.50", "\"overlapWithPrevious\": 0.5", StringComparison.Ordinal));
        Assert.Empty(respelled.Errors);
        Assert.Equal(PlanLock, PrivateRunPlanner.Create(
            Assert.IsType<CorpusManifest>(respelled.Value), RunId, ProducerId, ProducerVersion, CorpusFixtures.GoldenScoredUtc).PlanLock);
    }

    [Fact]
    public void GoldenAggregateBytesAndIndependentlyCountedMetricsArePinned()
    {
        var manifest = Manifest();
        var plan = PrivateRunPlanner.Create(manifest, RunId, ProducerId, ProducerVersion, CorpusFixtures.GoldenScoredUtc);
        var predictions = CorpusJson.ParsePredictions(CorpusFixtures.Golden("synthetic-predictions.v1.json"));
        Assert.Empty(predictions.Errors);
        var aggregate = IndependentScorer.Score(
            manifest,
            plan,
            Assert.IsType<PredictionDocument>(predictions.Value),
            CorpusFixtures.Thresholds(),
            CorpusEvidenceClass.SyntheticRaster,
            CorpusFixtures.GoldenScoredUtc);
        var json = CorpusJson.SerializeAggregateResults(aggregate);
        CorpusFixtures.AssertGolden("synthetic-aggregate-results.v1.json", json);
        Assert.Empty(AggregateResultValidation.ValidateInterchange(json, CorpusFixtures.ThresholdsJson()));
        Assert.True(aggregate.Privacy.SafeToPublish);
        Assert.Equal(PlanLock, aggregate.PlanLock);

        // Counted by hand from the synthetic predictions: a duplicated overlap claim, a wrong extra
        // claim, a confident wrong-only answer, an abstention, an unavailable result, and two claims
        // in unknown-truth scopes that are excluded rather than scored.
        var loot = Assert.Single(aggregate.Slices, slice => slice.Intent == BenchmarkIntent.LootDecision);
        Assert.Equal("pass", loot.Status);
        Assert.Equal(30, loot.Denominator);
        Assert.Equal(6, loot.IndependentSplitUnits);
        Assert.Equal(26, loot.TruePositives);
        Assert.Equal(3, loot.FalsePositives);
        Assert.Equal(4, loot.FalseNegatives);
        Assert.Equal(2, loot.ExcludedUnknowns);
        Assert.Equal(2, loot.ExcludedPredictionClaims);
        Assert.Equal(29, loot.AttemptedKnownClaims);
        Assert.Equal(1, loot.Abstentions);
        Assert.Equal(1, loot.ConfidentWrong);
        Assert.Equal(1, loot.OverlapDeduplicationErrors);
        Assert.Equal(0, loot.MissingFrames);
        Assert.Equal(0, loot.ReorderedFrames);
        Assert.Equal(11, loot.PerformanceSampleCount);
        Assert.Equal(60.125m, loot.MaximumElapsedMilliseconds);

        var ammo = Assert.Single(aggregate.Slices, slice => slice.Intent == BenchmarkIntent.Ammo);
        Assert.Equal("insufficient-data", ammo.Status);
        Assert.Equal(5, ammo.TruePositives);
        Assert.Equal(5, ammo.IndependentSplitUnits);
        Assert.Equal(6, aggregate.Slices.Count(slice => slice.Denominator == 0 && slice.IndependentSplitUnits == 0 && slice.Status == "insufficient-data"));
    }

    public static string ManifestJson() => CorpusFixtures.Golden("synthetic-manifest.v1.json");

    private static CorpusManifest Manifest()
    {
        var parsed = CorpusJson.ParseManifest(ManifestJson());
        Assert.Empty(parsed.Errors);
        return Assert.IsType<CorpusManifest>(parsed.Value);
    }
}
