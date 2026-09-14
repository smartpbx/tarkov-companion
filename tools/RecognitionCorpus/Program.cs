using TarkovCompanion.RecognitionCorpus;

if (args.Length != 2 || args[0] is not ("validate-manifest" or "validate-run-plan" or "validate-predictions"))
{
    Console.Error.WriteLine("Usage: RecognitionCorpus validate-manifest|validate-run-plan|validate-predictions <outside-repository-json>");
    return 2;
}

CorpusValidation.RejectRepositoryPath(args[1]);
var json = await File.ReadAllTextAsync(args[1]);
var errors = args[0] == "validate-manifest"
    ? CorpusValidation.ValidatePrivateManifestInterchange(json)
    : CorpusValidation.ValidateTruthFreeInterchange(json, args[0] == "validate-run-plan" ? "run-plan.v1" : "predictions.v1");
foreach (var error in errors)
{
    Console.Error.WriteLine(error);
}

return errors.Count == 0 ? 0 : 1;
