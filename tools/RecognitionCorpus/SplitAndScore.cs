using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.RecognitionCorpus;

public static class SplitPlanner
{
    /// <summary>
    /// A connected component is a split unit: exact decoded pixels, perceptual-near links, and
    /// capture sequences cannot cross a split even when a crop, redaction, or scroll has a new
    /// byte-level hash. The component key commits to every sorted content root in the component.
    /// </summary>
    public static IReadOnlyDictionary<string, CorpusSplit> Assign(IReadOnlyList<CorpusSample> samples)
    {
        var units = BuildUnits(samples);
        return units.ToDictionary(unit => unit.Key, unit => StableAssignment(unit.Key), StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> ValidateUnits(IReadOnlyList<CorpusSample> samples)
    {
        var errors = new List<string>();
        foreach (var unit in BuildUnits(samples))
        {
            foreach (var sample in unit.Value)
            {
                if (!string.Equals(sample.DeclaredSplitUnitId, unit.Key, StringComparison.Ordinal))
                {
                    errors.Add($"Sample {sample.SampleId} does not declare its independently recomputed split unit.");
                }
            }
        }

        return errors;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<CorpusSample>> BuildUnits(IReadOnlyList<CorpusSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var indexed = samples.Select((sample, index) => (sample, index)).ToArray();
        var union = new UnionFind(indexed.Length);
        var byHash = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySequence = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < indexed.Length; index++)
        {
            var sample = indexed[index].sample;
            foreach (var hash in new[] { sample.DecodedPixelSha256 }.Concat(sample.NearDuplicateHashes ?? []))
            {
                if (hash is null)
                {
                    continue;
                }

                if (byHash.TryGetValue(hash, out var existing))
                {
                    union.Union(index, existing);
                }
                else
                {
                    byHash.Add(hash, index);
                }
            }

            if (bySequence.TryGetValue(sample.Lineage.SequenceId, out var sequenceMember))
            {
                union.Union(index, sequenceMember);
            }
            else
            {
                bySequence.Add(sample.Lineage.SequenceId, index);
            }
        }

        var components = new Dictionary<int, List<CorpusSample>>();
        for (var index = 0; index < indexed.Length; index++)
        {
            var root = union.Find(index);
            if (!components.TryGetValue(root, out var component))
            {
                component = [];
                components.Add(root, component);
            }

            component.Add(indexed[index].sample);
        }

        return components.Values.ToDictionary(
            ComponentId,
            component => (IReadOnlyList<CorpusSample>)component.AsReadOnly(),
            StringComparer.Ordinal);
    }

    public static CorpusSplit StableAssignment(string splitUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(splitUnitId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(splitUnitId));
        var bucket = BinaryPrimitives.ReadUInt32BigEndian(digest) % 100;
        return bucket < 80 ? CorpusSplit.Train : bucket < 90 ? CorpusSplit.Tune : CorpusSplit.Test;
    }

    private static string ComponentId(IEnumerable<CorpusSample> component)
    {
        var roots = component
            .Select(sample => sample.DecodedPixelSha256 ?? $"evidence:{sample.SampleId}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);
        var basis = string.Join('\n', roots);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"recognition-corpus-split-unit.v1\n{basis}")));
    }

