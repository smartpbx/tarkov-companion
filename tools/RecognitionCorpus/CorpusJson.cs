using System.Globalization;
using System.Text.Json;

namespace TarkovCompanion.RecognitionCorpus;

public sealed record CorpusParseResult<T>(T? Value, IReadOnlyList<string> Errors) where T : class;

public static class CorpusJson
{
    public static CorpusParseResult<CorpusManifest> ParseManifest(string json)
    {
        if (!TryRoot(json, CorpusValidation.ManifestSchemaVersion, true, out var document, out var errors))
        {
            return new(null, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            var samples = Array(root, "samples", "$", errors).Select((element, index) => ParseSample(element, $"$.samples[{index}]", errors)).ToArray();
            var privateEvidence = Array(root, "privateEvidence", "$", errors)
                .Select((element, index) => ParsePrivateEvidence(element, $"$.privateEvidence[{index}]", errors)).ToArray();
            var manifest = new CorpusManifest(
                String(root, "corpusId", "$", errors),
                String(root, "nearDuplicateGraphVersion", "$", errors),
                samples,
                privateEvidence);
            return new(manifest, errors);
        }
    }

    public static CorpusParseResult<RunPlan> ParseRunPlan(string json)
    {
        if (!TryRoot(json, CorpusValidation.RunPlanSchemaVersion, false, out var document, out var errors))
        {
            return new(null, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            var producer = Object(root, "producer", "$", errors);
            var samples = Array(root, "samples", "$", errors).Select((element, index) =>
            {
                var path = $"$.samples[{index}]";
                return new RunPlanSample(
                    String(element, "sampleId", path, errors),
                    ParseSplit(String(element, "split", path, errors), path, errors),
                    ParseIntent(String(element, "intent", path, errors), path, errors),
                    ParseEvidenceClass(String(element, "evidenceClass", path, errors), path, errors),
                    ParseContext(Object(element, "provenance", path, errors), path + ".provenance", errors),
                    ParseLineage(Object(element, "lineage", path, errors), path + ".lineage", errors));
            }).ToArray();
            var plan = new RunPlan(
                String(root, "runId", "$", errors),
                String(producer, "id", "$.producer", errors),
                String(producer, "version", "$.producer", errors),
                String(root, "policyVersion", "$", errors),
                String(root, "corpusId", "$", errors),
                String(root, "nearDuplicateGraphVersion", "$", errors),
                String(root, "planLock", "$", errors),
                samples);
            return new(plan, errors);
        }
    }

    public static CorpusParseResult<PredictionDocument> ParsePredictions(string json)
    {
        if (!TryRoot(json, CorpusValidation.PredictionsSchemaVersion, false, out var document, out var errors))
        {
            return new(null, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            var producer = Object(root, "producer", "$", errors);
            var predictions = Array(root, "predictions", "$", errors).Select((element, index) =>
            {
                var path = $"$.predictions[{index}]";
                var performance = Object(element, "performance", path, errors);
                var claims = Array(element, "claims", path, errors).Select((claim, claimIndex) =>
                {
                    var claimPath = $"{path}.claims[{claimIndex}]";
                    return new PredictionClaim(
                        String(claim, "claimId", claimPath, errors),
                        String(claim, "kind", claimPath, errors),
                        NullableString(claim, "value", claimPath, errors),
                        NullableObject(claim, "region", claimPath, errors) is { } region ? ParseRegion(region, claimPath + ".region", errors) : null);
                }).ToArray();
                return new ProducerPrediction(
                    String(element, "sampleId", path, errors),
                    ParseIntent(String(element, "intent", path, errors), path, errors),
                    ParseEvidenceClass(String(element, "evidenceClass", path, errors), path, errors),
                    ParsePredictionType(String(element, "type", path, errors), path, errors),
                    ParsePredictionStatus(String(element, "status", path, errors), path, errors),
                    Decimal(element, "confidence", path, errors),
                    claims,
                    Decimal(performance, "elapsedMilliseconds", path + ".performance", errors));
            }).ToArray();
            var predictionsDocument = new PredictionDocument(
                String(root, "runId", "$", errors),
                String(producer, "id", "$.producer", errors),
                String(producer, "version", "$.producer", errors),
                predictions);
            return new(predictionsDocument, errors);
        }
    }

    private static CorpusSample ParseSample(JsonElement element, string path, ICollection<string> errors)
    {
        RequireObject(element, path, errors);
        var content = Object(element, "content", path, errors);
        var truth = Object(element, "truth", path, errors);
        var claims = Array(truth, "claims", path + ".truth", errors).Select((claim, index) =>
        {
            var claimPath = $"{path}.truth.claims[{index}]";
            return new TruthClaim(
                String(claim, "truthId", claimPath, errors),
                ParseTruthState(String(claim, "state", claimPath, errors), claimPath, errors),
                String(claim, "kind", claimPath, errors),
                NullableString(claim, "value", claimPath, errors),
                NullableObject(claim, "region", claimPath, errors) is { } region ? ParseRegion(region, claimPath + ".region", errors) : null);
        }).ToArray();
        return new CorpusSample(
            String(element, "sampleId", path, errors),
            ParseEvidenceClass(String(element, "evidenceClass", path, errors), path, errors),
            NullableString(content, "decodedPixelSha256", path + ".content", errors),
            StringArray(content, "nearDuplicateIds", path + ".content", errors),
            String(element, "splitUnitId", path, errors),
            ParseContext(Object(element, "provenance", path, errors), path + ".provenance", errors),
            ParseLineage(Object(element, "lineage", path, errors), path + ".lineage", errors),
            claims,
            NullableString(element, "consentHash", path, errors),
            NullableString(element, "privacyReviewHash", path, errors));
    }

    private static PrivateSampleEvidence ParsePrivateEvidence(JsonElement element, string path, ICollection<string> errors)
    {
        RequireObject(element, path, errors);
        var consentElement = NullableObject(element, "consent", path, errors);
        var reviewElement = NullableObject(element, "privacyReview", path, errors);
        return new PrivateSampleEvidence(
            String(element, "sampleId", path, errors),
            NullableString(element, "observedDecodedPixelSha256", path, errors),
            consentElement is null ? null : ParseConsent(consentElement.Value, path + ".consent", errors),
            reviewElement is null ? null : ParsePrivacyReview(reviewElement.Value, path + ".privacyReview", errors));
    }

    private static ConsentEvidence ParseConsent(JsonElement element, string path, ICollection<string> errors)
    {
        Const(element, "schemaVersion", "consent.v1", path, errors);
        var retention = Object(element, "retention", path, errors);
        var revocation = Object(element, "revocation", path, errors);
        return new ConsentEvidence(
            String(element, "consentId", path, errors),
            String(element, "consentHash", path, errors),
            StringArray(element, "allowedUses", path, errors),
            Utc(element, "consentedUtc", path, errors),
            Utc(retention, "expiresUtc", path + ".retention", errors),
            String(revocation, "state", path + ".revocation", errors));
    }

    private static PrivacyReviewEvidence ParsePrivacyReview(JsonElement element, string path, ICollection<string> errors)
    {
        Const(element, "schemaVersion", "privacy-review.v1", path, errors);
        return new PrivacyReviewEvidence(
            String(element, "reviewId", path, errors),
            String(element, "reviewHash", path, errors),
            String(element, "state", path, errors),
            String(element, "redaction", path, errors),
            Utc(element, "reviewedUtc", path, errors));
    }

    private static CaptureContext ParseContext(JsonElement element, string path, ICollection<string> errors)
    {
        Const(element, "schemaVersion", "provenance.v1", path, errors);
        var resolution = Object(element, "resolution", path, errors);
        return new CaptureContext(
            String(element, "captureIntentId", path, errors),
            String(element, "sessionId", path, errors),
            String(element, "correlationId", path, errors),
            Integer(element, "captureOrdinal", path, errors),
            NullableString(element, "workspaceId", path, errors),
            NullableString(element, "profileId", path, errors),
            NullableString(element, "mapId", path, errors),
            NullableString(element, "floorId", path, errors),
            NullableString(element, "planId", path, errors),
            StringArray(element, "objectiveIds", path, errors),
            NullableString(element, "selectedReference", path, errors),
            NullableString(element, "priorScanReference", path, errors),
            String(element, "deviceClass", path, errors),
            String(element, "surface", path, errors),
            Integer(resolution, "width", path + ".resolution", errors),
            Integer(resolution, "height", path + ".resolution", errors),
            Decimal(element, "uiScale", path, errors),
            String(element, "locale", path, errors),
            String(element, "gameVersion", path, errors),
            String(element, "companionUiVersion", path, errors));
    }

    private static SequenceLineage ParseLineage(JsonElement element, string path, ICollection<string> errors) => new(
        String(element, "sequenceId", path, errors),
        Integer(element, "frameOrdinal", path, errors),
        String(element, "viewportId", path, errors),
        String(element, "containerIdentity", path, errors),
        Decimal(element, "overlapWithPrevious", path, errors),
        NullableString(element, "parentContainerIdentity", path, errors));

    private static PixelRegion ParseRegion(JsonElement element, string path, ICollection<string> errors) => new(
        Integer(element, "x", path, errors),
        Integer(element, "y", path, errors),
        Integer(element, "width", path, errors),
        Integer(element, "height", path, errors));

    private static bool TryRoot(
        string json,
        string expectedSchemaVersion,
        bool privateManifest,
        out JsonDocument document,
        out List<string> errors)
    {
        errors = [];
        document = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add("The JSON document is empty.");
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            errors.Add($"Malformed JSON: {exception.Message}");
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The JSON document root must be an object.");
            document.Dispose();
            document = null!;
            return false;
        }

        Const(document.RootElement, "schemaVersion", expectedSchemaVersion, "$", errors);
        RejectDuplicateProperties(document.RootElement, "$", errors);
        errors.AddRange(CorpusValidation.PrivacyErrors(document.RootElement, privateManifest));
        return true;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, ICollection<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    errors.Add($"{path} repeats JSON property {property.Name}.");
                }

                RejectDuplicateProperties(property.Value, path + "." + property.Name, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in element.EnumerateArray())
            {
                RejectDuplicateProperties(value, $"{path}[{index}]", errors);
                index++;
            }
        }
    }

