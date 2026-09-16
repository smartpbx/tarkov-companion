using System.Text;

namespace TarkovCompanion.RecognitionCorpus;

/// <summary>
/// The command-line surface, kept out of Program.cs so tests can drive every command against real
/// files without spawning a process. The clock is injectable for tests only; the executable always
/// passes the system clock, so no command lets a caller choose the time consent and retention are
/// judged at.
/// </summary>
public static class CorpusCli
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(time);
        if (args.Count == 0)
        {
            Usage(error);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "validate-manifest" when args.Count == 2 => await ValidateManifest(args[1], error, time, cancellationToken),
                "emit-run-plan" when args.Count == 6 => await EmitRunPlan(args[1], args[2], args[3], args[4], args[5], error, time, cancellationToken),
                "validate-run-plan" when args.Count == 3 => await ValidateRunPlan(args[1], args[2], error, time, cancellationToken),
                "validate-predictions" when args.Count == 3 => await ValidatePredictions(args[1], args[2], error, time, cancellationToken),
                "validate-aggregate" when args.Count == 3 => await ValidateAggregate(args[1], args[2], error, cancellationToken),
                "score-and-publish" when args.Count == 7 => await ScoreAndPublish(args[1], args[2], args[3], args[4], args[5], args[6], error, time, cancellationToken),
                "publish-aggregate" when args.Count == 4 => await PublishAggregate(args[1], args[2], args[3], error, cancellationToken),
                _ => InvalidUsage(error),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only text this tool wrote and marked path-free is printed as is. A runtime I/O
            // exception names the file it failed on, so it becomes a fixed message instead.
            await error.WriteLineAsync(CorpusDiagnostics.DescribeFailure(exception));
            return 1;
        }
    }

    private static async Task<int> ValidateManifest(string manifestPath, TextWriter error, TimeProvider time, CancellationToken cancellationToken)
    {
        var json = await ReadPrivate(manifestPath, cancellationToken);
        return await Report(CorpusValidation.ValidatePrivateManifestInterchange(json, time.GetUtcNow()), error);
    }

    private static async Task<int> EmitRunPlan(
        string manifestPath,
        string runId,
        string producerId,
        string producerVersion,
        string outputPath,
        TextWriter error,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var manifestInput = CorpusPaths.ResolvePrivateInput(manifestPath);
        var output = CorpusPaths.ResolvePrivateOutput(outputPath);
        if (CorpusPaths.SamePath(output, manifestInput))
        {
            throw CorpusDiagnostics.PathFree(new InvalidOperationException("A run plan cannot overwrite its private manifest."));
        }

        var manifestJson = await File.ReadAllTextAsync(manifestInput, cancellationToken);
        var now = time.GetUtcNow();
        var manifest = CorpusJson.ParseManifest(manifestJson);
        if (manifest.Value is null || manifest.Errors.Count != 0)
        {
            return await Report(manifest.Errors, error);
        }

        var manifestErrors = CorpusValidation.ValidateManifest(manifest.Value, now);
        if (manifestErrors.Count != 0)
        {
            return await Report(manifestErrors, error);
        }

        var plan = PrivateRunPlanner.Create(manifest.Value, runId, producerId, producerVersion, now);
        var planJson = CorpusJson.SerializeRunPlan(plan);

        // The emitted bytes, not the in-memory plan, are what a producer receives. Parse and
        // validate them against the same private manifest before they exist on disk, so an id or
        // version that the interchange privacy rules reject is never written.
        var roundTripErrors = CorpusValidation.ValidateRunPlanInterchange(planJson, manifestJson, now);
        if (roundTripErrors.Count != 0)
        {
            return await Report(roundTripErrors, error);
        }

        await WriteAtomic(output, planJson, cancellationToken);
        return 0;
    }

    private static async Task<int> ValidateRunPlan(string runPlanPath, string manifestPath, TextWriter error, TimeProvider time, CancellationToken cancellationToken)
    {
        var runPlan = await ReadPrivate(runPlanPath, cancellationToken);
        var manifest = await ReadPrivate(manifestPath, cancellationToken);
        return await Report(CorpusValidation.ValidateRunPlanInterchange(runPlan, manifest, time.GetUtcNow()), error);
    }

    private static async Task<int> ValidatePredictions(string predictionsPath, string runPlanPath, TextWriter error, TimeProvider time, CancellationToken cancellationToken)
    {
        var predictions = await ReadPrivate(predictionsPath, cancellationToken);
        var runPlan = await ReadPrivate(runPlanPath, cancellationToken);
        return await Report(CorpusValidation.ValidatePredictionsInterchange(predictions, runPlan, time.GetUtcNow()), error);
    }

    private static async Task<int> ValidateAggregate(string aggregatePath, string thresholdsPath, TextWriter error, CancellationToken cancellationToken)
    {
        var aggregate = await ReadExisting(aggregatePath, cancellationToken);
        var thresholds = await ReadExisting(thresholdsPath, cancellationToken);
        return await Report(AggregateResultValidation.ValidateInterchange(aggregate, thresholds), error);
    }

    private static async Task<int> ScoreAndPublish(
        string manifestPath,
        string runPlanPath,
        string predictionsPath,
        string thresholdsPath,
        string evidenceClassValue,
        string outputPath,
        TextWriter error,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var evidenceClass = ParseEvidenceClass(evidenceClassValue);
        var privateInputs = new[] { manifestPath, runPlanPath, predictionsPath }.Select(CorpusPaths.ResolvePrivateInput).ToArray();
        var thresholdsInput = CorpusPaths.ResolveExistingInput(thresholdsPath);
        var output = CorpusPaths.ResolvePublicationOutput(outputPath);
        if (privateInputs.Append(thresholdsInput).Any(input => CorpusPaths.SamePath(input, output)))
        {
            throw CorpusDiagnostics.PathFree(new InvalidOperationException("Aggregate output cannot overwrite a scorer input."));
        }

        var manifest = CorpusJson.ParseManifest(await File.ReadAllTextAsync(privateInputs[0], cancellationToken));
        var plan = CorpusJson.ParseRunPlan(await File.ReadAllTextAsync(privateInputs[1], cancellationToken));
        var predictions = CorpusJson.ParsePredictions(await File.ReadAllTextAsync(privateInputs[2], cancellationToken));
        var thresholds = CorpusJson.ParseThresholds(await File.ReadAllTextAsync(thresholdsInput, cancellationToken));
        var parseErrors = manifest.Errors
            .Concat(plan.Errors.Select(message => $"Run plan: {message}"))
            .Concat(predictions.Errors.Select(message => $"Predictions: {message}"))
            .Concat(thresholds.Errors.Select(message => $"Thresholds: {message}"))
            .ToArray();
        if (manifest.Value is null || plan.Value is null || predictions.Value is null || thresholds.Value is null || parseErrors.Length != 0)
        {
            return await Report(parseErrors, error);
        }

        var aggregate = IndependentScorer.Score(
            manifest.Value,
            plan.Value,
            predictions.Value,
            thresholds.Value,
            evidenceClass,
            time.GetUtcNow());
        var errors = AggregateResultValidation.Validate(aggregate, thresholds.Value);
        if (errors.Count != 0)
        {
            return await Report(errors, error);
        }

        await WriteAtomic(output, CorpusJson.SerializeAggregateResults(aggregate), cancellationToken);
        return 0;
    }

    private static async Task<int> PublishAggregate(string aggregatePath, string thresholdsPath, string outputPath, TextWriter error, CancellationToken cancellationToken)
    {
        var aggregateInput = CorpusPaths.ResolveExistingInput(aggregatePath);
        var thresholdsInput = CorpusPaths.ResolveExistingInput(thresholdsPath);
        var output = CorpusPaths.ResolvePublicationOutput(outputPath);
        if (CorpusPaths.SamePath(output, aggregateInput) || CorpusPaths.SamePath(output, thresholdsInput))
        {
            throw CorpusDiagnostics.PathFree(new InvalidOperationException("Published output cannot overwrite its aggregate or threshold input."));
        }

        var aggregate = CorpusJson.ParseAggregateResults(await File.ReadAllTextAsync(aggregateInput, cancellationToken));
        var thresholds = CorpusJson.ParseThresholds(await File.ReadAllTextAsync(thresholdsInput, cancellationToken));
        var errors = aggregate.Errors.Concat(thresholds.Errors.Select(message => $"Thresholds: {message}")).ToList();
        if (aggregate.Value is null || thresholds.Value is null)
        {
            return await Report(errors, error);
        }

        errors.AddRange(AggregateResultValidation.Validate(aggregate.Value, thresholds.Value));
        if (errors.Count != 0)
        {
            return await Report(errors, error);
        }

        // Re-serializing the typed contract strips benign unknown extensions after the privacy
        // visitor has rejected hostile ones, so publication cannot smuggle opaque payloads through.
        await WriteAtomic(output, CorpusJson.SerializeAggregateResults(aggregate.Value), cancellationToken);
        return 0;
    }

    private static async Task<string> ReadPrivate(string path, CancellationToken cancellationToken) =>
        await File.ReadAllTextAsync(CorpusPaths.ResolvePrivateInput(path), cancellationToken);

    private static async Task<string> ReadExisting(string path, CancellationToken cancellationToken) =>
        await File.ReadAllTextAsync(CorpusPaths.ResolveExistingInput(path), cancellationToken);

    /// <summary>
    /// Writes beside the destination and renames over it. The temporary name is new and opened
    /// with CreateNew, so a planted file or link at that name fails instead of being followed.
    /// </summary>
    private static async Task WriteAtomic(string output, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(output) ?? throw CorpusDiagnostics.PathFree(new InvalidOperationException("A recognition corpus output requires a parent directory."));
        var temporary = Path.Join(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), cancellationToken);
            }

            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static CorpusEvidenceClass ParseEvidenceClass(string value) => value switch
    {
        "real-raster" => CorpusEvidenceClass.RealRaster,
        "synthetic-raster" => CorpusEvidenceClass.SyntheticRaster,
        "post-ocr-evidence" => CorpusEvidenceClass.PostOcrEvidence,
        _ => throw CorpusDiagnostics.PathFree(new ArgumentException("Evidence class must be real-raster, synthetic-raster, or post-ocr-evidence.")),
    };

    private static async Task<int> Report(IEnumerable<string> source, TextWriter error)
    {
        var errors = source.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var message in errors)
        {
            await error.WriteLineAsync(message);
        }

        return errors.Length == 0 ? 0 : 1;
    }

    private static int InvalidUsage(TextWriter error)
    {
        Usage(error);
        return 2;
    }

    private static void Usage(TextWriter error)
    {
        error.WriteLine("Usage:");
        error.WriteLine("  RecognitionCorpus validate-manifest <outside-repository-private-manifest.json>");
        error.WriteLine("  RecognitionCorpus emit-run-plan <outside-repository-private-manifest.json> <run-id> <producer-id> <producer-version> <outside-repository-run-plan.json>");
        error.WriteLine("  RecognitionCorpus validate-run-plan <outside-repository-run-plan.json> <outside-repository-private-manifest.json>");
        error.WriteLine("  RecognitionCorpus validate-predictions <outside-repository-predictions.json> <outside-repository-run-plan.json>");
        error.WriteLine("  RecognitionCorpus validate-aggregate <aggregate-results.json> <thresholds.json>");
        error.WriteLine("  RecognitionCorpus score-and-publish <private-manifest.json> <run-plan.json> <predictions.json> <thresholds.json> <evidence-class> <aggregate-output.json>");
        error.WriteLine("  RecognitionCorpus publish-aggregate <aggregate-results.json> <thresholds.json> <published-output.json>");
    }
}