    private sealed class UnionFind(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();

        public int Find(int value)
        {
            while (_parents[value] != value)
            {
                _parents[value] = _parents[_parents[value]];
                value = _parents[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right)
            {
                _parents[right] = left;
            }
        }
    }
}

internal sealed record PrivatePlannedSample(
    string SampleId,
    CorpusSplit Split,
    BenchmarkIntent Intent,
    CorpusEvidenceClass EvidenceClass,
    CaptureContext Context,
    SequenceLineage Lineage,
    string SplitUnitId,
    bool ConsentPermitsSplit);

public static class PrivateRunPlanner
{
    public static RunPlan Create(
        CorpusManifest privateManifest,
        string runId,
        string producerId,
        string producerVersion,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(privateManifest);
        if (!CorpusValidation.OpaqueId(runId) || !CorpusValidation.OpaqueId(producerId) || !CorpusValidation.BoundedToken(producerVersion))
        {
            throw new ArgumentException("A run plan requires opaque run/producer ids and a bounded producer version.");
        }

        var errors = CorpusValidation.ValidateManifest(privateManifest, nowUtc);
        if (errors.Count != 0)
        {
            throw new ArgumentException($"Private manifest is ineligible: {string.Join("; ", errors)}", nameof(privateManifest));
        }

        var expected = ExpectedSamples(privateManifest);
        if (expected.Count == 0)
        {
            throw new ArgumentException("No sample in the private manifest has consent for the split it is assigned to.", nameof(privateManifest));
        }

        var planSamples = expected.Select(sample => new RunPlanSample(
            sample.SampleId,
            sample.Split,
            sample.Intent,
            sample.EvidenceClass,
            sample.Context,
            sample.Lineage)).ToArray();
        var planLock = ComputeLock(runId, producerId, producerVersion, privateManifest, expected);
        return new RunPlan(
            runId,
            producerId,
            producerVersion,
            CorpusValidation.FrozenPolicyVersion,
            privateManifest.CorpusId,
            privateManifest.NearDuplicateGraphVersion,
            planLock,
            planSamples);
    }

    /// <summary>
    /// The samples a producer may be handed. A real capture consented only for benchmarking
    /// keeps its content-stable split, because moving it would break the split unit it shares
    /// with its crops and scroll frames, but it is withheld from the plan when that split is
    /// Train or Tune: handing it to a producer there would be training on pixels the contributor
    /// never allowed training on.
    /// </summary>
    internal static IReadOnlyList<PrivatePlannedSample> ExpectedSamples(CorpusManifest privateManifest) =>
        PlannedSamples(privateManifest).Where(sample => sample.ConsentPermitsSplit).ToArray();

    internal static IReadOnlyList<PrivatePlannedSample> PlannedSamples(CorpusManifest privateManifest)
    {
        // Units are built from every sample, withheld ones included: a withheld original still
        // joins its crop and its redaction into one unit.
        var units = SplitPlanner.BuildUnits(privateManifest.Samples);
        var unitBySample = units.SelectMany(unit => unit.Value.Select(sample => (sample.SampleId, Unit: unit.Key)))
            .ToDictionary(pair => pair.SampleId, pair => pair.Unit, StringComparer.Ordinal);
        var evidenceBySample = privateManifest.PrivateEvidence.ToDictionary(item => item.SampleId, StringComparer.Ordinal);
        return privateManifest.Samples
            .OrderBy(sample => sample.SampleId, StringComparer.Ordinal)
            .Select(sample =>
            {
                var split = SplitPlanner.StableAssignment(unitBySample[sample.SampleId]);
                return new PrivatePlannedSample(
                    sample.SampleId,
                    split,
                    IndependentScorer.IntentFor(sample.Context.CaptureIntentId),
                    sample.EvidenceClass,
                    sample.Context,
                    sample.Lineage,
                    unitBySample[sample.SampleId],
                    ConsentPermits(sample, evidenceBySample.GetValueOrDefault(sample.SampleId), split));
            })
            .ToArray();
    }

    internal static bool ConsentPermits(CorpusSample sample, PrivateSampleEvidence? evidence, CorpusSplit split)
    {
        if (sample.EvidenceClass != CorpusEvidenceClass.RealRaster)
        {
            return true;
        }

        var use = split switch
        {
            CorpusSplit.Train => "train",
            CorpusSplit.Tune => "tune",
            CorpusSplit.Test => "benchmark",
            _ => null,
        };
        return use is not null && evidence?.Consent?.AllowedUses is { } allowedUses && allowedUses.Contains(use, StringComparer.Ordinal);
    }