    private static JsonElement Object(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        errors.Add($"{path}.{name} is required and must be an object.");
        return default;
    }

    private static JsonElement? NullableObject(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            errors.Add($"{path}.{name} is required (object or null).");
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        errors.Add($"{path}.{name} must be an object or null.");
        return null;
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().ToArray();
        }

        errors.Add($"{path}.{name} is required and must be an array.");
        return [];
    }

    private static string String(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        errors.Add($"{path}.{name} is required and must be a string.");
        return string.Empty;
    }

    private static string? NullableString(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            errors.Add($"{path}.{name} is required (string or null).");
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors.Add($"{path}.{name} must be a string or null.");
        return null;
    }

    private static IReadOnlyList<string> StringArray(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        var values = Array(parent, name, path, errors);
        var result = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].ValueKind == JsonValueKind.String)
            {
                result.Add(values[index].GetString()!);
            }
            else
            {
                errors.Add($"{path}.{name}[{index}] must be a string.");
            }
        }

        return result;
    }

    private static int Integer(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
        {
            return result;
        }

        errors.Add($"{path}.{name} is required and must be a 32-bit integer.");
        return int.MinValue;
    }

    private static decimal Decimal(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result))
        {
            return result;
        }

        errors.Add($"{path}.{name} is required and must be a finite decimal number.");
        return decimal.MinValue;
    }

    private static DateTimeOffset Utc(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        var value = String(parent, name, path, errors);
        if (value.EndsWith('Z') && DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            return result;
        }

        errors.Add($"{path}.{name} must be a valid UTC timestamp ending in Z.");
        return DateTimeOffset.MinValue;
    }

    private static void Const(JsonElement parent, string name, string expected, string path, ICollection<string> errors)
    {
        var actual = String(parent, name, path, errors);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            errors.Add($"{path}.{name} must equal {expected}.");
        }
    }

    private static void RequireObject(JsonElement value, string path, ICollection<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object.");
        }
    }

    private static CorpusEvidenceClass ParseEvidenceClass(string value, string path, ICollection<string> errors) => value switch
    {
        "real-raster" => CorpusEvidenceClass.RealRaster,
        "synthetic-raster" => CorpusEvidenceClass.SyntheticRaster,
        "post-ocr-evidence" => CorpusEvidenceClass.PostOcrEvidence,
        _ => Invalid(value, path, "evidence class", errors, (CorpusEvidenceClass)(-1)),
    };

    private static TruthState ParseTruthState(string value, string path, ICollection<string> errors) => value switch
    {
        "known" => TruthState.Known,
        "unknown" => TruthState.Unknown,
        _ => Invalid(value, path, "truth state", errors, (TruthState)(-1)),
    };

    private static CorpusSplit ParseSplit(string value, string path, ICollection<string> errors) => value switch
    {
        "train" => CorpusSplit.Train,
        "tune" => CorpusSplit.Tune,
        "test" => CorpusSplit.Test,
        _ => Invalid(value, path, "split", errors, (CorpusSplit)(-1)),
    };

    private static BenchmarkIntent ParseIntent(string value, string path, ICollection<string> errors) => value switch
    {
        "loot-decision" => BenchmarkIntent.LootDecision,
        "full-stash" => BenchmarkIntent.FullStash,
        "ammo" => BenchmarkIntent.Ammo,
        "keys" => BenchmarkIntent.Keys,
        "quest-items" => BenchmarkIntent.QuestItems,
        "map-extracts-timers" => BenchmarkIntent.MapExtractsTimers,
        "health-character" => BenchmarkIntent.HealthCharacter,
        "auto-detect" => BenchmarkIntent.AutoDetect,
        _ => Invalid(value, path, "benchmark intent", errors, (BenchmarkIntent)(-1)),
    };

    private static PredictionStatus ParsePredictionStatus(string value, string path, ICollection<string> errors) => value switch
    {
        "detected" => PredictionStatus.Detected,
        "abstained" => PredictionStatus.Abstained,
        "unavailable" => PredictionStatus.Unavailable,
        _ => Invalid(value, path, "prediction status", errors, (PredictionStatus)(-1)),
    };

    private static PredictionType ParsePredictionType(string value, string path, ICollection<string> errors)
    {
        if (PredictionTypeNames.TryParse(value, out var type))
        {
            return type;
        }

        return Invalid(value, path, "prediction type", errors, (PredictionType)(-1));
    }

    private static T Invalid<T>(string value, string path, string kind, ICollection<string> errors, T invalid)
    {
        errors.Add($"{path} contains unsupported {kind} '{value}'.");
        return invalid;
    }
}
