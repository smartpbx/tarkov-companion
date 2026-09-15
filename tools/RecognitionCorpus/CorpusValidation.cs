using System.Security.Cryptography;
using System.Text.Json;

namespace TarkovCompanion.RecognitionCorpus;

public static class CorpusValidation
{
    public const string ManifestSchemaVersion = "manifest.v1";
    public const string RunPlanSchemaVersion = "run-plan.v1";
    public const string PredictionsSchemaVersion = "predictions.v1";
    public const string AggregateResultsSchemaVersion = "aggregate-results.v1";
    public const string ThresholdsSchemaVersion = "thresholds.v1";
    public const string FrozenPolicyVersion = "recognition-scorer-policy.v1";
    public const string NearDuplicateGraphVersion = "phash-graph.v1";
    public const decimal MaximumElapsedMilliseconds = 120_000m;
    public const int FrozenMinimumIndependentSplitUnits = 5;
    public const int FrozenMinimumKnownClaims = 30;
    public const decimal FrozenMinimumCoverage = 0.9m;
    public const decimal FrozenMaximumAbstentionRate = 0.2m;
    public const decimal FrozenMaximumConfidentWrongRate = 0.05m;
    public const decimal FrozenMinimumF1 = 0.85m;

    private static readonly HashSet<string> ForbiddenInterchangeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "filename", "fileName", "sourceFilename", "sourcePath", "sourceName", "originalName", "originalFileName", "originalFilename",
        "originalPath", "originalUri", "absolutePath", "path", "pixels", "ocrText", "truth", "labels",
        "consent", "privacyReview", "privateEvidence", "decodedPixelSha256", "observedDecodedPixelSha256", "nearDuplicateHashes",
    };

    private static readonly HashSet<string> ForbiddenPrivateManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "filename", "fileName", "sourceFilename", "sourcePath", "sourceName", "originalName", "originalFileName", "originalFilename",
        "originalPath", "originalUri", "absolutePath", "path", "pixels", "ocrText", "consentRecord",
    };

    private static readonly HashSet<string> ForbiddenAggregateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sampleId", "claimId", "truthId", "claims", "predictions", "perSample", "perSampleResults", "sampleResults",
        "provenance", "lineage", "privateEvidence",
        "consent", "privacyReview", "decodedPixelSha256", "observedDecodedPixelSha256",
    };

    private static readonly HashSet<string> AllowedUses = new(StringComparer.Ordinal)
    {
        "benchmark", "train", "tune",
    };

    private static readonly HashSet<string> DeviceClasses = new(StringComparer.Ordinal)
    {
        "desktop", "tablet", "phone", "unknown",
    };

    private static readonly HashSet<string> Surfaces = new(StringComparer.Ordinal)
    {
        "companion-window", "paired-device", "user-screenshot", "unknown",
    };

    public static IReadOnlyList<string> ValidateManifest(CorpusManifest manifest, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (!OpaqueId(manifest.CorpusId))
        {
            errors.Add("A manifest requires an opaque corpus id.");
        }

        if (!string.Equals(manifest.NearDuplicateGraphVersion, NearDuplicateGraphVersion, StringComparison.Ordinal))
        {
            errors.Add($"A manifest requires the frozen {NearDuplicateGraphVersion} near-duplicate graph.");
        }

        if (manifest.Samples is null || manifest.Samples.Count == 0)
        {
            return [.. errors, "A manifest requires at least one sample."];
        }

        if (manifest.PrivateEvidence is null)
        {
            return [.. errors, "A manifest requires a private-evidence collection."];
        }

        var sampleIds = new HashSet<string>(StringComparer.Ordinal);
        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        var evidenceBySample = new Dictionary<string, PrivateSampleEvidence>(StringComparer.Ordinal);
        foreach (var evidence in manifest.PrivateEvidence)
        {
            if (evidence is null || !OpaqueId(evidence.SampleId) || !evidenceBySample.TryAdd(evidence.SampleId, evidence))
            {
                errors.Add("Private evidence must name one unique opaque sample id.");
            }
        }

        var sequenceFrames = new Dictionary<string, List<CorpusSample>>(StringComparer.Ordinal);
        var truthSequences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sample in manifest.Samples)
        {
            if (sample is null || sample.Context is null || sample.Lineage is null || sample.Truth is null || sample.NearDuplicateHashes is null)
            {
                errors.Add("A manifest cannot contain null samples or null required members.");
                continue;
            }

            if (!sampleIds.Add(sample.SampleId) || !OpaqueId(sample.SampleId))
            {
                errors.Add("Sample ids must be unique opaque identifiers.");
            }

            if (!Enum.IsDefined(sample.EvidenceClass))
            {
                errors.Add($"Sample {sample.SampleId} has an undefined evidence class.");
            }

            var isRaster = sample.EvidenceClass is CorpusEvidenceClass.RealRaster or CorpusEvidenceClass.SyntheticRaster;
            if (isRaster)
            {
                if (!Sha256(sample.DecodedPixelSha256))
                {
                    errors.Add($"Raster sample {sample.SampleId} requires a canonical decoded-pixel SHA-256.");
                }
                else
                {
                    contentHashes.Add(sample.DecodedPixelSha256!);
                }

                if (!evidenceBySample.TryGetValue(sample.SampleId, out var privateEvidence))
                {
                    errors.Add($"Raster sample {sample.SampleId} lacks private imported-content evidence.");
                }
                else
                {
                    ValidateImportedContent(sample, privateEvidence, nowUtc, errors);
                }
            }
            else
            {
                if (sample.DecodedPixelSha256 is not null || sample.NearDuplicateHashes.Count != 0)
                {
                    errors.Add($"Post-OCR sample {sample.SampleId} must not claim raster hashes.");
                }

                if (evidenceBySample.ContainsKey(sample.SampleId))
                {
                    errors.Add($"Post-OCR sample {sample.SampleId} must not carry private raster evidence.");
                }

                if (sample.ConsentHash is not null || sample.PrivacyReviewHash is not null)
                {
                    errors.Add($"Post-OCR sample {sample.SampleId} must not carry consent or privacy summaries.");
                }
            }

            if (sample.NearDuplicateHashes.Count != sample.NearDuplicateHashes.Distinct(StringComparer.Ordinal).Count() ||
                sample.NearDuplicateHashes.Any(hash => !Sha256(hash) || string.Equals(hash, sample.DecodedPixelSha256, StringComparison.Ordinal)))
            {
                errors.Add($"Sample {sample.SampleId} has duplicate, self-referential, or malformed perceptual-near-duplicate hashes.");
            }

            ValidateContext(sample.SampleId, sample.Context, errors);
            ValidateLineage(sample.SampleId, sample.Lineage, errors);
            if (!sequenceFrames.TryGetValue(sample.Lineage.SequenceId, out var frames))
            {
                frames = [];
                sequenceFrames.Add(sample.Lineage.SequenceId, frames);
            }

            frames.Add(sample);
            if (sample.Truth.Count == 0)
            {
                errors.Add($"Sample {sample.SampleId} requires at least one explicit known or unknown truth claim.");
            }

            var truthIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var truth in sample.Truth)
            {
                if (truth is null || !truthIds.Add(truth.TruthId) || !OpaqueId(truth.TruthId) ||
                    !PredictionTypeNames.TryParse(truth.Kind, out _) ||
                    (truth.State == TruthState.Known && !KnownTruthHasValue(truth)) ||
                    (truth.State == TruthState.Unknown && (truth.Value is not null || truth.Region is not null)) ||
                    !Enum.IsDefined(truth.State))
                {
                    errors.Add($"Sample {sample.SampleId} has invalid truth-with-unknowns.");
                    continue;
                }

                if (truth.Region is not null)
                {
                    ValidateRegion(sample.SampleId, truth.Region, sample.Context, "truth", errors);
                }

                if (truthSequences.TryGetValue(truth.TruthId, out var priorSequence) &&
                    !string.Equals(priorSequence, sample.Lineage.SequenceId, StringComparison.Ordinal))
                {
                    errors.Add($"Truth {truth.TruthId} cannot cross capture sequences.");
                }
                else
                {
                    truthSequences[truth.TruthId] = sample.Lineage.SequenceId;
                }
            }
        }

        foreach (var evidenceId in evidenceBySample.Keys.Where(id => !sampleIds.Contains(id)))
        {
            errors.Add($"Private evidence names unknown sample {evidenceId}.");
        }

        foreach (var sample in manifest.Samples.Where(sample => sample is not null))
        {
            if (sample.NearDuplicateHashes.Any(hash => !contentHashes.Contains(hash)))
            {
                errors.Add($"Sample {sample.SampleId} has a near-duplicate edge to content absent from the manifest.");
            }
        }

        foreach (var (_, frames) in sequenceFrames)
        {
            var ordered = frames.OrderBy(frame => frame.Lineage.FrameOrdinal).ToArray();
            if (!frames.SequenceEqual(ordered))
            {
                errors.Add($"Sequence {ordered[0].Lineage.SequenceId} frames are reordered in the manifest.");
            }

            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].Lineage.FrameOrdinal != index ||
                    (index == 0 && ordered[index].Lineage.OverlapWithPrevious != 0))
                {
                    errors.Add($"Sequence {ordered[index].Lineage.SequenceId} requires contiguous frames from zero and zero first-frame overlap.");
                    break;
                }
            }

            foreach (var repeated in ordered.SelectMany(sample => sample.Truth.Select(truth => (sample, truth)))
                         .GroupBy(pair => pair.truth.TruthId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                if (repeated.Select(pair => (pair.truth.State, pair.truth.Kind, pair.truth.Value, pair.truth.Region)).Distinct().Skip(1).Any())
                {
                    errors.Add($"Sequence {ordered[0].Lineage.SequenceId} repeats truth {repeated.Key} with conflicting values.");
                }

                if (repeated.Skip(1).Any(pair => pair.sample.Lineage.OverlapWithPrevious <= 0))
                {
                    errors.Add($"Sequence {ordered[0].Lineage.SequenceId} repeats truth {repeated.Key} outside an overlapping frame.");
                }
            }
        }

        foreach (var session in manifest.Samples.Where(sample => sample is not null && sample.Context is not null)
                     .GroupBy(sample => sample.Context.SessionId, StringComparer.Ordinal))
        {
            var ordinals = session.Select(sample => sample.Context.CaptureOrdinal).OrderBy(ordinal => ordinal).ToArray();
            if (ordinals.Where((ordinal, index) => ordinal != index).Any())
            {
                errors.Add($"Capture session {session.Key} requires unique contiguous capture ordinals from zero.");
            }
        }

        if (errors.All(error => !error.Contains("null", StringComparison.OrdinalIgnoreCase)))
        {
            errors.AddRange(SplitPlanner.ValidateUnits(manifest.Samples));
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateRunPlan(RunPlan plan, CorpusManifest privateManifest, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(privateManifest);
        var errors = ValidateManifest(privateManifest, nowUtc).Select(error => $"Private plan context: {error}").ToList();
        if (!OpaqueId(plan.RunId) || !OpaqueId(plan.ProducerId) || !BoundedToken(plan.ProducerVersion))
        {
            errors.Add("A run plan requires opaque run/producer ids and a bounded producer version.");
        }

        if (!string.Equals(plan.PolicyVersion, FrozenPolicyVersion, StringComparison.Ordinal))
        {
            errors.Add($"A run plan requires frozen policy {FrozenPolicyVersion}.");
        }

        if (!string.Equals(plan.CorpusId, privateManifest.CorpusId, StringComparison.Ordinal) ||
            !string.Equals(plan.NearDuplicateGraphVersion, privateManifest.NearDuplicateGraphVersion, StringComparison.Ordinal))
        {
            errors.Add("A run plan must name its authorized private corpus and near-duplicate graph.");
        }

        if (plan.Samples is null || plan.Samples.Count == 0)
        {
            return [.. errors, "A run plan requires at least one sample."];
        }

        if (errors.Any(error => error.StartsWith("Private plan context:", StringComparison.Ordinal)))
        {
            return errors;
        }

        var planned = PrivateRunPlanner.PlannedSamples(privateManifest);
        var plannedById = planned.ToDictionary(sample => sample.SampleId, StringComparer.Ordinal);
        var expected = planned.Where(sample => sample.ConsentPermitsSplit).ToArray();
        var actualIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in plan.Samples)
        {
            if (sample is null || !OpaqueId(sample.SampleId) || !actualIds.Add(sample.SampleId))
            {
                errors.Add("Run-plan sample ids must be unique opaque identifiers.");
                continue;
            }

            ValidateContext(sample.SampleId, sample.Context, errors);
            ValidateLineage(sample.SampleId, sample.Lineage, errors);
        }

        var expectedIds = expected.Select(sample => sample.SampleId).ToArray();
        var actualOrderedIds = plan.Samples.Where(sample => sample is not null).Select(sample => sample.SampleId).ToArray();
        foreach (var missing in expectedIds.Except(actualOrderedIds, StringComparer.Ordinal))
        {
            errors.Add($"Run plan is missing authorized sample {missing}.");
        }

        foreach (var extra in actualOrderedIds.Except(expectedIds, StringComparer.Ordinal))
        {
            errors.Add(plannedById.TryGetValue(extra, out var withheld)
                ? $"Run plan names sample {extra} in split {withheld.Split}, which its private consent does not permit."
                : $"Run plan names unauthorized sample {extra}.");
        }

        if (expectedIds.Length == actualOrderedIds.Length && !expectedIds.SequenceEqual(actualOrderedIds, StringComparer.Ordinal))
        {
            errors.Add("Run-plan samples are reordered from the private planner's canonical order.");
        }

        var expectedById = expected.ToDictionary(sample => sample.SampleId, StringComparer.Ordinal);
        foreach (var actual in plan.Samples.Where(sample => sample is not null))
        {
            if (!expectedById.TryGetValue(actual.SampleId, out var expectedSample))
            {
                continue;
            }

            if (actual.Split != expectedSample.Split || actual.Intent != expectedSample.Intent ||
                actual.EvidenceClass != expectedSample.EvidenceClass ||
                !ContextEquals(actual.Context, expectedSample.Context) || actual.Lineage != expectedSample.Lineage)
            {
                errors.Add($"Run-plan sample {actual.SampleId} does not match its private context, lineage, intent, evidence class, or content-stable split.");
            }
        }

        foreach (var splitUnit in plan.Samples.Where(sample => sample is not null)
                     .GroupBy(sample => plannedById.TryGetValue(sample.SampleId, out var item) ? item.SplitUnitId : sample.SampleId, StringComparer.Ordinal))
        {
            if (splitUnit.Select(sample => sample.Split).Distinct().Skip(1).Any())
            {
                errors.Add($"Exact, near-duplicate, or sequence split unit {splitUnit.Key} leaks across splits.");
            }
        }

        var expectedLock = PrivateRunPlanner.ComputeLock(plan.RunId, plan.ProducerId, plan.ProducerVersion, privateManifest, expected);
        if (!Sha256(plan.PlanLock) || !string.Equals(plan.PlanLock, expectedLock, StringComparison.Ordinal))
        {
            errors.Add("Run-plan lock does not match the authorized private content graph and complete canonical plan.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidatePredictions(PredictionDocument document, RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        if (!OpaqueId(plan.RunId) || !OpaqueId(plan.ProducerId) || !BoundedToken(plan.ProducerVersion) ||
            !string.Equals(plan.PolicyVersion, FrozenPolicyVersion, StringComparison.Ordinal) ||
            !OpaqueId(plan.CorpusId) || !string.Equals(plan.NearDuplicateGraphVersion, NearDuplicateGraphVersion, StringComparison.Ordinal) ||
            !Sha256(plan.PlanLock) || plan.Samples is null || plan.Samples.Count == 0)
        {
            errors.Add("Prediction validation requires a structurally valid frozen run-plan context.");
        }

        if (!string.Equals(document.RunId, plan.RunId, StringComparison.Ordinal) ||
            !string.Equals(document.ProducerId, plan.ProducerId, StringComparison.Ordinal) ||
            !string.Equals(document.ProducerVersion, plan.ProducerVersion, StringComparison.Ordinal))
        {
            errors.Add("Predictions must match the run and producer identity/version in the run plan.");
        }

        // A run id is chosen by whoever emits the plan and can be reused; the lock commits to the
        // private graph, membership, context, and truth the producer was actually handed. Output
        // from a superseded plan under the same run id therefore cannot be scored against a new one.
        if (!Sha256(document.PlanLock) || !string.Equals(document.PlanLock, plan.PlanLock, StringComparison.Ordinal))
        {
            errors.Add("Predictions must be bound to the exact run-plan lock they were produced from.");
        }

        if (document.Predictions is null || document.Predictions.Count == 0)
        {
            return [.. errors, "A prediction document requires at least one explicit result."];
        }

        var planned = new Dictionary<string, RunPlanSample>(StringComparer.Ordinal);
        foreach (var sample in plan.Samples ?? [])
        {
            if (sample is null || !OpaqueId(sample.SampleId) || !planned.TryAdd(sample.SampleId, sample))
            {
                errors.Add("Prediction run-plan context requires unique opaque sample ids.");
                continue;
            }

            ValidateContext(sample.SampleId, sample.Context, errors);
            ValidateLineage(sample.SampleId, sample.Lineage, errors);
            if (!Enum.IsDefined(sample.Split) || !Enum.IsDefined(sample.Intent) || !Enum.IsDefined(sample.EvidenceClass))
            {
                errors.Add($"Prediction run-plan sample {sample.SampleId} has an undefined split, intent, or evidence class.");
            }
        }

        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        var resultKeys = new HashSet<(string SampleId, PredictionType Type)>();
        foreach (var prediction in document.Predictions)
        {
            if (prediction is null || !OpaqueId(prediction.SampleId) || prediction.Claims is null)
            {
                errors.Add("Predictions cannot contain null results or required members.");
                continue;
            }

            if (!planned.TryGetValue(prediction.SampleId, out var sample))
            {
                errors.Add($"Prediction names sample {prediction.SampleId}, which is not a member of run {plan.RunId}.");
                continue;
            }

            if (!resultKeys.Add((prediction.SampleId, prediction.Type)))
            {
                errors.Add($"Prediction sample {prediction.SampleId} repeats result type {PredictionTypeNames.Format(prediction.Type)}.");
            }

            if (prediction.Intent != sample.Intent || prediction.EvidenceClass != sample.EvidenceClass)
            {
                errors.Add($"Prediction sample {prediction.SampleId} does not match its planned intent and evidence class.");
            }

            if (!Enum.IsDefined(prediction.Intent) || !Enum.IsDefined(prediction.EvidenceClass) ||
                !Enum.IsDefined(prediction.Type) || !Enum.IsDefined(prediction.Status) ||
                prediction.Confidence is < 0 or > 1 || prediction.ElapsedMilliseconds is < 0 or > MaximumElapsedMilliseconds)
            {
                errors.Add($"Prediction sample {prediction.SampleId} has an invalid enum, confidence, or elapsed-time bound.");
            }

            if ((prediction.Status == PredictionStatus.Detected && prediction.Claims.Count == 0) ||
                (prediction.Status != PredictionStatus.Detected && (prediction.Claims.Count != 0 || prediction.Confidence != 0)))
            {
                errors.Add($"Prediction sample {prediction.SampleId} has claims or confidence inconsistent with status {prediction.Status}.");
            }

            if (!Enum.IsDefined(prediction.Type))
            {
                continue;
            }

            var expectedKind = PredictionTypeNames.Format(prediction.Type);
            foreach (var claim in prediction.Claims)
            {
                if (claim is null || !OpaqueId(claim.ClaimId) || !claimIds.Add(claim.ClaimId) ||
                    !string.Equals(claim.Kind, expectedKind, StringComparison.Ordinal) ||
                    (prediction.Type == PredictionType.Region && (claim.Region is null || claim.Value is not null)) ||
                    (prediction.Type != PredictionType.Region &&
                     (string.IsNullOrWhiteSpace(claim.Value) || claim.Value.Length > 4096)))
                {
                    errors.Add($"Prediction sample {prediction.SampleId} has an invalid, duplicate, or type-inconsistent claim.");
                    continue;
                }

                if (claim.Region is not null)
                {
                    ValidateRegion(prediction.SampleId, claim.Region, sample.Context, "prediction", errors);
                }
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateThresholds(FrozenThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var errors = new List<string>();
        if (!string.Equals(thresholds.PolicyVersion, FrozenPolicyVersion, StringComparison.Ordinal))
        {
            errors.Add($"Thresholds require frozen policy {FrozenPolicyVersion}.");
        }

        if (thresholds.MinimumIndependentSplitUnits != FrozenMinimumIndependentSplitUnits ||
            thresholds.MinimumKnownClaims != FrozenMinimumKnownClaims ||
            thresholds.MinimumCoverage != FrozenMinimumCoverage ||
            thresholds.MaximumAbstentionRate != FrozenMaximumAbstentionRate ||
            thresholds.MaximumConfidentWrongRate != FrozenMaximumConfidentWrongRate ||
            thresholds.MinimumF1 != FrozenMinimumF1)
        {
            errors.Add("Threshold counts and rates must exactly match the frozen recognition-release candidate policy.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidatePrivateManifestInterchange(string json, DateTimeOffset nowUtc)
    {
        var parsed = CorpusJson.ParseManifest(json);
        if (parsed.Value is null)
        {
            return parsed.Errors;
        }

        return [.. parsed.Errors, .. ValidateManifest(parsed.Value, nowUtc)];
    }

    public static IReadOnlyList<string> ValidateRunPlanInterchange(string json, string privateManifestJson, DateTimeOffset nowUtc)
    {
        var parsedPlan = CorpusJson.ParseRunPlan(json);
        var parsedManifest = CorpusJson.ParseManifest(privateManifestJson);
        if (parsedPlan.Value is null || parsedManifest.Value is null)
        {
            return [.. parsedPlan.Errors, .. parsedManifest.Errors.Select(error => $"Private plan context: {error}")];
        }

        return [.. parsedPlan.Errors, .. parsedManifest.Errors.Select(error => $"Private plan context: {error}"), .. ValidateRunPlan(parsedPlan.Value, parsedManifest.Value, nowUtc)];
    }

    public static IReadOnlyList<string> ValidatePredictionsInterchange(string json, string runPlanJson)
    {
        var parsedPredictions = CorpusJson.ParsePredictions(json);
        var parsedPlan = CorpusJson.ParseRunPlan(runPlanJson);
        if (parsedPredictions.Value is null || parsedPlan.Value is null)
        {
            return [.. parsedPredictions.Errors, .. parsedPlan.Errors.Select(error => $"Run-plan context: {error}")];
        }

        return [.. parsedPredictions.Errors, .. parsedPlan.Errors.Select(error => $"Run-plan context: {error}"), .. ValidatePredictions(parsedPredictions.Value, parsedPlan.Value)];
    }

    public static string CanonicalPixelHash(ReadOnlySpan<byte> decodedPixels) =>
        Convert.ToHexStringLower(SHA256.HashData(decodedPixels));

    public static void RequireExpectedPixelHash(string expectedHash, ReadOnlySpan<byte> decodedPixels)
    {
        if (!Sha256(expectedHash) || !string.Equals(expectedHash, CanonicalPixelHash(decodedPixels), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decoded pixels changed from the manifest's canonical SHA-256.");
        }
    }

    public static IReadOnlyList<string> VerifyRealEligibility(
        CorpusSample sample,
        PrivateSampleEvidence privateEvidence,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(privateEvidence);
        var errors = new List<string>();
        ValidateImportedContent(sample, privateEvidence, nowUtc, errors);
        return errors;
    }

    internal static void ValidateContext(string sampleId, CaptureContext? context, ICollection<string> errors)
    {
        if (context is null || !OpaqueId(context.CaptureIntentId) ||
            !Enum.GetValues<BenchmarkIntent>().Any(intent => string.Equals(IndependentScorer.IntentId(intent), context.CaptureIntentId, StringComparison.Ordinal)) ||
            !OpaqueId(context.SessionId) || !OpaqueId(context.CorrelationId) ||
            context.CaptureOrdinal < 0 || context.Width is < 1 or > 32768 || context.Height is < 1 or > 32768 ||
            context.UiScale is < 0.25m or > 8m || !DeviceClasses.Contains(context.DeviceClass) || !Surfaces.Contains(context.Surface) ||
            !BoundedToken(context.Locale) || !BoundedToken(context.GameVersion) || !BoundedToken(context.CompanionUiVersion) ||
            context.ObjectiveIds is null || context.ObjectiveIds.Count > 256 ||
            context.ObjectiveIds.Count != context.ObjectiveIds.Distinct(StringComparer.Ordinal).Count() ||
            context.ObjectiveIds.Any(id => !OpaqueId(id)) || OptionalIdInvalid(context.WorkspaceId) || OptionalIdInvalid(context.ProfileId) ||
            OptionalIdInvalid(context.MapId) || OptionalIdInvalid(context.FloorId) || OptionalIdInvalid(context.PlanId) ||
            OptionalIdInvalid(context.SelectedReference) || OptionalIdInvalid(context.PriorScanReference))
        {
            errors.Add($"Sample {sampleId} has an invalid immutable context snapshot.");
        }
    }

    internal static void ValidateLineage(string sampleId, SequenceLineage? lineage, ICollection<string> errors)
    {
        if (lineage is null || lineage.FrameOrdinal < 0 || lineage.OverlapWithPrevious is < 0 or > 1 ||
            !OpaqueId(lineage.SequenceId) || !OpaqueId(lineage.ViewportId) || !OpaqueId(lineage.ContainerIdentity) ||
            OptionalIdInvalid(lineage.ParentContainerIdentity) ||
            string.Equals(lineage.ParentContainerIdentity, lineage.ContainerIdentity, StringComparison.Ordinal))
        {
            errors.Add($"Sample {sampleId} has invalid sequence lineage.");
        }
    }

    internal static bool ContextEquals(CaptureContext? left, CaptureContext? right) =>
        left is not null && right is not null &&
        string.Equals(left.CaptureIntentId, right.CaptureIntentId, StringComparison.Ordinal) &&
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
        string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) &&
        left.CaptureOrdinal == right.CaptureOrdinal &&
        string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.MapId, right.MapId, StringComparison.Ordinal) &&
        string.Equals(left.FloorId, right.FloorId, StringComparison.Ordinal) &&
        string.Equals(left.PlanId, right.PlanId, StringComparison.Ordinal) &&
        left.ObjectiveIds.SequenceEqual(right.ObjectiveIds, StringComparer.Ordinal) &&
        string.Equals(left.SelectedReference, right.SelectedReference, StringComparison.Ordinal) &&
        string.Equals(left.PriorScanReference, right.PriorScanReference, StringComparison.Ordinal) &&
        string.Equals(left.DeviceClass, right.DeviceClass, StringComparison.Ordinal) &&
        string.Equals(left.Surface, right.Surface, StringComparison.Ordinal) &&
        left.Width == right.Width && left.Height == right.Height && left.UiScale == right.UiScale &&
        string.Equals(left.Locale, right.Locale, StringComparison.Ordinal) &&
        string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal) &&
        string.Equals(left.CompanionUiVersion, right.CompanionUiVersion, StringComparison.Ordinal);

    internal static bool Sha256(string? value) => value is { Length: 64 } && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');

    internal static bool OpaqueId(string? value) => value is { Length: >= 16 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static void ValidateImportedContent(CorpusSample sample, PrivateSampleEvidence evidence, DateTimeOffset nowUtc, ICollection<string> errors)
    {
        if (!string.Equals(sample.SampleId, evidence.SampleId, StringComparison.Ordinal) ||
            !Sha256(evidence.ObservedDecodedPixelSha256) ||
            !string.Equals(sample.DecodedPixelSha256, evidence.ObservedDecodedPixelSha256, StringComparison.Ordinal))
        {
            errors.Add($"Sample {sample.SampleId} decoded pixels changed from the canonical imported-content SHA-256.");
        }

        if (sample.EvidenceClass != CorpusEvidenceClass.RealRaster)
        {
            if (sample.ConsentHash is not null || sample.PrivacyReviewHash is not null || evidence.Consent is not null || evidence.PrivacyReview is not null)
            {
                errors.Add($"Non-real sample {sample.SampleId} must not carry private consent or privacy evidence.");
            }

            return;
        }

        if (!Sha256(sample.ConsentHash) || !Sha256(sample.PrivacyReviewHash) || evidence.Consent is null || evidence.PrivacyReview is null ||
            !string.Equals(sample.ConsentHash, evidence.Consent?.ConsentHash, StringComparison.Ordinal) ||
            !string.Equals(sample.PrivacyReviewHash, evidence.PrivacyReview?.ReviewHash, StringComparison.Ordinal))
        {
            errors.Add($"Real sample {sample.SampleId} lacks matching full private consent and privacy evidence; hash-shaped summaries alone are ineligible.");
        }

        var consent = evidence.Consent;
        if (consent is null || !OpaqueId(consent.ConsentId) || !Sha256(consent.ConsentHash) || consent.AllowedUses is null ||
            consent.AllowedUses.Count == 0 || consent.AllowedUses.Count != consent.AllowedUses.Distinct(StringComparer.Ordinal).Count() ||
            consent.AllowedUses.Any(use => !AllowedUses.Contains(use)) || !consent.AllowedUses.Contains("benchmark", StringComparer.Ordinal) ||
            !IsUtc(consent.ConsentedUtc) || consent.ConsentedUtc > nowUtc || !IsUtc(consent.RetentionExpiresUtc) ||
            consent.RetentionExpiresUtc <= nowUtc || consent.RetentionExpiresUtc <= consent.ConsentedUtc ||
            !string.Equals(consent.RevocationState, "active", StringComparison.Ordinal))
        {
            errors.Add($"Real sample {sample.SampleId} lacks active allowed-use consent, valid UTC retention, or non-revoked authority.");
        }

        var review = evidence.PrivacyReview;
        if (review is null || !OpaqueId(review.ReviewId) || !Sha256(review.ReviewHash) || !IsUtc(review.ReviewedUtc) ||
            review.ReviewedUtc > nowUtc || (consent is not null && review.ReviewedUtc < consent.ConsentedUtc) ||
            !string.Equals(review.State, "approved", StringComparison.Ordinal) || review.RedactionState is not ("not-required" or "applied"))
        {
            errors.Add($"Real sample {sample.SampleId} lacks an approved matching UTC privacy/redaction review.");
        }
    }

    private static void ValidateRegion(string sampleId, PixelRegion region, CaptureContext context, string source, ICollection<string> errors)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > context.Width || (long)region.Y + region.Height > context.Height)
        {
            errors.Add($"Sample {sampleId} has an out-of-bounds {source} region for its capture dimensions.");
        }
    }

    private static bool KnownTruthHasValue(TruthClaim truth) =>
        truth.Kind == "region"
            ? truth.Region is not null && truth.Value is null
            : !string.IsNullOrWhiteSpace(truth.Value) && truth.Value.Length <= 4096;

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool OptionalIdInvalid(string? value) => value is not null && !OpaqueId(value);

    internal static bool BoundedToken(string? value) => value is { Length: >= 1 and <= 128 } && !string.IsNullOrWhiteSpace(value);

    internal static IReadOnlyList<string> PrivacyErrors(JsonElement root, bool privateManifest)
    {
        var errors = new List<string>();
        Visit(root, errors, privateManifest ? ForbiddenPrivateManifestNames : ForbiddenInterchangeNames);
        return errors;
    }

    internal static IReadOnlyList<string> AggregatePrivacyErrors(JsonElement root)
    {
        var errors = new List<string>();
        Visit(root, errors, ForbiddenAggregateNames);
        return errors;
    }

    private static void Visit(JsonElement element, ICollection<string> errors, IReadOnlySet<string> forbiddenNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (forbiddenNames.Contains(property.Name))
                {
                    errors.Add($"Interchange cannot contain prohibited field {property.Name}.");
                }

                Visit(property.Value, errors, forbiddenNames);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                Visit(value, errors, forbiddenNames);
            }
        }
        else if (element.ValueKind == JsonValueKind.String && LooksFilesystemPath(element.GetString() ?? string.Empty))
        {
            errors.Add("Interchange cannot contain an absolute filesystem path or relative traversal.");
        }
    }

    private static bool LooksFilesystemPath(string value) =>
        Path.IsPathFullyQualified(value) ||
        value.StartsWith("/", StringComparison.Ordinal) ||
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/') ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal);
}

internal static class PredictionTypeNames
{
    public static bool TryParse(string? value, out PredictionType type)
    {
        type = value switch
        {
            "context" => PredictionType.Context,
            "region" => PredictionType.Region,
            "grid-cell" => PredictionType.GridCell,
            "item" => PredictionType.Item,
            "attribute" => PredictionType.Attribute,
            "extract" => PredictionType.Extract,
            "timer" => PredictionType.Timer,
            "health" => PredictionType.Health,
            "recommendation" => PredictionType.Recommendation,
            _ => (PredictionType)(-1),
        };
        return Enum.IsDefined(type);
    }

    public static string Format(PredictionType type) => type switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
