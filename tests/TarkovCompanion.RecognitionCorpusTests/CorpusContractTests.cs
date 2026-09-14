using System.Text.Json;
using TarkovCompanion.RecognitionCorpus;

namespace TarkovCompanion.RecognitionCorpusTests;

public sealed class CorpusContractTests
{
    [Fact]
    public void CheckedInBaselineIsExactlyBlockedUntilConsentedPixelsExist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "baselines", "current-main.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("not-measured: blocked-no-consented-pixels", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void SyntheticManifestAllowsTruthButRealRasterRequiresConsentPrivacyAndRetention()
    {
        var synthetic = Sample("sample-synthetic-0001", CorpusEvidenceClass.SyntheticRaster, "a" + new string('0', 63));
        synthetic = WithDeclaredUnit(synthetic);
        Assert.Empty(CorpusValidation.ValidateManifest(new CorpusManifest("corpus-synthetic-0001", "phash-graph.v1", [synthetic]), Future));

        var real = WithDeclaredUnit(Sample("sample-real-0000001", CorpusEvidenceClass.RealRaster, "b" + new string('0', 63)));
        var errors = CorpusValidation.ValidateManifest(new CorpusManifest("corpus-real-000000001", "phash-graph.v1", [real]), Future);

        Assert.Contains(errors, error => error.Contains("consent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("privacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SplitUnitsJoinExactNearDuplicateAndScrollLineageAndRemainStable()
    {
        var first = Sample("sample-scroll-first1", CorpusEvidenceClass.SyntheticRaster, "c" + new string('0', 63), frame: 0);
        var second = Sample("sample-scroll-second", CorpusEvidenceClass.SyntheticRaster, "d" + new string('0', 63), frame: 1, near: [first.DecodedPixelSha256!]);
        var initial = new[] { first, second };
        var unit = Assert.Single(SplitPlanner.BuildUnits(initial));
        var samples = initial.Select(sample => sample with { DeclaredSplitUnitId = unit.Key }).ToArray();

        Assert.Empty(SplitPlanner.ValidateUnits(samples));
        var firstAssignment = SplitPlanner.Assign(samples);
        var secondAssignment = SplitPlanner.Assign(samples.Reverse().ToArray());
        Assert.Equal(firstAssignment.Single().Key, secondAssignment.Single().Key);
        Assert.Equal(firstAssignment.Single().Value, secondAssignment.Single().Value);
        Assert.Single(firstAssignment);
    }

    [Fact]
    public void TruthFreeInterchangeAllowsUnknownFieldsButRejectsTruthPathsAndOcrStrings()
    {
        const string allowed = """{ "schemaVersion":"run-plan.v1", "extension":{"future":"ok"} }""";
        const string forbidden = """{ "schemaVersion":"run-plan.v1", "truth":{"label":"no"}, "sourcePath":"/private/a.png", "ocrText":"private" }""";

        Assert.Empty(CorpusValidation.ValidateTruthFreeInterchange(allowed, "run-plan.v1"));
        var errors = CorpusValidation.ValidateTruthFreeInterchange(forbidden, "run-plan.v1");
        Assert.Contains(errors, error => error.Contains("truth", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("ocrText", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckedInSyntheticRunPlanRemainsTruthFree()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "examples");
        Assert.Empty(CorpusValidation.ValidateTruthFreeInterchange(File.ReadAllText(Path.Combine(root, "synthetic-run-plan.v1.json")), "run-plan.v1"));
    }

    [Fact]
    public void UnexpectedDecodedPixelChangesAndMismatchedPrivateConsentFailClosed()
    {
        var sample = WithDeclaredUnit(Sample("sample-private-real001", CorpusEvidenceClass.RealRaster, CorpusValidation.CanonicalPixelHash([1, 2, 3])) with
        {
            Consent = new ConsentEvidence(new string('a', 64), ["benchmark"], Future, "active"),
            PrivacyReview = new PrivacyReviewEvidence("approved", "not-required", Future),
        });
        var consent = sample.Consent! with { ConsentHash = new string('b', 64) };

        Assert.Throws<InvalidDataException>(() => CorpusValidation.RequireExpectedPixelHash(sample.DecodedPixelSha256!, [3, 2, 1]));
        Assert.Contains(CorpusValidation.VerifyRealEligibility(sample, consent, sample.PrivacyReview!, Future), error => error.Contains("matching", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepositoryPathsCannotBeCorpusRoots()
    {
        Assert.Throws<InvalidOperationException>(() => CorpusValidation.RejectRepositoryPath(Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void IndependentScorerDoesNotDoubleCountOverlappingFramesAndMarksUnsupportedSlices()
    {
        var first = Sample("sample-score-first01", CorpusEvidenceClass.SyntheticRaster, "e" + new string('0', 63), frame: 0);
        var second = Sample("sample-score-second", CorpusEvidenceClass.SyntheticRaster, "f" + new string('0', 63), frame: 1, near: [first.DecodedPixelSha256!]) with
        {
            Lineage = new SequenceLineage("sequence-score-0001", 1, "viewport-01", "root", 0.5m, null),
            Truth = [new TruthClaim("truth-shared-item", TruthState.Known, "item", "known-item")],
        };
        first = first with { Truth = [new TruthClaim("truth-shared-item", TruthState.Known, "item", "known-item")] };
        var unit = Assert.Single(SplitPlanner.BuildUnits([first, second]));
        var samples = new[]
        {
            first with { DeclaredSplitUnitId = unit.Key },
            second with { DeclaredSplitUnitId = unit.Key },
        };
        var predictions = new[]
        {
            new ProducerPrediction(samples[0].SampleId, BenchmarkIntent.LootDecision, CorpusEvidenceClass.SyntheticRaster, "item", PredictionStatus.Detected, 0.95m,
                [new PredictionClaim("claim-one", "item", "known-item")], 10),
            new ProducerPrediction(samples[1].SampleId, BenchmarkIntent.LootDecision, CorpusEvidenceClass.SyntheticRaster, "item", PredictionStatus.Detected, 0.95m,
                [new PredictionClaim("claim-duplicate", "item", "known-item")], 20),
        };
        var thresholds = new FrozenThresholds("recognition-scorer-policy.v1", 1, 1, 0, 1, 1, 0);

        var slices = IndependentScorer.Score(samples, predictions, thresholds);
        var loot = Assert.Single(slices.Where(slice => slice.Intent == BenchmarkIntent.LootDecision && slice.EvidenceClass == CorpusEvidenceClass.SyntheticRaster));
        var health = Assert.Single(slices.Where(slice => slice.Intent == BenchmarkIntent.HealthCharacter && slice.EvidenceClass == CorpusEvidenceClass.RealRaster));

        Assert.Equal("reported", loot.Status);
        Assert.Equal(1, loot.Denominator);
        Assert.Equal(1, loot.TruePositives);
        Assert.Equal(1, loot.FalsePositives);
        Assert.Equal(1, loot.OverlapDeduplicationErrors);
        Assert.Equal("insufficient-data", health.Status);
    }

    private static readonly DateTimeOffset Future = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CorpusSample Sample(string sampleId, CorpusEvidenceClass evidenceClass, string hash, int frame = 0, IReadOnlyList<string>? near = null)
    {
        var intentId = IndependentScorer.IntentId(BenchmarkIntent.LootDecision);
        return new CorpusSample(
            sampleId,
            evidenceClass,
            hash,
            near ?? [],
            new string('0', 64),
            new CaptureContext(intentId, "session-corpus-0001", "correlation-corpus-001", frame, "workspace-001", "profile-00001", "map-customs-0001", "floor-0000000001", "plan-00000000001", [], null, null, "desktop", "user-screenshot", 1920, 1080, 1m, "en-US", "1.0", "2.0"),
            new SequenceLineage("sequence-score-0001", frame, "viewport-01", "root", frame == 0 ? 0m : 0.5m, null),
            [new TruthClaim($"truth-{sampleId}", TruthState.Known, "item", "known-item")],
            null,
            null);
    }

    private static CorpusSample WithDeclaredUnit(CorpusSample sample)
    {
        var unit = Assert.Single(SplitPlanner.BuildUnits([sample]));
        return sample with { DeclaredSplitUnitId = unit.Key };
    }
}
