using TarkovCompanion.RecognitionCorpus;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    return args[0] switch
    {
        "validate-manifest" when args.Length == 2 => await ValidateManifest(args[1]),
        "validate-run-plan" when args.Length == 3 => await ValidateRunPlan(args[1], args[2]),
        "validate-predictions" when args.Length == 3 => await ValidatePredictions(args[1], args[2]),
        "validate-aggregate" when args.Length == 3 => await ValidateAggregate(args[1], args[2]),
        "score-and-publish" when args.Length == 7 => await ScoreAndPublish(args[1], args[2], args[3], args[4], args[5], args[6]),
        "publish-aggregate" when args.Length == 4 => await PublishAggregate(args[1], args[2], args[3]),
        _ => InvalidUsage(),
    };
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> ValidateManifest(string manifestPath)
{
    var json = await ReadPrivate(manifestPath);
    return Report(CorpusValidation.ValidatePrivateManifestInterchange(json, DateTimeOffset.UtcNow));
}

static async Task<int> ValidateRunPlan(string runPlanPath, string manifestPath)
{
    var runPlan = await ReadPrivate(runPlanPath);
    var manifest = await ReadPrivate(manifestPath);
    return Report(CorpusValidation.ValidateRunPlanInterchange(runPlan, manifest, DateTimeOffset.UtcNow));
}

static async Task<int> ValidatePredictions(string predictionsPath, string runPlanPath)
{
    var predictions = await ReadPrivate(predictionsPath);
    var runPlan = await ReadPrivate(runPlanPath);
    return Report(CorpusValidation.ValidatePredictionsInterchange(predictions, runPlan));
}

static async Task<int> ValidateAggregate(string aggregatePath, string thresholdsPath)
{
    var aggregate = await ReadExisting(aggregatePath);
    var thresholds = await ReadExisting(thresholdsPath);
    return Report(AggregateResultValidation.ValidateInterchange(aggregate, thresholds));
}

static async Task<int> ScoreAndPublish(
    string manifestPath,
    string runPlanPath,
    string predictionsPath,
    string thresholdsPath,
    string evidenceClassValue,
    string outputPath)
{
    var manifestJson = await ReadPrivate(manifestPath);
    var planJson = await ReadPrivate(runPlanPath);
    var predictionsJson = await ReadPrivate(predictionsPath);
    var thresholdsJson = await ReadExisting(thresholdsPath);
    var manifest = CorpusJson.ParseManifest(manifestJson);
    var plan = CorpusJson.ParseRunPlan(planJson);
    var predictions = CorpusJson.ParsePredictions(predictionsJson);
    var thresholds = CorpusJson.ParseThresholds(thresholdsJson);
    var parseErrors = manifest.Errors
        .Concat(plan.Errors.Select(error => $"Run plan: {error}"))
        .Concat(predictions.Errors.Select(error => $"Predictions: {error}"))
        .Concat(thresholds.Errors.Select(error => $"Thresholds: {error}"))
        .ToArray();
    if (manifest.Value is null || plan.Value is null || predictions.Value is null || thresholds.Value is null || parseErrors.Length != 0)
    {
        return Report(parseErrors);
    }

    var evidenceClass = ParseEvidenceClass(evidenceClassValue);
    var aggregate = IndependentScorer.Score(
        manifest.Value,
        plan.Value,
        predictions.Value,
        thresholds.Value,
        evidenceClass,
        DateTimeOffset.UtcNow);
    var errors = AggregateResultValidation.Validate(aggregate, thresholds.Value);
    if (errors.Count != 0)
    {
        return Report(errors);
    }

    var inputPaths = new[] { manifestPath, runPlanPath, predictionsPath }
        .Select(CorpusValidation.ResolvePrivateInputPath)
        .ToHashSet(PathComparer());
    inputPaths.Add(CorpusValidation.ResolveExistingInputPath(thresholdsPath));
    var resolvedOutput = CorpusValidation.ResolveOutputPath(outputPath);
    if (inputPaths.Contains(resolvedOutput))
    {
        throw new InvalidOperationException("Aggregate output cannot overwrite a scorer input.");
    }

    await WriteAtomic(resolvedOutput, CorpusJson.SerializeAggregateResults(aggregate));
    return 0;
}

