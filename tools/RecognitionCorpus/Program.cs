using TarkovCompanion.RecognitionCorpus;

if (args.Length < 2 || args[0] is not ("validate-manifest" or "validate-run-plan" or "validate-predictions") ||
    (args[0] == "validate-manifest" && args.Length != 2) ||
    (args[0] != "validate-manifest" && args.Length != 3))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  RecognitionCorpus validate-manifest <outside-repository-private-manifest.json>");
    Console.Error.WriteLine("  RecognitionCorpus validate-run-plan <outside-repository-run-plan.json> <outside-repository-private-manifest.json>");
    Console.Error.WriteLine("  RecognitionCorpus validate-predictions <outside-repository-predictions.json> <outside-repository-run-plan.json>");
    return 2;
}

try
{
    foreach (var path in args.Skip(1))
    {
        CorpusValidation.RejectRepositoryPath(path);
    }

    var json = await File.ReadAllTextAsync(args[1]);
    IReadOnlyList<string> errors;
    if (args[0] == "validate-manifest")
    {
        errors = CorpusValidation.ValidatePrivateManifestInterchange(json, DateTimeOffset.UtcNow);
    }
    else
    {
        var contextJson = await File.ReadAllTextAsync(args[2]);
        errors = args[0] == "validate-run-plan"
            ? CorpusValidation.ValidateRunPlanInterchange(json, contextJson, DateTimeOffset.UtcNow)
            : CorpusValidation.ValidatePredictionsInterchange(json, contextJson);
    }

    foreach (var error in errors.Distinct(StringComparer.Ordinal))
    {
        Console.Error.WriteLine(error);
    }

    return errors.Count == 0 ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
