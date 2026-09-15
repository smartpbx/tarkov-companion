using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.RecognitionCorpus;
using Xunit;

namespace TarkovCompanion.RecognitionCorpusTests;

public sealed class CorpusContractTests
{
    private static readonly DateTimeOffset Now = new(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CheckedInBaselineIsExactlyBlockedUntilConsentedPixelsExist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "baselines", "current-main.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("not-measured: blocked-no-consented-pixels", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void JsonValidatorsFailClosedOnMalformedEmptyAndSchemaOnlyDocuments()
    {
        Assert.NotEmpty(CorpusValidation.ValidatePrivateManifestInterchange(string.Empty, Now));
        Assert.NotEmpty(CorpusValidation.ValidatePrivateManifestInterchange("{", Now));
        Assert.NotEmpty(CorpusValidation.ValidatePrivateManifestInterchange("{}", Now));
        Assert.NotEmpty(CorpusValidation.ValidatePrivateManifestInterchange("""{"schemaVersion":"manifest.v1"}""", Now));
        Assert.Contains(CorpusValidation.ValidatePrivateManifestInterchange(
                """{"schemaVersion":"wrong","schemaVersion":"manifest.v1"}""", Now),
            error => error.Contains("repeats JSON property", StringComparison.OrdinalIgnoreCase));

        var manifest = Manifest(Sample("sample-json-empty-0001"));
        var plan = PrivateRunPlanner.Create(manifest, "run-json-empty-00001", "producer-json-00001", "1.0", Now);
        Assert.NotEmpty(CorpusValidation.ValidateRunPlanInterchange("""{"schemaVersion":"run-plan.v1"}""", ManifestJson(manifest), Now));
        Assert.NotEmpty(CorpusValidation.ValidatePredictionsInterchange("""{"schemaVersion":"predictions.v1"}""", RunPlanJson(plan)));
    }

    [Fact]
    public void UnknownJsonPropertiesAreCompatibleButPrivateAndFilesystemFieldsRemainForbidden()
    {
        var manifest = Manifest(Sample("sample-json-compatible1"));
        var manifestNode = JsonNode.Parse(ManifestJson(manifest))!.AsObject();
        manifestNode["futureExtension"] = new JsonObject { ["revision"] = 2 };
        Assert.Empty(CorpusValidation.ValidatePrivateManifestInterchange(manifestNode.ToJsonString(), Now));

        var plan = PrivateRunPlanner.Create(manifest, "run-json-compatible01", "producer-json-00002", "1.0", Now);
        var planNode = JsonNode.Parse(RunPlanJson(plan))!.AsObject();
        planNode["futureExtension"] = true;
        Assert.Empty(CorpusValidation.ValidateRunPlanInterchange(planNode.ToJsonString(), ManifestJson(manifest), Now));

        planNode["sourcePath"] = "/private/capture.png";
        planNode["truth"] = new JsonObject { ["label"] = "private" };
        planNode["ocrText"] = "private text";
        planNode["filename"] = "capture.png";
        var errors = CorpusValidation.ValidateRunPlanInterchange(planNode.ToJsonString(), ManifestJson(manifest), Now);
        Assert.Contains(errors, error => error.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("truth", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("ocrText", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("filename", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("filesystem path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealRasterNeedsMatchingFullConsentPrivacyRetentionAndImportedPixels()
    {
        var sample = RealSample("sample-private-real0001");
        var manifest = Manifest(sample);
        Assert.Empty(CorpusValidation.ValidateManifest(manifest, Now));

        var summaryOnly = manifest with { PrivateEvidence = [] };
        Assert.Contains(CorpusValidation.ValidateManifest(summaryOnly, Now), error => error.Contains("private", StringComparison.OrdinalIgnoreCase));

        var changedPixels = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with { ObservedDecodedPixelSha256 = Hash("changed") }],
        };
        Assert.Contains(CorpusValidation.ValidateManifest(changedPixels, Now), error => error.Contains("changed", StringComparison.OrdinalIgnoreCase));

        var revoked = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with { Consent = manifest.PrivateEvidence[0].Consent! with { RevocationState = "revoked" } }],
        };
        Assert.Contains(CorpusValidation.ValidateManifest(revoked, Now), error => error.Contains("revoked", StringComparison.OrdinalIgnoreCase));

        var mismatchedReview = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                PrivacyReview = manifest.PrivateEvidence[0].PrivacyReview! with { ReviewHash = Hash("different-review") },
            }],
        };
        Assert.Contains(CorpusValidation.ValidateManifest(mismatchedReview, Now), error => error.Contains("matching", StringComparison.OrdinalIgnoreCase));

        var offsetReview = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                PrivacyReview = manifest.PrivateEvidence[0].PrivacyReview! with { ReviewedUtc = Now.ToOffset(TimeSpan.FromHours(2)) },
            }],
        };
        Assert.Contains(CorpusValidation.ValidateManifest(offsetReview, Now), error => error.Contains("UTC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConditionalRasterHashesTruthUnknownsAndOpaqueIdsAreEnforced()
    {
        var postOcr = Sample("sample-post-ocr-000001", CorpusEvidenceClass.PostOcrEvidence) with
        {
            DecodedPixelSha256 = Hash("not-allowed"),
            NearDuplicateHashes = [Hash("near-not-allowed")],
        };
        var invalidTruth = Sample("sample-invalid-truth01") with
        {
            Truth = [new TruthClaim("truth-invalid-00001", TruthState.Unknown, "item", "invented")],
        };
        var shortId = Sample("short-id");

        Assert.Contains(CorpusValidation.ValidateManifest(Manifest(postOcr), Now), error => error.Contains("must not claim raster", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CorpusValidation.ValidateManifest(Manifest(invalidTruth), Now), error => error.Contains("truth-with-unknowns", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CorpusValidation.ValidateManifest(Manifest(shortId), Now), error => error.Contains("opaque", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TruthAndPredictionRegionsCannotEscapeCaptureDimensions()
    {
        var truthRegion = Sample("sample-region-truth001") with
        {
            Truth = [new TruthClaim("truth-region-000001", TruthState.Known, "region", null, new PixelRegion(1900, 1000, 40, 90))],
        };
        Assert.Contains(CorpusValidation.ValidateManifest(Manifest(truthRegion), Now), error => error.Contains("out-of-bounds truth region", StringComparison.OrdinalIgnoreCase));

        var validSample = Sample("sample-region-valid001") with
        {
            Truth = [new TruthClaim("truth-region-valid01", TruthState.Known, "region", null, new PixelRegion(10, 20, 30, 40))],
        };
        var manifest = Manifest(validSample);
        var plan = PrivateRunPlanner.Create(manifest, "run-region-bound-0001", "producer-region-0001", "1.0", Now);
        var badPrediction = new PredictionDocument(plan.RunId, plan.ProducerId, plan.ProducerVersion,
        [
            Prediction(plan.Samples[0], PredictionType.Region,
                [new PredictionClaim("claim-region-bound01", "region", null, new PixelRegion(1910, 10, 20, 20))]),
        ]);

        Assert.Contains(CorpusValidation.ValidatePredictions(badPrediction, plan), error => error.Contains("out-of-bounds prediction region", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SplitPlannerJoinsExactNearDuplicatesAndSequencesAndIsStable()
    {
        var sharedHash = Hash("exact-content");
        var first = Sample("sample-split-exact-001", hash: sharedHash);
        var second = Sample("sample-split-exact-002", hash: sharedHash);
        var third = Sample("sample-split-near-0001", near: [sharedHash]);
        var scrollA = Sample("sample-split-scroll001", sequenceId: "sequence-split-scroll01", sessionId: "session-split-scroll-01", ordinal: 0, frame: 0);
        var scrollB = Sample("sample-split-scroll002", sequenceId: "sequence-split-scroll01", sessionId: "session-split-scroll-01", ordinal: 1, frame: 1, overlap: 0.5m);
        var manifest = Manifest(first, second, third, scrollA, scrollB);

        var units = SplitPlanner.BuildUnits(manifest.Samples);
        Assert.Equal(2, units.Count);
        var forward = SplitPlanner.Assign(manifest.Samples);
        var reverse = SplitPlanner.Assign(manifest.Samples.Reverse().ToArray());
        Assert.Equal(forward.OrderBy(pair => pair.Key), reverse.OrderBy(pair => pair.Key));
        Assert.Empty(SplitPlanner.ValidateUnits(manifest.Samples));
    }

    [Fact]
    public void PrivatePlannerLockRejectsMissingDuplicateReorderedAndMutatedPlanSamples()
    {
        var first = Sample("sample-plan-lock-00001");
        var second = Sample("sample-plan-lock-00002");
        var manifest = Manifest(first, second);
        var plan = PrivateRunPlanner.Create(manifest, "run-plan-lock-000001", "producer-plan-00001", "1.0", Now);
        Assert.Empty(CorpusValidation.ValidateRunPlan(plan, manifest, Now));

        var missing = plan with { Samples = [plan.Samples[0]] };
        Assert.Contains(CorpusValidation.ValidateRunPlan(missing, manifest, Now), error => error.Contains("missing", StringComparison.OrdinalIgnoreCase));

        var duplicate = plan with { Samples = [plan.Samples[0], plan.Samples[0]] };
        Assert.Contains(CorpusValidation.ValidateRunPlan(duplicate, manifest, Now), error => error.Contains("unique", StringComparison.OrdinalIgnoreCase));

        var reordered = plan with { Samples = plan.Samples.Reverse().ToArray() };
        Assert.Contains(CorpusValidation.ValidateRunPlan(reordered, manifest, Now), error => error.Contains("reordered", StringComparison.OrdinalIgnoreCase));

        var mutated = plan with
        {
            Samples = [plan.Samples[0] with { Split = Different(plan.Samples[0].Split) }, plan.Samples[1]],
        };
        Assert.Contains(CorpusValidation.ValidateRunPlan(mutated, manifest, Now), error => error.Contains("content-stable split", StringComparison.OrdinalIgnoreCase));

        var forgedLock = plan with { PlanLock = new string('f', 64) };
        Assert.Contains(CorpusValidation.ValidateRunPlan(forgedLock, manifest, Now), error => error.Contains("lock", StringComparison.OrdinalIgnoreCase));

        var wrongPolicy = plan with { PolicyVersion = "recognition-scorer-policy.v2" };
        Assert.Contains(CorpusValidation.ValidateRunPlan(wrongPolicy, manifest, Now), error => error.Contains("frozen policy", StringComparison.OrdinalIgnoreCase));

        var changedContext = plan with
        {
            Samples = [plan.Samples[0] with { Context = plan.Samples[0].Context with { Width = 1280 } }, plan.Samples[1]],
        };
        Assert.Contains(CorpusValidation.ValidateRunPlan(changedContext, manifest, Now), error => error.Contains("context", StringComparison.OrdinalIgnoreCase));

        var changedTruth = manifest with
        {
            Samples = [manifest.Samples[0] with
            {
                Truth = [manifest.Samples[0].Truth[0] with { Value = "different-canonical-answer" }],
            }, manifest.Samples[1]],
        };
        Assert.Contains(CorpusValidation.ValidateRunPlan(plan, changedTruth, Now), error => error.Contains("lock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NearDuplicateRunPlanCannotLeakAcrossSplits()
    {
        var first = Sample("sample-leak-first-0001");
        var second = Sample("sample-leak-second-001", near: [first.DecodedPixelSha256!]);
        var manifest = Manifest(first, second);
        var plan = PrivateRunPlanner.Create(manifest, "run-leak-check-000001", "producer-leak-00001", "1.0", Now);
        var leaked = plan with
        {
            Samples = [plan.Samples[0], plan.Samples[1] with { Split = Different(plan.Samples[0].Split) }],
        };

        Assert.Contains(CorpusValidation.ValidateRunPlan(leaked, manifest, Now), error => error.Contains("leaks across splits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PredictionValidationBindsRunMembershipIntentEvidenceStatusClaimsAndTiming()
    {
        var manifest = Manifest(Sample("sample-prediction-00001"));
        var plan = PrivateRunPlanner.Create(manifest, "run-prediction-00001", "producer-predict-0001", "1.0", Now);
        var sample = plan.Samples[0];
        var valid = new PredictionDocument(plan.RunId, plan.ProducerId, plan.ProducerVersion,
            [Prediction(sample, PredictionType.Item, [new PredictionClaim("claim-prediction-0001", "item", "known-item")])]);
        Assert.Empty(CorpusValidation.ValidatePredictions(valid, plan));

        var wrongRun = valid with { RunId = "run-prediction-other01" };
        Assert.Contains(CorpusValidation.ValidatePredictions(wrongRun, plan), error => error.Contains("run and producer", StringComparison.OrdinalIgnoreCase));

        var unknown = valid with { Predictions = [valid.Predictions[0] with { SampleId = "sample-unknown-000001" }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(unknown, plan), error => error.Contains("not a member", StringComparison.OrdinalIgnoreCase));

        var wrongIntent = valid with { Predictions = [valid.Predictions[0] with { Intent = BenchmarkIntent.HealthCharacter }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(wrongIntent, plan), error => error.Contains("intent", StringComparison.OrdinalIgnoreCase));

        var wrongEvidence = valid with { Predictions = [valid.Predictions[0] with { EvidenceClass = CorpusEvidenceClass.RealRaster }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(wrongEvidence, plan), error => error.Contains("evidence", StringComparison.OrdinalIgnoreCase));

        var badConfidence = valid with { Predictions = [valid.Predictions[0] with { Confidence = 1.1m }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(badConfidence, plan), error => error.Contains("confidence", StringComparison.OrdinalIgnoreCase));

        var badStatus = valid with { Predictions = [valid.Predictions[0] with { Status = PredictionStatus.Abstained }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(badStatus, plan), error => error.Contains("inconsistent", StringComparison.OrdinalIgnoreCase));

        var badTime = valid with { Predictions = [valid.Predictions[0] with { ElapsedMilliseconds = CorpusValidation.MaximumElapsedMilliseconds + 1 }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(badTime, plan), error => error.Contains("elapsed", StringComparison.OrdinalIgnoreCase));

        var duplicateResult = valid with { Predictions = [valid.Predictions[0], valid.Predictions[0] with
        {
            Claims = [new PredictionClaim("claim-prediction-0002", "item", "known-item")],
        }] };
        Assert.Contains(CorpusValidation.ValidatePredictions(duplicateResult, plan), error => error.Contains("repeats result type", StringComparison.OrdinalIgnoreCase));

        var duplicateClaim = valid with
        {
            Predictions =
            [
                valid.Predictions[0],
                Prediction(sample, PredictionType.Attribute, [new PredictionClaim("claim-prediction-0001", "attribute", "value")]),
            ],
        };
        Assert.Contains(CorpusValidation.ValidatePredictions(duplicateClaim, plan), error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PredictionJsonRejectsUnsupportedTypeInsteadOfIgnoringIt()
    {
        var manifest = Manifest(Sample("sample-json-predict-0001"));
        var plan = PrivateRunPlanner.Create(manifest, "run-json-predict-0001", "producer-json-pred001", "1.0", Now);
        var document = new PredictionDocument(plan.RunId, plan.ProducerId, plan.ProducerVersion,
            [Prediction(plan.Samples[0], PredictionType.Item, [new PredictionClaim("claim-json-predict001", "item", "known-item")])]);
        var validJson = PredictionsJson(document);
        Assert.Empty(CorpusValidation.ValidatePredictionsInterchange(validJson, RunPlanJson(plan)));
        var json = validJson.Replace("\"type\":\"item\"", "\"type\":\"enemy-position\"", StringComparison.Ordinal);

        Assert.Contains(CorpusValidation.ValidatePredictionsInterchange(json, RunPlanJson(plan)), error => error.Contains("unsupported prediction type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepeatedTruthCanMatchInLaterOverlapFrameOnlyOnceAndDuplicateOutputIsFalsePositive()
    {
        var component = FindTestComponent(seed =>
        {
            var sequenceId = $"sequence-score-overlap-{seed:0000}";
            var sessionId = $"session-score-overlap-{seed:0000}";
            return
            [
                Sample($"sample-score-overlap-a-{seed:0000}", sequenceId: sequenceId, sessionId: sessionId, ordinal: 0, frame: 0) with
                {
                    Truth = [new TruthClaim("truth-shared-overlap01", TruthState.Known, "item", "known-item")],
                },
                Sample($"sample-score-overlap-b-{seed:0000}", sequenceId: sequenceId, sessionId: sessionId, ordinal: 1, frame: 1, overlap: 0.5m) with
                {
                    Truth = [new TruthClaim("truth-shared-overlap01", TruthState.Known, "item", "known-item")],
                },
            ];
        });
        var first = component[0];
        var second = component[1];
        var manifest = Manifest(first, second);
        var plan = PrivateRunPlanner.Create(manifest, "run-score-overlap-001", "producer-score-00001", "1.0", Now);
        var secondOnly = new[]
        {
            Prediction(plan.Samples.Single(sample => sample.SampleId == second.SampleId), PredictionType.Item,
                [new PredictionClaim("claim-later-frame-0001", "item", "known-item")]),
        };
        var metrics = Slice(Score(manifest, plan, secondOnly));
        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(0, metrics.FalsePositives);
        Assert.Equal(1, metrics.MissingFrames);

        var duplicates = plan.Samples.Select((sample, index) => Prediction(sample, PredictionType.Item,
            [new PredictionClaim($"claim-overlap-duplicate-{index:0000}", "item", "known-item")])).ToArray();
        metrics = Slice(Score(manifest, plan, duplicates));
        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(1, metrics.FalsePositives);
        Assert.Equal(1, metrics.OverlapDeduplicationErrors);
        Assert.Equal(0, metrics.ReorderedFrames);
    }

    [Fact]
    public void SequenceOverlapErrorsStayScopedToTheSequenceThatDuplicatedTruth()
    {
        var a = FindTestComponent(seed =>
        [
            Repeated($"sample-sequence-a0-{seed:0000}", $"sequence-metric-a-{seed:0000}", $"session-metric-a-{seed:0000}", "truth-sequence-a-00001", 0, 0, 0),
            Repeated($"sample-sequence-a1-{seed:0000}", $"sequence-metric-a-{seed:0000}", $"session-metric-a-{seed:0000}", "truth-sequence-a-00001", 1, 1, 0.5m),
        ]);
        var b = FindTestComponent(seed =>
        [
            Repeated($"sample-sequence-b0-{seed:0000}", $"sequence-metric-b-{seed:0000}", $"session-metric-b-{seed:0000}", "truth-sequence-b-00001", 0, 0, 0),
            Repeated($"sample-sequence-b1-{seed:0000}", $"sequence-metric-b-{seed:0000}", $"session-metric-b-{seed:0000}", "truth-sequence-b-00001", 1, 1, 0.5m),
        ]);
        var (a0, a1, b0, b1) = (a[0], a[1], b[0], b[1]);
        var manifest = Manifest(a0, a1, b0, b1);
        var plan = PrivateRunPlanner.Create(manifest, "run-sequence-scope-001", "producer-sequence-001", "1.0", Now);
        var predictions = new[]
        {
            Prediction(plan.Samples.Single(sample => sample.SampleId == a0.SampleId), PredictionType.Item, [new PredictionClaim("claim-sequence-a-00001", "item", "known-item")]),
            Prediction(plan.Samples.Single(sample => sample.SampleId == a1.SampleId), PredictionType.Item, [new PredictionClaim("claim-sequence-a-00002", "item", "known-item")]),
            Prediction(plan.Samples.Single(sample => sample.SampleId == b1.SampleId), PredictionType.Item, [new PredictionClaim("claim-sequence-b-00001", "item", "known-item")]),
        };

        var metrics = Slice(Score(manifest, plan, predictions));
        Assert.Equal(1, metrics.OverlapDeduplicationErrors);

        var reversed = Slice(Score(manifest, plan, predictions.Reverse().ToArray()));
        Assert.Equal(1, reversed.ReorderedFrames);
    }

    [Fact]
    public void MetricsReportCoverageAccuracyRecallFalsePositiveRateF1AndThresholdDisposition()
    {
        var samples = FindIndependentTestSamples(30, "sample-metrics");
        var manifest = Manifest(samples);
        var plan = PrivateRunPlanner.Create(manifest, "run-metrics-report-0001", "producer-metrics-0001", "1.0", Now);
        var predictions = plan.Samples.Select((sample, index) => Prediction(sample, PredictionType.Item,
        [
            new PredictionClaim($"claim-metrics-{index:0000000001}", "item", index >= 27 ? "wrong-item" : "known-item"),
        ])).ToArray();
        var metrics = Slice(Score(manifest, plan, predictions));

        Assert.Equal("fail", metrics.Status);
        Assert.Equal(30, metrics.AttemptedKnownClaims);
        Assert.Equal(1m, metrics.Coverage);
        Assert.Equal(27, metrics.RecallNumerator);
        Assert.Equal(30, metrics.RecallDenominator);
        Assert.Equal(0.9m, metrics.Recall);
        Assert.Equal(27, metrics.AccuracyNumerator);
        Assert.Equal(33, metrics.AccuracyDenominator);
        Assert.Equal(27m / 33m, metrics.Accuracy);
        Assert.Equal(3, metrics.FalsePositiveNumerator);
        Assert.Equal(30, metrics.FalsePositiveDenominator);
        Assert.Equal(0.1m, metrics.FalsePositiveRate);
        Assert.Equal(0.9m, metrics.F1);
        Assert.Equal(30, metrics.PerformanceSampleCount);
        Assert.NotNull(metrics.MeanElapsedMilliseconds);
        Assert.NotNull(metrics.MaximumElapsedMilliseconds);
        Assert.InRange(metrics.ConfidenceIntervalLower, 0m, metrics.Recall);
        Assert.InRange(metrics.ConfidenceIntervalUpper, metrics.Recall, 1m);

        var passingPredictions = plan.Samples.Select((sample, index) => Prediction(sample, PredictionType.Item,
            [new PredictionClaim($"claim-passing-{index:0000000001}", "item", "known-item")])).ToArray();
        var passing = Slice(Score(manifest, plan, passingPredictions));
        Assert.Equal("pass", passing.Status);

        var underpoweredManifest = Manifest(FindIndependentTestSamples(29, "sample-underpowered"));
        var underpoweredPlan = PrivateRunPlanner.Create(
            underpoweredManifest,
            "run-underpowered-00001",
            "producer-underpowered-01",
            "1.0",
            Now);
        var underpoweredPredictions = underpoweredPlan.Samples.Select((sample, index) => Prediction(sample, PredictionType.Item,
            [new PredictionClaim($"claim-underpowered-{index:00000001}", "item", "known-item")])).ToArray();
        var underpowered = Slice(Score(underpoweredManifest, underpoweredPlan, underpoweredPredictions));
        Assert.Equal("insufficient-data", underpowered.Status);
    }

    [Fact]
    public void UnknownScopesExcludePredictionsAndMultiClaimMatchesAreNotConfidentWrong()
    {
        var sample = FindIndependentTestSamples(1, "sample-unknown-metric")[0] with
        {
            Truth =
            [
                new TruthClaim("truth-known-metric-0001", TruthState.Known, "item", "known-item"),
                new TruthClaim("truth-unknown-metric01", TruthState.Unknown, "attribute", null),
            ],
        };
        var manifest = Manifest(sample);
        var plan = PrivateRunPlanner.Create(manifest, "run-unknown-metric-001", "producer-unknown-0001", "1.0", Now);
        var predictions = new[]
        {
            Prediction(plan.Samples[0], PredictionType.Item,
            [
                new PredictionClaim("claim-correct-metric-001", "item", "known-item"),
                new PredictionClaim("claim-extra-wrong-00001", "item", "wrong-item"),
            ]),
            Prediction(plan.Samples[0], PredictionType.Attribute,
                [new PredictionClaim("claim-unknown-scope-001", "attribute", "not-adjudicable")]),
        };
        var metrics = Slice(Score(manifest, plan, predictions));

        Assert.Equal(1, metrics.Denominator);
        Assert.Equal(1, metrics.ExcludedUnknowns);
        Assert.Equal(1, metrics.ExcludedPredictionClaims);
        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(1, metrics.FalsePositives);
        Assert.Equal(0, metrics.ConfidentWrong);
        Assert.Equal(1m, metrics.Coverage);
        Assert.Equal("insufficient-data", metrics.Status);

        var wrongOnly = predictions.Select(prediction => prediction.Type == PredictionType.Item
            ? prediction with { Claims = [new PredictionClaim("claim-only-wrong-00001", "item", "wrong-item")] }
            : prediction).ToArray();
        Assert.Equal(1, Slice(Score(manifest, plan, wrongOnly)).ConfidentWrong);
    }

    [Fact]
    public void IndependentScorerRequiresAuthorizedEligibleTestSplitAtScoreTime()
    {
        var testSample = FindIndependentTestSamples(1, "sample-authorized-test", CorpusEvidenceClass.RealRaster)[0];
        var manifest = Manifest(testSample);
        var plan = PrivateRunPlanner.Create(manifest, "run-authorized-test-001", "producer-authorized-001", "1.0", Now);
        var prediction = Prediction(plan.Samples[0], PredictionType.Item,
            [new PredictionClaim("claim-authorized-test01", "item", "known-item")]);
        Assert.Equal(CorpusEvidenceClass.RealRaster, Score(manifest, plan, [prediction], CorpusEvidenceClass.RealRaster).EvidenceClass);

        var revoked = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                Consent = manifest.PrivateEvidence[0].Consent! with { RevocationState = "revoked" },
            }],
        };
        Assert.Throws<ArgumentException>(() => Score(revoked, plan, [prediction], CorpusEvidenceClass.RealRaster));

        var expired = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                Consent = manifest.PrivateEvidence[0].Consent! with { RetentionExpiresUtc = Now },
            }],
        };
        Assert.Throws<ArgumentException>(() => Score(expired, plan, [prediction], CorpusEvidenceClass.RealRaster));

        var reviewRejected = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                PrivacyReview = manifest.PrivateEvidence[0].PrivacyReview! with { State = "rejected" },
            }],
        };
        Assert.Throws<ArgumentException>(() => Score(reviewRejected, plan, [prediction], CorpusEvidenceClass.RealRaster));

        var benchmarkUseRemoved = manifest with
        {
            PrivateEvidence = [manifest.PrivateEvidence[0] with
            {
                Consent = manifest.PrivateEvidence[0].Consent! with { AllowedUses = ["train"] },
            }],
        };
        Assert.Throws<ArgumentException>(() => Score(benchmarkUseRemoved, plan, [prediction], CorpusEvidenceClass.RealRaster));

        var trainSample = FindIndependentSplitSamples(1, "sample-not-test", CorpusSplit.Train)[0];
        var trainManifest = Manifest(trainSample);
        var trainPlan = PrivateRunPlanner.Create(trainManifest, "run-not-test-split-001", "producer-not-test-001", "1.0", Now);
        var trainPrediction = Prediction(trainPlan.Samples[0], PredictionType.Item,
            [new PredictionClaim("claim-not-test-split01", "item", "known-item")]);
        Assert.Throws<ArgumentException>(() => Score(trainManifest, trainPlan, [trainPrediction]));
    }

    [Fact]
    public void AggregateRoundTripAndSemanticValidatorRejectTamperingMixingAndAssertedPrivacy()
    {
        var manifest = Manifest(FindIndependentTestSamples(30, "sample-aggregate"));
        var plan = PrivateRunPlanner.Create(manifest, "run-aggregate-roundtrip1", "producer-aggregate-001", "1.0", Now);
        var predictions = plan.Samples.Select((sample, index) => Prediction(sample, PredictionType.Item,
            [new PredictionClaim($"claim-aggregate-{index:00000001}", "item", "known-item")])).ToArray();
        var aggregate = Score(manifest, plan, predictions);
        Assert.True(aggregate.Privacy.SafeToPublish);
        Assert.Empty(AggregateResultValidation.Validate(aggregate, Thresholds()));

        var json = CorpusJson.SerializeAggregateResults(aggregate);
        var parsed = CorpusJson.ParseAggregateResults(json);
        Assert.NotNull(parsed.Value);
        Assert.Empty(parsed.Errors);
        Assert.Equal(json, CorpusJson.SerializeAggregateResults(parsed.Value!));
        Assert.Empty(AggregateResultValidation.ValidateInterchange(json, ThresholdsJson()));

        var loot = Slice(aggregate);
        var badArithmetic = aggregate with
        {
            Slices = aggregate.Slices.Select(slice => slice.Intent == BenchmarkIntent.LootDecision
                ? slice with { TruePositives = slice.TruePositives - 1 }
                : slice).ToArray(),
        };
        Assert.Contains(AggregateResultValidation.Validate(badArithmetic, Thresholds()),
            error => error.Contains("arithmetic", StringComparison.OrdinalIgnoreCase));

        var badRate = aggregate with
        {
            Slices = aggregate.Slices.Select(slice => slice.Intent == BenchmarkIntent.LootDecision
                ? slice with { Recall = 0.5m }
                : slice).ToArray(),
        };
        Assert.Contains(AggregateResultValidation.Validate(badRate, Thresholds()),
            error => error.Contains("rates", StringComparison.OrdinalIgnoreCase));

        var badStatus = aggregate with
        {
            Slices = aggregate.Slices.Select(slice => slice.Intent == BenchmarkIntent.LootDecision
                ? slice with { Status = "fail" }
                : slice).ToArray(),
        };
        Assert.Contains(AggregateResultValidation.Validate(badStatus, Thresholds()),
            error => error.Contains("status", StringComparison.OrdinalIgnoreCase));

        var duplicateIntent = aggregate with { Slices = aggregate.Slices.Take(aggregate.Slices.Count - 1).Append(loot).ToArray() };
        Assert.Contains(AggregateResultValidation.Validate(duplicateIntent, Thresholds()),
            error => error.Contains("unique", StringComparison.OrdinalIgnoreCase) || error.Contains("exactly one", StringComparison.OrdinalIgnoreCase));

        var mixedEvidence = aggregate with
        {
            Slices = aggregate.Slices.Select(slice => slice.Intent == BenchmarkIntent.LootDecision
                ? slice with { EvidenceClass = CorpusEvidenceClass.RealRaster }
                : slice).ToArray(),
        };
        Assert.Contains(AggregateResultValidation.Validate(mixedEvidence, Thresholds()),
            error => error.Contains("mix", StringComparison.OrdinalIgnoreCase));

        var privateExtension = JsonNode.Parse(json)!.AsObject();
        privateExtension["futureExtension"] = new JsonObject { ["sampleId"] = "sample-private-leak-001" };
        Assert.Contains(AggregateResultValidation.ValidateInterchange(privateExtension.ToJsonString(), ThresholdsJson()),
            error => error.Contains("sampleId", StringComparison.OrdinalIgnoreCase));

        var smallManifest = Manifest(FindIndependentTestSamples(1, "sample-unsafe-aggregate"));
        var smallPlan = PrivateRunPlanner.Create(smallManifest, "run-unsafe-aggregate01", "producer-unsafe-00001", "1.0", Now);
        var smallPrediction = Prediction(smallPlan.Samples[0], PredictionType.Item,
            [new PredictionClaim("claim-unsafe-aggregate1", "item", "known-item")]);
        var forgedPrivacy = Score(smallManifest, smallPlan, [smallPrediction]) with
        {
            Privacy = new AggregatePrivacy(true, CorpusValidation.FrozenMinimumIndependentSplitUnits, false),
        };
        Assert.Contains(AggregateResultValidation.Validate(forgedPrivacy, Thresholds()),
            error => error.Contains("recomputed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AggregateAndPolicySchemasRequireTypedMetricFieldsAndFrozenThresholds()
    {
        var schemaRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "schemas");
        using var aggregate = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "aggregate-results.v1.schema.json")));
        var sliceRequired = aggregate.RootElement.GetProperty("$defs").GetProperty("slice").GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var field in new[] { "excludedPredictionClaims", "accuracy", "recall", "falsePositiveRate", "f1", "sequence", "performance" })
        {
            var expected = field switch
            {
                "sequence" => "overlapDeduplicationErrors",
                "performance" => "performanceSampleCount",
                _ => field,
            };
            Assert.Contains(expected, sliceRequired);
        }

        var aggregateRequired = aggregate.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("runId", aggregateRequired);
        Assert.Contains("planLock", aggregateRequired);
        Assert.Contains("scoredUtc", aggregateRequired);

        using var thresholds = JsonDocument.Parse(File.ReadAllText(Path.Combine(schemaRoot, "thresholds.v1.schema.json")));
        var candidateRequired = thresholds.RootElement.GetProperty("properties").GetProperty("candidateThresholds").GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("minimumCoverage", candidateRequired);
        Assert.Contains("minimumF1", candidateRequired);
        Assert.Contains(CorpusValidation.ValidateThresholds(Thresholds() with { MinimumF1 = 0.1m }),
            error => error.Contains("exactly match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckedInSyntheticPlanIsSchemaShapedTruthFreeButNotSelfAuthorizing()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "examples", "synthetic-run-plan.v1.json");
        var json = File.ReadAllText(path);
        var parsed = CorpusJson.ParseRunPlan(json);

        Assert.NotNull(parsed.Value);
        Assert.Empty(parsed.Errors);
        Assert.DoesNotContain("truth", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decodedPixelSha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new string('0', 64), parsed.Value!.PlanLock);
    }

    [Fact]
    public void RepositoryPathsCannotBeCorpusRootsAndPixelBytesMustMatch()
    {
        Assert.Throws<InvalidOperationException>(() => CorpusValidation.RejectRepositoryPath(RepositoryRoot()));
        Assert.Throws<InvalidDataException>(() => CorpusValidation.RequireExpectedPixelHash(Hash("expected"), Encoding.UTF8.GetBytes("changed")));
    }

    [Fact]
    public void PrivateInputResolutionRejectsIntermediateLinksBackIntoAWorktree()
    {
        var temporary = Directory.CreateTempSubdirectory("recognition-corpus-link-");
        try
        {
            var link = Path.Combine(temporary.FullName, "linked-worktree");
            try
            {
                Directory.CreateSymbolicLink(link, RepositoryRoot());
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var linkedFixture = Path.Combine(link, "fixtures", "recognition-corpus", "examples", "synthetic-run-plan.v1.json");
            Assert.Throws<InvalidOperationException>(() => CorpusValidation.ResolvePrivateInputPath(linkedFixture));
        }
        finally
        {
            temporary.Delete(true);
        }
    }

    [Fact]
    public void HostileExtensionFixturesRejectRelativeTraversalAndOriginalNames()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "recognition-corpus", "hostile");
        var traversal = CorpusJson.ParseRunPlan(File.ReadAllText(Path.Combine(root, "relative-traversal-run-plan.v1.json")));
        Assert.Contains(traversal.Errors, error => error.Contains("relative traversal", StringComparison.OrdinalIgnoreCase));

        var originalName = CorpusJson.ParsePredictions(File.ReadAllText(Path.Combine(root, "original-name-predictions.v1.json")));
        Assert.Contains(originalName.Errors, error => error.Contains("originalName", StringComparison.OrdinalIgnoreCase));
    }

    private static CorpusSample Repeated(string sampleId, string sequenceId, string sessionId, string truthId, int ordinal, int frame, decimal overlap) =>
        Sample(sampleId, sequenceId: sequenceId, sessionId: sessionId, ordinal: ordinal, frame: frame, overlap: overlap) with
        {
            Truth = [new TruthClaim(truthId, TruthState.Known, "item", "known-item")],
        };

    private static CorpusSample RealSample(string sampleId)
    {
        var consentHash = Hash("consent-" + sampleId);
        var reviewHash = Hash("review-" + sampleId);
        return Sample(sampleId, CorpusEvidenceClass.RealRaster) with
        {
            ConsentHash = consentHash,
            PrivacyReviewHash = reviewHash,
        };
    }

    private static CorpusSample Sample(
        string sampleId,
        CorpusEvidenceClass evidenceClass = CorpusEvidenceClass.SyntheticRaster,
        string? hash = null,
        IReadOnlyList<string>? near = null,
        string? sequenceId = null,
        string? sessionId = null,
        int ordinal = 0,
        int frame = 0,
        decimal overlap = 0)
    {
        hash ??= evidenceClass == CorpusEvidenceClass.PostOcrEvidence ? null : Hash(sampleId);
        return new CorpusSample(
            sampleId,
            evidenceClass,
            hash,
            near ?? [],
            new string('0', 64),
            new CaptureContext(
                IndependentScorer.IntentId(BenchmarkIntent.LootDecision),
                sessionId ?? "session-" + sampleId,
                "correlation-" + sampleId,
                ordinal,
                null,
                null,
                null,
                null,
                null,
                [],
                null,
                null,
                "desktop",
                "user-screenshot",
                1920,
                1080,
                1m,
                "en-US",
                "1.0",
                "2.0"),
            new SequenceLineage(
                sequenceId ?? "sequence-" + sampleId,
                frame,
                "viewport-fixture-0001",
                "container-fixture-0001",
                overlap,
                null),
            [new TruthClaim("truth-" + sampleId, TruthState.Known, "item", "known-item")],
            null,
            null);
    }

    private static CorpusManifest Manifest(params CorpusSample[] source)
    {
        var units = SplitPlanner.BuildUnits(source);
        var unitBySample = units.SelectMany(unit => unit.Value.Select(sample => (sample.SampleId, Unit: unit.Key)))
            .ToDictionary(pair => pair.SampleId, pair => pair.Unit, StringComparer.Ordinal);
        var samples = source.Select(sample => sample with { DeclaredSplitUnitId = unitBySample[sample.SampleId] }).ToArray();
        var evidence = samples.Where(sample => sample.EvidenceClass != CorpusEvidenceClass.PostOcrEvidence).Select(sample =>
        {
            ConsentEvidence? consent = null;
            PrivacyReviewEvidence? review = null;
            if (sample.EvidenceClass == CorpusEvidenceClass.RealRaster)
            {
                consent = new ConsentEvidence(
                    "consent-authority-" + sample.SampleId,
                    sample.ConsentHash!,
                    ["benchmark"],
                    Now.AddDays(-2),
                    Future,
                    "active");
                review = new PrivacyReviewEvidence(
                    "review-authority-" + sample.SampleId,
                    sample.PrivacyReviewHash!,
                    "approved",
                    "not-required",
                    Now.AddDays(-1));
            }

            return new PrivateSampleEvidence(sample.SampleId, sample.DecodedPixelSha256, consent, review);
        }).ToArray();
        return new CorpusManifest("corpus-fixture-000001", CorpusValidation.NearDuplicateGraphVersion, samples, evidence);
    }

    private static ProducerPrediction Prediction(RunPlanSample sample, PredictionType type, IReadOnlyList<PredictionClaim> claims) =>
        new(sample.SampleId, sample.Intent, sample.EvidenceClass, type, PredictionStatus.Detected, 0.95m, claims, 10m);

    private static FrozenThresholds Thresholds() => new(
        CorpusValidation.FrozenPolicyVersion,
        CorpusValidation.FrozenMinimumIndependentSplitUnits,
        CorpusValidation.FrozenMinimumKnownClaims,
        CorpusValidation.FrozenMinimumCoverage,
        CorpusValidation.FrozenMaximumAbstentionRate,
        CorpusValidation.FrozenMaximumConfidentWrongRate,
        CorpusValidation.FrozenMinimumF1);

    private static AggregateResults Score(
        CorpusManifest manifest,
        RunPlan plan,
        IReadOnlyList<ProducerPrediction> predictions,
        CorpusEvidenceClass evidenceClass = CorpusEvidenceClass.SyntheticRaster) =>
        IndependentScorer.Score(
            manifest,
            plan,
            new PredictionDocument(plan.RunId, plan.ProducerId, plan.ProducerVersion, predictions),
            Thresholds(),
            evidenceClass,
            Now);

    private static SliceMetrics Slice(AggregateResults aggregate) => Assert.Single(
        aggregate.Slices,
        slice => slice.Intent == BenchmarkIntent.LootDecision && slice.EvidenceClass == CorpusEvidenceClass.SyntheticRaster);

    private static CorpusSample[] FindIndependentTestSamples(
        int count,
        string prefix,
        CorpusEvidenceClass evidenceClass = CorpusEvidenceClass.SyntheticRaster) =>
        FindIndependentSplitSamples(count, prefix, CorpusSplit.Test, evidenceClass);

    private static CorpusSample[] FindIndependentSplitSamples(
        int count,
        string prefix,
        CorpusSplit split,
        CorpusEvidenceClass evidenceClass = CorpusEvidenceClass.SyntheticRaster)
    {
        var result = new List<CorpusSample>(count);
        for (var candidate = 0; result.Count < count && candidate < 100_000; candidate++)
        {
            var id = $"{prefix}-{candidate:00000000}";
            var sample = evidenceClass == CorpusEvidenceClass.RealRaster
                ? RealSample(id)
                : Sample(id, evidenceClass);
            var manifest = Manifest(sample);
            var plan = PrivateRunPlanner.Create(manifest, "run-split-search-0001", "producer-split-search1", "1.0", Now);
            if (plan.Samples[0].Split == split)
            {
                result.Add(sample);
            }
        }

        return result.Count == count
            ? result.ToArray()
            : throw new InvalidOperationException($"Could not find {count} deterministic {split} samples.");
    }

    private static CorpusSample[] FindTestComponent(Func<int, CorpusSample[]> factory)
    {
        for (var seed = 0; seed < 10_000; seed++)
        {
            var samples = factory(seed);
            var manifest = Manifest(samples);
            var plan = PrivateRunPlanner.Create(manifest, "run-component-search-01", "producer-component-001", "1.0", Now);
            if (plan.Samples.All(sample => sample.Split == CorpusSplit.Test))
            {
                return samples;
            }
        }

        throw new InvalidOperationException("Could not find a deterministic Test-split component.");
    }

    private static CorpusSplit Different(CorpusSplit split) => split == CorpusSplit.Train ? CorpusSplit.Test : CorpusSplit.Train;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The recognition corpus test repository root could not be found.");
    }

    private static string Hash(string value) => CorpusValidation.CanonicalPixelHash(Encoding.UTF8.GetBytes(value));

    private static string ThresholdsJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = CorpusValidation.ThresholdsSchemaVersion,
        policyVersion = CorpusValidation.FrozenPolicyVersion,
        frozenBeforeTuning = true,
        minimumIndependentSplitUnits = CorpusValidation.FrozenMinimumIndependentSplitUnits,
        minimumKnownClaims = CorpusValidation.FrozenMinimumKnownClaims,
        candidateThresholds = new
        {
            minimumCoverage = CorpusValidation.FrozenMinimumCoverage,
            maximumAbstentionRate = CorpusValidation.FrozenMaximumAbstentionRate,
            maximumConfidentWrongRate = CorpusValidation.FrozenMaximumConfidentWrongRate,
            minimumF1 = CorpusValidation.FrozenMinimumF1,
        },
        unsupportedDisposition = "insufficient-data",
    });

    private static string ManifestJson(CorpusManifest manifest) => JsonSerializer.Serialize(new
    {
        schemaVersion = CorpusValidation.ManifestSchemaVersion,
        corpusId = manifest.CorpusId,
        nearDuplicateGraphVersion = manifest.NearDuplicateGraphVersion,
        samples = manifest.Samples.Select(sample => new
        {
            sampleId = sample.SampleId,
            evidenceClass = Evidence(sample.EvidenceClass),
            splitUnitId = sample.DeclaredSplitUnitId,
            content = new { decodedPixelSha256 = sample.DecodedPixelSha256, nearDuplicateIds = sample.NearDuplicateHashes },
            consentHash = sample.ConsentHash,
            privacyReviewHash = sample.PrivacyReviewHash,
            provenance = Context(sample.Context),
            lineage = Lineage(sample.Lineage),
            truth = new
            {
                claims = sample.Truth.Select(claim => new
                {
                    truthId = claim.TruthId,
                    state = claim.State == TruthState.Known ? "known" : "unknown",
                    kind = claim.Kind,
                    value = claim.Value,
                    region = claim.Region is null ? null : Region(claim.Region),
                }),
            },
        }),
        privateEvidence = manifest.PrivateEvidence.Select(evidence => new
        {
            sampleId = evidence.SampleId,
            observedDecodedPixelSha256 = evidence.ObservedDecodedPixelSha256,
            consent = evidence.Consent is null ? null : new
            {
                schemaVersion = "consent.v1",
                consentId = evidence.Consent.ConsentId,
                consentHash = evidence.Consent.ConsentHash,
                allowedUses = evidence.Consent.AllowedUses,
                consentedUtc = Utc(evidence.Consent.ConsentedUtc),
                retention = new { expiresUtc = Utc(evidence.Consent.RetentionExpiresUtc) },
                revocation = new { state = evidence.Consent.RevocationState },
            },
            privacyReview = evidence.PrivacyReview is null ? null : new
            {
                schemaVersion = "privacy-review.v1",
                reviewId = evidence.PrivacyReview.ReviewId,
                reviewHash = evidence.PrivacyReview.ReviewHash,
                state = evidence.PrivacyReview.State,
                reviewedUtc = Utc(evidence.PrivacyReview.ReviewedUtc),
                redaction = evidence.PrivacyReview.RedactionState,
            },
        }),
    });

    private static string RunPlanJson(RunPlan plan) => JsonSerializer.Serialize(new
    {
        schemaVersion = CorpusValidation.RunPlanSchemaVersion,
        runId = plan.RunId,
        producer = new { id = plan.ProducerId, version = plan.ProducerVersion },
        policyVersion = plan.PolicyVersion,
        corpusId = plan.CorpusId,
        nearDuplicateGraphVersion = plan.NearDuplicateGraphVersion,
        planLock = plan.PlanLock,
        samples = plan.Samples.Select(sample => new
        {
            sampleId = sample.SampleId,
            split = Split(sample.Split),
            intent = Intent(sample.Intent),
            evidenceClass = Evidence(sample.EvidenceClass),
            provenance = Context(sample.Context),
            lineage = Lineage(sample.Lineage),
        }),
    });

    private static string PredictionsJson(PredictionDocument document) => JsonSerializer.Serialize(new
    {
        schemaVersion = CorpusValidation.PredictionsSchemaVersion,
        runId = document.RunId,
        producer = new { id = document.ProducerId, version = document.ProducerVersion },
        predictions = document.Predictions.Select(prediction => new
        {
            sampleId = prediction.SampleId,
            intent = Intent(prediction.Intent),
            evidenceClass = Evidence(prediction.EvidenceClass),
            type = Type(prediction.Type),
            status = prediction.Status == PredictionStatus.Detected ? "detected" : prediction.Status == PredictionStatus.Abstained ? "abstained" : "unavailable",
            confidence = prediction.Confidence,
            claims = prediction.Claims.Select(claim => new
            {
                claimId = claim.ClaimId,
                kind = claim.Kind,
                value = claim.Value,
                region = claim.Region is null ? null : Region(claim.Region),
            }),
            performance = new { elapsedMilliseconds = prediction.ElapsedMilliseconds },
        }),
    });

    private static object Context(CaptureContext context) => new
    {
        schemaVersion = "provenance.v1",
        captureIntentId = context.CaptureIntentId,
        sessionId = context.SessionId,
        correlationId = context.CorrelationId,
        captureOrdinal = context.CaptureOrdinal,
        workspaceId = context.WorkspaceId,
        profileId = context.ProfileId,
        mapId = context.MapId,
        floorId = context.FloorId,
        planId = context.PlanId,
        objectiveIds = context.ObjectiveIds,
        selectedReference = context.SelectedReference,
        priorScanReference = context.PriorScanReference,
        deviceClass = context.DeviceClass,
        surface = context.Surface,
        resolution = new { width = context.Width, height = context.Height },
        uiScale = context.UiScale,
        locale = context.Locale,
        gameVersion = context.GameVersion,
        companionUiVersion = context.CompanionUiVersion,
    };

    private static object Lineage(SequenceLineage lineage) => new
    {
        sequenceId = lineage.SequenceId,
        frameOrdinal = lineage.FrameOrdinal,
        viewportId = lineage.ViewportId,
        containerIdentity = lineage.ContainerIdentity,
        overlapWithPrevious = lineage.OverlapWithPrevious,
        parentContainerIdentity = lineage.ParentContainerIdentity,
    };

    private static object Region(PixelRegion region) => new { x = region.X, y = region.Y, width = region.Width, height = region.Height };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string Evidence(CorpusEvidenceClass value) => value switch
    {
        CorpusEvidenceClass.RealRaster => "real-raster",
        CorpusEvidenceClass.SyntheticRaster => "synthetic-raster",
        CorpusEvidenceClass.PostOcrEvidence => "post-ocr-evidence",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Split(CorpusSplit value) => value.ToString().ToLowerInvariant();

    private static string Intent(BenchmarkIntent value) => value switch
    {
        BenchmarkIntent.LootDecision => "loot-decision",
        BenchmarkIntent.FullStash => "full-stash",
        BenchmarkIntent.Ammo => "ammo",
        BenchmarkIntent.Keys => "keys",
        BenchmarkIntent.QuestItems => "quest-items",
        BenchmarkIntent.MapExtractsTimers => "map-extracts-timers",
        BenchmarkIntent.HealthCharacter => "health-character",
        BenchmarkIntent.AutoDetect => "auto-detect",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Type(PredictionType value) => value switch
    {
        PredictionType.Context => "context",
        PredictionType.Region => "region",
        PredictionType.GridCell => "grid-cell",
        PredictionType.Item => "item",
        PredictionType.Attribute => "attribute",
        PredictionType.Extract => "extract",
        PredictionType.Timer => "timer",
        PredictionType.Health => "health",
        PredictionType.Recommendation => "recommendation",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
