namespace TarkovCompanion.RecognitionCorpus;

/// <summary>
/// Publication validation deliberately derives every rate and disposition from counts and the
/// frozen policy. Producer-supplied booleans, rates, and status strings are never treated as
/// proof that an aggregate is safe or correct.
/// </summary>
public static class AggregateResultValidation
{
    public static IReadOnlyList<string> Validate(AggregateResults aggregate, FrozenThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(thresholds);
        var errors = CorpusValidation.ValidateThresholds(thresholds).ToList();
        if (!CorpusValidation.OpaqueId(aggregate.RunId) || !CorpusValidation.OpaqueId(aggregate.ProducerId) ||
            !BoundedToken(aggregate.ProducerVersion) || !CorpusValidation.OpaqueId(aggregate.CorpusId) ||
            !CorpusValidation.Sha256(aggregate.PlanLock) || aggregate.ScoredUtc == DateTimeOffset.MinValue ||
            aggregate.ScoredUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(aggregate.EvidenceClass))
        {
            errors.Add("Aggregate results require bounded run, producer, corpus, plan-lock, UTC-time, and evidence-class provenance.");
        }

        if (!string.Equals(aggregate.PolicyVersion, thresholds.PolicyVersion, StringComparison.Ordinal))
        {
            errors.Add("Aggregate results must name the exact frozen threshold policy.");
        }

        if (aggregate.Privacy is null || aggregate.Slices is null)
        {
            return [.. errors, "Aggregate results require privacy metadata and all required slices."];
        }

        var requiredIntents = Enum.GetValues<BenchmarkIntent>();
        var uniqueIntents = new HashSet<BenchmarkIntent>();
        foreach (var slice in aggregate.Slices)
        {
            if (slice is null)
            {
                errors.Add("Aggregate results cannot contain a null slice.");
                continue;
            }

            if (!Enum.IsDefined(slice.Intent) || !uniqueIntents.Add(slice.Intent))
            {
                errors.Add("Aggregate results require one unique slice for every benchmark intent.");
            }

            if (!Enum.IsDefined(slice.EvidenceClass) || slice.EvidenceClass != aggregate.EvidenceClass)
            {
                errors.Add("Aggregate slices cannot mix evidence classes or disagree with their document evidence class.");
            }

            ValidateSlice(slice, thresholds, errors);
        }

        if (aggregate.Slices.Count != requiredIntents.Length ||
            requiredIntents.Any(intent => !uniqueIntents.Contains(intent)))
        {
            errors.Add("Aggregate results require exactly one slice for each frozen benchmark intent.");
        }

        var nonEmptySlices = aggregate.Slices.Where(slice => slice is not null && IndependentScorer.ContainsAggregateEvidence(slice)).ToArray();
        var computedSafeToPublish = nonEmptySlices.Length > 0 &&
                                    nonEmptySlices.All(slice => slice.IndependentSplitUnits >= thresholds.MinimumIndependentSplitUnits);
        if (aggregate.Privacy.MinimumIndependentSplitUnits != thresholds.MinimumIndependentSplitUnits ||
            aggregate.Privacy.ContainsPerSampleResults ||
            aggregate.Privacy.SafeToPublish != computedSafeToPublish || !computedSafeToPublish)
        {
            errors.Add("Aggregate privacy safety must be recomputed from non-empty slice units; asserted publication flags are not authority.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateInterchange(string aggregateJson, string thresholdsJson)
    {
        var aggregate = CorpusJson.ParseAggregateResults(aggregateJson);
        var thresholds = CorpusJson.ParseThresholds(thresholdsJson);
        if (aggregate.Value is null || thresholds.Value is null)
        {
            return [.. aggregate.Errors, .. thresholds.Errors.Select(error => $"Threshold context: {error}")];
        }

        return
        [
            .. aggregate.Errors,
            .. thresholds.Errors.Select(error => $"Threshold context: {error}"),
            .. Validate(aggregate.Value, thresholds.Value),
        ];
    }

    private static void ValidateSlice(SliceMetrics slice, FrozenThresholds thresholds, ICollection<string> errors)
    {
        var counts = new[]
        {
            slice.Numerator, slice.Denominator, slice.ExcludedUnknowns, slice.ExcludedPredictionClaims,
            slice.IndependentSplitUnits, slice.AttemptedKnownClaims, slice.TruePositives, slice.FalsePositives,
            slice.FalseNegatives, slice.Abstentions, slice.ConfidentWrong, slice.MissingFrames,
            slice.ReorderedFrames, slice.OverlapDeduplicationErrors, slice.AccuracyNumerator,
            slice.AccuracyDenominator, slice.RecallNumerator, slice.RecallDenominator,
            slice.FalsePositiveNumerator, slice.FalsePositiveDenominator, slice.PerformanceSampleCount,
        };
        if (counts.Any(value => value < 0))
        {
            errors.Add($"Aggregate slice {slice.Intent} contains a negative count.");
            return;
        }

        var denominator = (long)slice.TruePositives + slice.FalseNegatives;
        var accuracyDenominator = denominator + slice.FalsePositives;
        var falsePositiveDenominator = (long)slice.TruePositives + slice.FalsePositives;
        var f1Denominator = (2L * slice.TruePositives) + slice.FalsePositives + slice.FalseNegatives;
        if (denominator > int.MaxValue || accuracyDenominator > int.MaxValue ||
            falsePositiveDenominator > int.MaxValue || f1Denominator > int.MaxValue)
        {
            errors.Add($"Aggregate slice {slice.Intent} count arithmetic overflows the v1 contract.");
            return;
        }

        if (slice.Numerator != slice.TruePositives || slice.Denominator != denominator ||
            slice.AccuracyNumerator != slice.TruePositives || slice.AccuracyDenominator != accuracyDenominator ||
            slice.RecallNumerator != slice.TruePositives || slice.RecallDenominator != denominator ||
            slice.FalsePositiveNumerator != slice.FalsePositives || slice.FalsePositiveDenominator != falsePositiveDenominator ||
            slice.AttemptedKnownClaims > denominator || slice.TruePositives > slice.AttemptedKnownClaims ||
            slice.Abstentions > slice.AttemptedKnownClaims || slice.ConfidentWrong > slice.FalseNegatives)
        {
            errors.Add($"Aggregate slice {slice.Intent} count arithmetic is inconsistent.");
        }

        var expectedCoverage = Rate(slice.AttemptedKnownClaims, denominator);
        var expectedAccuracy = Rate(slice.TruePositives, accuracyDenominator);
        var expectedRecall = Rate(slice.TruePositives, denominator);
        var expectedFalsePositiveRate = Rate(slice.FalsePositives, falsePositiveDenominator);
        var expectedF1 = Rate(2L * slice.TruePositives, f1Denominator);
        var expectedAbstentionRate = Rate(slice.Abstentions, denominator);
        var expectedConfidentWrongRate = Rate(slice.ConfidentWrong, denominator);
        var expectedInterval = IndependentScorer.Wilson(slice.TruePositives, (int)denominator);
        if (slice.Coverage != expectedCoverage || slice.Accuracy != expectedAccuracy ||
            slice.Recall != expectedRecall || slice.FalsePositiveRate != expectedFalsePositiveRate ||
            slice.F1 != expectedF1 || slice.AbstentionRate != expectedAbstentionRate ||
            slice.ConfidentWrongRate != expectedConfidentWrongRate ||
            slice.ConfidenceIntervalLower != expectedInterval.Lower ||
            slice.ConfidenceIntervalUpper != expectedInterval.Upper)
        {
            errors.Add($"Aggregate slice {slice.Intent} rates or confidence interval do not match their counts.");
        }

        var underpowered = slice.IndependentSplitUnits < thresholds.MinimumIndependentSplitUnits ||
                           denominator < thresholds.MinimumKnownClaims;
        var passes = expectedCoverage >= thresholds.MinimumCoverage &&
                     expectedAbstentionRate <= thresholds.MaximumAbstentionRate &&
                     expectedConfidentWrongRate <= thresholds.MaximumConfidentWrongRate &&
                     expectedF1 >= thresholds.MinimumF1;
        var expectedStatus = underpowered ? "insufficient-data" : passes ? "pass" : "fail";
        if (!string.Equals(slice.Status, expectedStatus, StringComparison.Ordinal))
        {
            errors.Add($"Aggregate slice {slice.Intent} status does not match the frozen thresholds.");
        }

        if (slice.PerformanceSampleCount == 0)
        {
            if (slice.MeanElapsedMilliseconds is not null || slice.MaximumElapsedMilliseconds is not null)
            {
                errors.Add($"Aggregate slice {slice.Intent} cannot report timing without performance samples.");
            }
        }
        else if (slice.MeanElapsedMilliseconds is null || slice.MaximumElapsedMilliseconds is null)
        {
            errors.Add($"Aggregate slice {slice.Intent} has inconsistent bounded performance aggregates.");
        }
        else
        {
            var mean = slice.MeanElapsedMilliseconds.Value;
            var maximum = slice.MaximumElapsedMilliseconds.Value;
            if (mean is < 0 or > CorpusValidation.MaximumElapsedMilliseconds ||
                maximum is < 0 or > CorpusValidation.MaximumElapsedMilliseconds || mean > maximum)
            {
                errors.Add($"Aggregate slice {slice.Intent} has inconsistent bounded performance aggregates.");
            }
        }
    }

    private static decimal Rate(long numerator, long denominator) => denominator == 0 ? 0 : (decimal)numerator / denominator;

    private static bool BoundedToken(string? value) =>
        value is { Length: >= 1 and <= 128 } && !string.IsNullOrWhiteSpace(value);
}