    internal static string ComputeLock(
        string runId,
        string producerId,
        string producerVersion,
        CorpusManifest privateManifest,
        IReadOnlyList<PrivatePlannedSample> samples)
    {
        var builder = new StringBuilder();
        Append(builder, "private-run-plan-lock.v1");
        Append(builder, runId);
        Append(builder, producerId);
        Append(builder, producerVersion);
        Append(builder, CorpusValidation.FrozenPolicyVersion);
        Append(builder, privateManifest.CorpusId);
        Append(builder, privateManifest.NearDuplicateGraphVersion);
        var evidenceBySample = privateManifest.PrivateEvidence.ToDictionary(item => item.SampleId, StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var source = privateManifest.Samples.Single(item => item.SampleId == sample.SampleId);
            Append(builder, sample.SampleId);
            Append(builder, sample.Split.ToString());
            Append(builder, sample.Intent.ToString());
            Append(builder, sample.EvidenceClass.ToString());
            Append(builder, sample.SplitUnitId);
            Append(builder, source.DecodedPixelSha256);
            Append(builder, "near-duplicate-hashes");
            Append(builder, source.NearDuplicateHashes.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var nearHash in source.NearDuplicateHashes.OrderBy(value => value, StringComparer.Ordinal))
            {
                Append(builder, nearHash);
            }

            // The lock is the scorer's commitment to the private answer key, not merely to
            // public plan membership. Canonical ordering makes harmless manifest reordering
            // stable while any truth-state, kind, value, or region change invalidates the run.
            Append(builder, "canonical-ground-truth");
            Append(builder, source.Truth.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var truth in source.Truth.OrderBy(value => value.TruthId, StringComparer.Ordinal))
            {
                Append(builder, truth.TruthId);
                Append(builder, truth.State.ToString());
                Append(builder, truth.Kind);
                Append(builder, truth.Value);
                Append(builder, truth.Region?.X.ToString(CultureInfo.InvariantCulture));
                Append(builder, truth.Region?.Y.ToString(CultureInfo.InvariantCulture));
                Append(builder, truth.Region?.Width.ToString(CultureInfo.InvariantCulture));
                Append(builder, truth.Region?.Height.ToString(CultureInfo.InvariantCulture));
            }

            AppendContext(builder, sample.Context);
            AppendLineage(builder, sample.Lineage);
            if (evidenceBySample.TryGetValue(sample.SampleId, out var evidence))
            {
                Append(builder, evidence.ObservedDecodedPixelSha256);
                if (evidence.Consent is { } consent)
                {
                    Append(builder, consent.ConsentId);
                    Append(builder, consent.ConsentHash);
                    foreach (var allowedUse in consent.AllowedUses.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        Append(builder, allowedUse);
                    }

                    Append(builder, consent.ConsentedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    Append(builder, consent.RetentionExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    Append(builder, consent.RevocationState);
                }

                if (evidence.PrivacyReview is { } review)
                {
                    Append(builder, review.ReviewId);
                    Append(builder, review.ReviewHash);
                    Append(builder, review.State);
                    Append(builder, review.RedactionState);
                    Append(builder, review.ReviewedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendContext(StringBuilder builder, CaptureContext context)
    {
        Append(builder, context.CaptureIntentId);
        Append(builder, context.SessionId);
        Append(builder, context.CorrelationId);
        Append(builder, context.CaptureOrdinal.ToString(CultureInfo.InvariantCulture));
        Append(builder, context.WorkspaceId);
        Append(builder, context.ProfileId);
        Append(builder, context.MapId);
        Append(builder, context.FloorId);
        Append(builder, context.PlanId);
        Append(builder, context.ObjectiveIds.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var objectiveId in context.ObjectiveIds)
        {
            Append(builder, objectiveId);
        }

        Append(builder, context.SelectedReference);
        Append(builder, context.PriorScanReference);
        Append(builder, context.DeviceClass);
        Append(builder, context.Surface);
        Append(builder, context.Width.ToString(CultureInfo.InvariantCulture));
        Append(builder, context.Height.ToString(CultureInfo.InvariantCulture));
        // "1", "1.0", and "1.00" are one JSON value; the lock must not depend on how a writer
        // happened to spell it.
        Append(builder, CorpusJson.CanonicalDecimalText(context.UiScale));
        Append(builder, context.Locale);
        Append(builder, context.GameVersion);
        Append(builder, context.CompanionUiVersion);
    }

    private static void AppendLineage(StringBuilder builder, SequenceLineage lineage)
    {
        Append(builder, lineage.SequenceId);
        Append(builder, lineage.FrameOrdinal.ToString(CultureInfo.InvariantCulture));
        Append(builder, lineage.ViewportId);
        Append(builder, lineage.ContainerIdentity);
        Append(builder, CorpusJson.CanonicalDecimalText(lineage.OverlapWithPrevious));
        Append(builder, lineage.ParentContainerIdentity);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= "<null>";
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
    }
}

public static class IndependentScorer
{
    public static AggregateResults Score(
        CorpusManifest privateManifest,
        RunPlan authorizedPlan,
        PredictionDocument predictionDocument,
        FrozenThresholds thresholds,
        CorpusEvidenceClass evidenceClass,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(privateManifest);
        ArgumentNullException.ThrowIfNull(authorizedPlan);
        ArgumentNullException.ThrowIfNull(predictionDocument);
        ArgumentNullException.ThrowIfNull(thresholds);

        var thresholdErrors = CorpusValidation.ValidateThresholds(thresholds);
        if (thresholdErrors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", thresholdErrors), nameof(thresholds));
        }

        if (!Enum.IsDefined(evidenceClass) || nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Scoring requires a defined evidence class and an explicit UTC score time.");
        }

        var manifestErrors = CorpusValidation.ValidateManifest(privateManifest, nowUtc);
        if (manifestErrors.Count > 0)
        {
            throw new ArgumentException(
                $"Private scoring manifest is no longer eligible: {string.Join("; ", manifestErrors)}",
                nameof(privateManifest));
        }

        var planErrors = CorpusValidation.ValidateRunPlan(authorizedPlan, privateManifest, nowUtc);
        if (planErrors.Count > 0)
        {
            throw new ArgumentException(
                $"Scoring requires the authorized, currently eligible frozen run plan: {string.Join("; ", planErrors)}",
                nameof(authorizedPlan));
        }

        var predictionErrors = CorpusValidation.ValidatePredictions(predictionDocument, authorizedPlan);
        if (predictionErrors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", predictionErrors), nameof(predictionDocument));
        }

        var plannedById = authorizedPlan.Samples.ToDictionary(sample => sample.SampleId, StringComparer.Ordinal);
        if (predictionDocument.Predictions.Any(prediction =>
                !plannedById.TryGetValue(prediction.SampleId, out var planned) ||
                planned.Split != CorpusSplit.Test || planned.EvidenceClass != evidenceClass))
        {
            throw new ArgumentException(
                "Independent scoring accepts predictions only for the authorized frozen Test split and one evidence class.",
                nameof(predictionDocument));
        }

        var testPlanSamples = authorizedPlan.Samples
            .Where(sample => sample.Split == CorpusSplit.Test && sample.EvidenceClass == evidenceClass)
            .ToArray();
        if (testPlanSamples.Length == 0)
        {
            throw new ArgumentException(
                "Independent scoring requires at least one authorized sample in the frozen Test split for the selected evidence class.",
                nameof(authorizedPlan));
        }

        var privateById = privateManifest.Samples.ToDictionary(sample => sample.SampleId, StringComparer.Ordinal);
        var samples = testPlanSamples.Select(sample => privateById[sample.SampleId]).ToArray();
        var units = SplitPlanner.BuildUnits(privateManifest.Samples);
        var unitBySample = units.SelectMany(unit => unit.Value.Select(sample => (sample.SampleId, unit.Key)))
            .ToDictionary(pair => pair.SampleId, pair => pair.Key, StringComparer.Ordinal);
        // Every intent remains explicit, including absent ones, but one result document can
        // represent only one evidence class. This prevents real, synthetic, and post-OCR
        // evidence from being pooled into a publishable-looking score.
        var slices = Enum.GetValues<BenchmarkIntent>()
            .Select(intent => ScoreSlice(
                intent,
                evidenceClass,
                samples.Where(sample => IntentFor(sample.Context.CaptureIntentId) == intent).ToArray(),
                predictionDocument.Predictions,
                unitBySample,
                thresholds))
            .ToArray();
        var nonEmptySlices = slices.Where(ContainsAggregateEvidence).ToArray();
        var safeToPublish = nonEmptySlices.Length > 0 &&
                            nonEmptySlices.All(slice => slice.IndependentSplitUnits >= thresholds.MinimumIndependentSplitUnits);
        return new AggregateResults(
            authorizedPlan.RunId,
            authorizedPlan.ProducerId,
            authorizedPlan.ProducerVersion,
            authorizedPlan.CorpusId,
            authorizedPlan.PlanLock,
            thresholds.PolicyVersion,
            evidenceClass,
            nowUtc,
            new AggregatePrivacy(safeToPublish, thresholds.MinimumIndependentSplitUnits, false),
            slices);
    }

    public static BenchmarkIntent IntentFor(string captureIntentId)
    {
        foreach (var value in Enum.GetValues<BenchmarkIntent>())
        {
            if (string.Equals(captureIntentId, IntentId(value), StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new ArgumentException("Capture intent id is not a frozen v1 benchmark intent.", nameof(captureIntentId));
    }

    public static string IntentId(BenchmarkIntent intent) => intent switch
    {
        BenchmarkIntent.LootDecision => "intent-v1-loot-decision",
        BenchmarkIntent.FullStash => "intent-v1-full-stash",
        BenchmarkIntent.Ammo => "intent-v1-ammo-items",
        BenchmarkIntent.Keys => "intent-v1-key-items",
        BenchmarkIntent.QuestItems => "intent-v1-quest-items",
        BenchmarkIntent.MapExtractsTimers => "intent-v1-map-extracts-timers",
        BenchmarkIntent.HealthCharacter => "intent-v1-health-character",
        BenchmarkIntent.AutoDetect => "intent-v1-auto-detect",
        _ => throw new ArgumentOutOfRangeException(nameof(intent)),
    };

    private static SliceMetrics ScoreSlice(
        BenchmarkIntent intent,
        CorpusEvidenceClass evidenceClass,
        IReadOnlyList<CorpusSample> samples,
        IReadOnlyList<ProducerPrediction> allPredictions,
        IReadOnlyDictionary<string, string> unitBySample,
        FrozenThresholds thresholds)
    {
        var sampleIds = samples.Select(sample => sample.SampleId).ToHashSet(StringComparer.Ordinal);
        var predictions = allPredictions.Where(prediction => sampleIds.Contains(prediction.SampleId)).ToArray();
        var allTruth = samples.SelectMany(sample => sample.Truth.Select(truth => (sample, truth))).ToArray();
        var knownGroups = allTruth.Where(pair => pair.truth.State == TruthState.Known)
            .GroupBy(pair => pair.truth.TruthId, StringComparer.Ordinal)
            .ToArray();
        var excluded = allTruth.Where(pair => pair.truth.State == TruthState.Unknown)
            .Select(pair => pair.truth.TruthId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var unknownScopes = allTruth.Where(pair => pair.truth.State == TruthState.Unknown)
            .Select(pair => (pair.sample.SampleId, pair.truth.Kind))
            .ToHashSet();
        var consumedClaims = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = new List<IGrouping<string, (CorpusSample sample, TruthClaim truth)>>();
        var truePositives = 0;
        var falseNegatives = 0;
        var attempted = 0;
        var abstentions = 0;
        var confidentWrong = 0;

        foreach (var truthGroup in knownGroups)
        {
            var truth = truthGroup.First().truth;
            var relevantPredictions = RelevantPredictions(truthGroup, predictions);
            if (relevantPredictions.Any(prediction => prediction.Status != PredictionStatus.Unavailable))
            {
                attempted++;
            }

            if (relevantPredictions.Any(prediction => prediction.Status == PredictionStatus.Abstained) &&
                relevantPredictions.All(prediction => prediction.Status != PredictionStatus.Detected))
            {
                abstentions++;
            }

            var match = UnconsumedClaims(relevantPredictions, truth, consumedClaims)
                .FirstOrDefault(pair => ClaimMatches(pair.claim, truth));
            if (match.claim is not null)
            {
                consumedClaims.Add(match.claim.ClaimId);
                truePositives++;
            }
            else
            {
                falseNegatives++;
                unmatched.Add(truthGroup);
            }
        }

        // Confident-wrong is decided only after every truth has had its chance to match. Deciding
        // it inside the loop let a claim that correctly matched a later truth count as the wrong
        // answer for an earlier one. A claim in an unknown-truth scope of the same kind is also not
        // evidence of a wrong answer: it may be a claim about the unlabelled object, exactly why it
        // is excluded from the false positives below. A correct claim plus an extra wrong claim is
        // therefore one TP and one FP, never a confident miss.
        foreach (var truthGroup in unmatched)
        {
            var truth = truthGroup.First().truth;
            if (UnconsumedClaims(RelevantPredictions(truthGroup, predictions), truth, consumedClaims).Any(pair =>
                    !unknownScopes.Contains((pair.prediction.SampleId, pair.claim.Kind)) &&
                    pair.prediction.Confidence >= CorpusValidation.FrozenConfidentWrongMinimumConfidence))
            {
                confidentWrong++;
            }
        }

        var unconsumedClaims = predictions
            .SelectMany(prediction => prediction.Claims.Select(claim => (prediction, claim)))
            .Where(pair => !consumedClaims.Contains(pair.claim.ClaimId))
            .ToArray();
        // Unknown truth marks an unadjudicable sample/type scope. Producer claims there reveal
        // neither correctness nor error and therefore cannot inflate the FP numerator.
        var excludedPredictionClaims = unconsumedClaims.Count(pair =>
            unknownScopes.Contains((pair.prediction.SampleId, pair.claim.Kind)));
        var falsePositives = unconsumedClaims.Length - excludedPredictionClaims;
        var sequence = SequenceErrors(samples, predictions);
        var denominator = knownGroups.Length;
        var independentUnits = samples.Select(sample => unitBySample[sample.SampleId]).Distinct(StringComparer.Ordinal).Count();
        var coverage = Rate(attempted, denominator);
        var accuracyDenominator = truePositives + falsePositives + falseNegatives;
        var accuracy = Rate(truePositives, accuracyDenominator);
        var recall = Rate(truePositives, denominator);
        var falsePositiveDenominator = truePositives + falsePositives;
        var falsePositiveRate = Rate(falsePositives, falsePositiveDenominator);
        var f1Denominator = (2 * truePositives) + falsePositives + falseNegatives;
        var f1 = f1Denominator == 0 ? 0 : (decimal)(2 * truePositives) / f1Denominator;
        var abstentionRate = Rate(abstentions, denominator);
        var confidentWrongRate = Rate(confidentWrong, denominator);
        var underpowered = independentUnits < thresholds.MinimumIndependentSplitUnits || denominator < thresholds.MinimumKnownClaims;
        var passes = coverage >= thresholds.MinimumCoverage && abstentionRate <= thresholds.MaximumAbstentionRate &&
                     confidentWrongRate <= thresholds.MaximumConfidentWrongRate && f1 >= thresholds.MinimumF1;
        var status = underpowered ? "insufficient-data" : passes ? "pass" : "fail";
        var interval = Wilson(truePositives, denominator);
        var elapsed = predictions.Select(prediction => prediction.ElapsedMilliseconds).ToArray();
        return new SliceMetrics(
            intent,
            evidenceClass,
            status,
            truePositives,
            denominator,
            excluded,
            excludedPredictionClaims,
            independentUnits,
            attempted,
            truePositives,
            falsePositives,
            falseNegatives,
            abstentions,
            confidentWrong,
            sequence.Missing,
            sequence.Reordered,
            sequence.OverlapErrors,
            truePositives,
            accuracyDenominator,
            truePositives,
            denominator,
            falsePositives,
            falsePositiveDenominator,
            coverage,
            accuracy,
            recall,
            falsePositiveRate,
            f1,
            abstentionRate,
            confidentWrongRate,
            interval.Lower,
            interval.Upper,
            elapsed.Length,
            elapsed.Length == 0 ? null : elapsed.Average(),
            elapsed.Length == 0 ? null : elapsed.Max());
    }

    private static ProducerPrediction[] RelevantPredictions(
        IEnumerable<(CorpusSample sample, TruthClaim truth)> truthGroup,
        IReadOnlyList<ProducerPrediction> predictions)
    {
        var visible = truthGroup.Select(pair => pair.sample.SampleId).ToHashSet(StringComparer.Ordinal);
        var kind = truthGroup.First().truth.Kind;
        return predictions.Where(prediction =>
            visible.Contains(prediction.SampleId) &&
            string.Equals(PredictionTypeNames.Format(prediction.Type), kind, StringComparison.Ordinal)).ToArray();
    }

    private static IEnumerable<(ProducerPrediction prediction, PredictionClaim claim)> UnconsumedClaims(
        IEnumerable<ProducerPrediction> predictions,
        TruthClaim truth,
        IReadOnlySet<string> consumedClaims) =>
        predictions
            .SelectMany(prediction => prediction.Claims.Select(claim => (prediction, claim)))
            .Where(pair => string.Equals(pair.claim.Kind, truth.Kind, StringComparison.Ordinal) &&
                           !consumedClaims.Contains(pair.claim.ClaimId));

    private static bool ClaimMatches(PredictionClaim claim, TruthClaim truth) =>
        string.Equals(claim.Kind, truth.Kind, StringComparison.Ordinal) &&
        string.Equals(claim.Value, truth.Value, StringComparison.Ordinal) &&
        claim.Region == truth.Region;

    private static (int Missing, int Reordered, int OverlapErrors) SequenceErrors(
        IReadOnlyList<CorpusSample> samples,
        IReadOnlyList<ProducerPrediction> predictions)
    {
        var missing = 0;
        var reordered = 0;
        var overlapErrors = 0;
        foreach (var sequence in samples.GroupBy(sample => sample.Lineage.SequenceId, StringComparer.Ordinal))
        {
            var expected = sequence.OrderBy(sample => sample.Lineage.FrameOrdinal).ToArray();
            var expectedIds = expected.Select(sample => sample.SampleId).ToHashSet(StringComparer.Ordinal);
            var observed = predictions.Where(prediction => expectedIds.Contains(prediction.SampleId))
                .Select(prediction => prediction.SampleId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            missing += expected.Count(sample => !observed.Contains(sample.SampleId, StringComparer.Ordinal));
            var expectedOrder = expected.Select(sample => sample.SampleId).Where(observed.Contains).ToArray();
            if (!expectedOrder.SequenceEqual(observed, StringComparer.Ordinal))
            {
                reordered++;
            }

            foreach (var truthGroup in expected.SelectMany(sample => sample.Truth.Select(truth => (sample, truth)))
                         .Where(pair => pair.truth.State == TruthState.Known)
                         .GroupBy(pair => pair.truth.TruthId, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1 && group.Skip(1).All(pair => pair.sample.Lineage.OverlapWithPrevious > 0)))
            {
                var truth = truthGroup.First().truth;
                var visible = truthGroup.Select(pair => pair.sample.SampleId).ToHashSet(StringComparer.Ordinal);
                var duplicateMatches = predictions.Where(prediction => visible.Contains(prediction.SampleId))
                    .SelectMany(prediction => prediction.Claims)
                    .Count(claim => ClaimMatches(claim, truth));
                overlapErrors += Math.Max(0, duplicateMatches - 1);
            }
        }

        return (missing, reordered, overlapErrors);
    }

    internal static decimal Rate(int numerator, int denominator) => denominator == 0 ? 0 : (decimal)numerator / denominator;

    internal static (decimal Lower, decimal Upper) Wilson(int successes, int trials)
    {
        if (trials == 0)
        {
            return (0, 0);
        }

        const decimal z = 1.959963984540054m;
        var n = (decimal)trials;
        var p = successes / n;
        var z2 = z * z;
        var center = (p + z2 / (2 * n)) / (1 + z2 / n);
        var margin = z * (decimal)Math.Sqrt((double)((p * (1 - p) + z2 / (4 * n)) / n)) / (1 + z2 / n);
        return (Math.Max(0, center - margin), Math.Min(1, center + margin));
    }

    internal static bool ContainsAggregateEvidence(SliceMetrics slice) =>
        slice.IndependentSplitUnits != 0 || slice.Denominator != 0 || slice.ExcludedUnknowns != 0 ||
        slice.ExcludedPredictionClaims != 0 || slice.PerformanceSampleCount != 0 || slice.MissingFrames != 0 ||
        slice.ReorderedFrames != 0 || slice.OverlapDeduplicationErrors != 0;
}