static async Task<int> PublishAggregate(string aggregatePath, string thresholdsPath, string outputPath)
{
    var aggregateJson = await ReadExisting(aggregatePath);
    var thresholdsJson = await ReadExisting(thresholdsPath);
    var aggregate = CorpusJson.ParseAggregateResults(aggregateJson);
    var thresholds = CorpusJson.ParseThresholds(thresholdsJson);
    var errors = aggregate.Errors.Concat(thresholds.Errors.Select(error => $"Thresholds: {error}")).ToList();
    if (aggregate.Value is null || thresholds.Value is null)
    {
        return Report(errors);
    }

    errors.AddRange(AggregateResultValidation.Validate(aggregate.Value, thresholds.Value));
    if (errors.Count != 0)
    {
        return Report(errors);
    }

    var resolvedOutput = CorpusValidation.ResolveOutputPath(outputPath);
    var inputPaths = new[]
    {
        CorpusValidation.ResolveExistingInputPath(aggregatePath),
        CorpusValidation.ResolveExistingInputPath(thresholdsPath),
    };
    if (inputPaths.Contains(resolvedOutput, PathComparer()))
    {
        throw new InvalidOperationException("Published output cannot overwrite its aggregate or threshold input.");
    }

    // Re-serializing the typed contract strips benign unknown extensions after the privacy
    // visitor has rejected hostile ones, so publication cannot smuggle opaque payloads through.
    await WriteAtomic(resolvedOutput, CorpusJson.SerializeAggregateResults(aggregate.Value));
    return 0;
}

static async Task<string> ReadPrivate(string path) =>
    await File.ReadAllTextAsync(CorpusValidation.ResolvePrivateInputPath(path));

static async Task<string> ReadExisting(string path) =>
    await File.ReadAllTextAsync(CorpusValidation.ResolveExistingInputPath(path));

static async Task WriteAtomic(string path, string contents)
{
    var output = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Aggregate output requires a parent directory.");
    if (!Directory.Exists(directory))
    {
        throw new DirectoryNotFoundException($"Aggregate output directory does not exist: {directory}");
    }

    var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
    try
    {
        await File.WriteAllTextAsync(temporary, contents);
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

static CorpusEvidenceClass ParseEvidenceClass(string value) => value switch
{
    "real-raster" => CorpusEvidenceClass.RealRaster,
    "synthetic-raster" => CorpusEvidenceClass.SyntheticRaster,
    "post-ocr-evidence" => CorpusEvidenceClass.PostOcrEvidence,
    _ => throw new ArgumentException("Evidence class must be real-raster, synthetic-raster, or post-ocr-evidence."),
};

static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

static int Report(IEnumerable<string> source)
{
    var errors = source.Distinct(StringComparer.Ordinal).ToArray();
    foreach (var error in errors)
    {
        Console.Error.WriteLine(error);
    }

    return errors.Length == 0 ? 0 : 1;
}

static int InvalidUsage()
{
    Usage();
    return 2;
}

static void Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  RecognitionCorpus validate-manifest <outside-repository-private-manifest.json>");
    Console.Error.WriteLine("  RecognitionCorpus validate-run-plan <outside-repository-run-plan.json> <outside-repository-private-manifest.json>");
    Console.Error.WriteLine("  RecognitionCorpus validate-predictions <outside-repository-predictions.json> <outside-repository-run-plan.json>");
    Console.Error.WriteLine("  RecognitionCorpus validate-aggregate <aggregate-results.json> <thresholds.json>");
    Console.Error.WriteLine("  RecognitionCorpus score-and-publish <private-manifest.json> <run-plan.json> <predictions.json> <thresholds.json> <evidence-class> <aggregate-output.json>");
    Console.Error.WriteLine("  RecognitionCorpus publish-aggregate <aggregate-results.json> <thresholds.json> <published-output.json>");
}
