using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.RecognitionCorpus;

public static class SplitPlanner
{
    /// <summary>
    /// A connected component is a split unit: exact decoded pixels, perceptual-near links, and
    /// capture sequences cannot cross a split even when a crop, redaction, or scroll has a new
    /// byte-level hash. The component key is a sorted content-derived hash, never a filename.
    /// </summary>
    public static IReadOnlyDictionary<string, CorpusSplit> Assign(IReadOnlyList<CorpusSample> samples)
    {
        var units = BuildUnits(samples);
        return units.ToDictionary(
            unit => unit.Key,
            unit => StableAssignment(unit.Key),
            StringComparer.Ordinal);
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
            component => ComponentId(component),
            component => (IReadOnlyList<CorpusSample>)component.AsReadOnly(),
            StringComparer.Ordinal);
    }

    public static CorpusSplit StableAssignment(string splitUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(splitUnitId);
        var bucket = SHA256.HashData(Encoding.UTF8.GetBytes(splitUnitId))[0] % 100;
        return bucket < 80 ? CorpusSplit.Train : bucket < 90 ? CorpusSplit.Tune : CorpusSplit.Test;
    }

    private static string ComponentId(IEnumerable<CorpusSample> component)
    {
        var basis = component
            .Select(sample => sample.DecodedPixelSha256 ?? $"evidence:{sample.SampleId}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .First();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"recognition-corpus-split-unit.v1:{basis}")));
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

public static class IndependentScorer
{
    public static IReadOnlyList<SliceMetrics> Score(
        IReadOnlyList<CorpusSample> samples,
        IReadOnlyList<ProducerPrediction> predictions,
        FrozenThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(thresholds);

        var manifestErrors = SplitPlanner.ValidateUnits(samples);
        if (manifestErrors.Count > 0)
        {
            throw new ArgumentException("Private manifest split units must validate before scoring.", nameof(samples));
        }

        var sampleById = samples.ToDictionary(sample => sample.SampleId, StringComparer.Ordinal);
        if (predictions.Any(prediction => !sampleById.TryGetValue(prediction.SampleId, out var sample) || sample.EvidenceClass != prediction.EvidenceClass))
        {
            throw new ArgumentException("Predictions must name a known sample with its exact evidence class.", nameof(predictions));
        }

        var units = SplitPlanner.BuildUnits(samples);
        var unitBySample = units.SelectMany(unit => unit.Value.Select(sample => (sample.SampleId, unit.Key)))
            .ToDictionary(pair => pair.SampleId, pair => pair.Key, StringComparer.Ordinal);
        // Absence is itself an outcome. Emit every intent/evidence-class cell so a report cannot
        // silently pool a missing health or scrolling-stash slice into a better-supported one.
        return Enum.GetValues<BenchmarkIntent>()
            .SelectMany(intent => Enum.GetValues<CorpusEvidenceClass>().Select(evidenceClass =>
                ScoreSlice(intent, evidenceClass,
                    samples.Where(sample => IntentFor(sample.Context.CaptureIntentId) == intent && sample.EvidenceClass == evidenceClass).ToArray(),
                    predictions, unitBySample, thresholds)))
            .ToArray();
    }

    /// <summary>
    /// The intent is carried as an opaque immutable capture-intent ID in evidence. The scorer
    /// receives its audited mapping, encoded with this stable v1 prefix, rather than inferring a
    /// label from OCR output or a displayed panel.
    /// </summary>
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

    public static string IntentId(BenchmarkIntent intent) => $"intent-v1-{intent.ToString().ToLowerInvariant()}";

    private static SliceMetrics ScoreSlice(
        BenchmarkIntent intent,
        CorpusEvidenceClass evidenceClass,
        IReadOnlyList<CorpusSample> samples,
        IReadOnlyList<ProducerPrediction> allPredictions,
        IReadOnlyDictionary<string, string> unitBySample,
        FrozenThresholds thresholds)
    {
        var sampleIds = samples.Select(sample => sample.SampleId).ToHashSet(StringComparer.Ordinal);
        var predictions = allPredictions.Where(prediction => prediction.Intent == intent && sampleIds.Contains(prediction.SampleId)).ToArray();
        // Truth ids survive a scroll or nested-container sequence. Grouping before metrics is
        // the no-double-count boundary: repeated visibility is evidence coverage, not a second
        // item to score.
        var allTruth = samples.SelectMany(sample => sample.Truth.Select(truth => (sample, truth))).ToArray();
        var known = allTruth.Where(pair => pair.truth.State == TruthState.Known)
            .GroupBy(pair => pair.truth.TruthId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(pair => pair.sample.Lineage.FrameOrdinal).First())
            .ToArray();
        var excluded = allTruth.Where(pair => pair.truth.State == TruthState.Unknown)
            .Select(pair => pair.truth.TruthId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var consumed = new HashSet<(string SampleId, string ClaimId)>();
        var truePositives = 0;
        var falseNegatives = 0;
        var abstentions = 0;
        var confidentWrong = 0;

        foreach (var (sample, truth) in known)
        {
            var candidates = predictions.Where(prediction => prediction.SampleId == sample.SampleId)
                .SelectMany(prediction => prediction.Claims.Select(claim => (prediction, claim)))
                .Where(pair => string.Equals(pair.claim.Kind, truth.Kind, StringComparison.Ordinal))
                .ToArray();
            var match = candidates.FirstOrDefault(pair => string.Equals(pair.claim.Value, truth.Value, StringComparison.Ordinal));
            if (match.claim is not null)
            {
                consumed.Add((sample.SampleId, match.claim.ClaimId));
                truePositives++;
                continue;
            }

            falseNegatives++;
            if (predictions.Any(prediction => prediction.SampleId == sample.SampleId && prediction.Status == PredictionStatus.Abstained))
            {
                abstentions++;
            }

            if (candidates.Any(pair => pair.prediction.Confidence >= 0.9m))
            {
                confidentWrong++;
            }
        }

        var falsePositives = predictions.SelectMany(prediction => prediction.Claims.Select(claim => (prediction.SampleId, claim.ClaimId)))
            .Count(claim => !consumed.Contains(claim));
        var sequence = SequenceErrors(samples, predictions);
        var denominator = known.Length;
        var units = samples.Select(sample => unitBySample[sample.SampleId]).Distinct(StringComparer.Ordinal).Count();
        var coverage = denominator == 0 ? 0m : (decimal)truePositives / denominator;
        var abstentionRate = denominator == 0 ? 0m : (decimal)abstentions / denominator;
        var confidentWrongRate = denominator == 0 ? 0m : (decimal)confidentWrong / denominator;
        var status = units < thresholds.MinimumIndependentSplitUnits || denominator < thresholds.MinimumKnownClaims
            ? "insufficient-data"
            : "reported";
        var interval = Wilson(truePositives, denominator);
        var elapsed = predictions.Where(prediction => prediction.ElapsedMilliseconds is not null).Select(prediction => prediction.ElapsedMilliseconds!.Value).ToArray();
        return new SliceMetrics(intent, evidenceClass, status, truePositives, denominator, excluded, units, truePositives, falsePositives,
            falseNegatives, abstentions, confidentWrong, sequence.Missing, sequence.Reordered, sequence.OverlapErrors, coverage,
            abstentionRate, confidentWrongRate, interval.Lower, interval.Upper, elapsed.Length == 0 ? null : elapsed.Average());
    }

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
            var observed = predictions.Select(prediction => prediction.SampleId).Where(id => expected.Any(sample => sample.SampleId == id)).Distinct(StringComparer.Ordinal).ToArray();
            missing += expected.Count(sample => !observed.Contains(sample.SampleId, StringComparer.Ordinal));
            var expectedOrder = expected.Select(sample => sample.SampleId).Where(observed.Contains).ToArray();
            if (!expectedOrder.SequenceEqual(observed))
            {
                reordered++;
            }

            if (expected.Skip(1).Any(sample => sample.Lineage.OverlapWithPrevious > 0) &&
                predictions.GroupBy(prediction => prediction.SampleId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                overlapErrors++;
            }
        }

        return (missing, reordered, overlapErrors);
    }

    private static (decimal Lower, decimal Upper) Wilson(int successes, int trials)
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
}
