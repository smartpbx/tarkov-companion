using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.RecognitionCorpus;

public sealed record CorpusParseResult<T>(T? Value, IReadOnlyList<string> Errors) where T : class;

public static class CorpusJson
{
    private static readonly HashSet<string> NestedSchemaVersions = new(StringComparer.Ordinal)
    {
        CorpusValidation.ProvenanceSchemaVersion,
        CorpusValidation.ConsentSchemaVersion,
        CorpusValidation.PrivacyReviewSchemaVersion,
    };

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
                String(root, "planLock", "$", errors),
                predictions);
            return new(predictionsDocument, errors);
        }
    }

    public static CorpusParseResult<FrozenThresholds> ParseThresholds(string json)
    {
        if (!TryRoot(json, CorpusValidation.ThresholdsSchemaVersion, false, out var document, out var errors))
        {
            return new(null, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            var candidates = Object(root, "candidateThresholds", "$", errors);
            if (!Boolean(root, "frozenBeforeTuning", "$", errors))
            {
                errors.Add("$.frozenBeforeTuning must equal true.");
            }

            var disposition = String(root, "unsupportedDisposition", "$", errors);
            if (!string.Equals(disposition, "insufficient-data", StringComparison.Ordinal))
            {
                errors.Add("$.unsupportedDisposition must equal insufficient-data.");
            }

            return new(new FrozenThresholds(
                String(root, "policyVersion", "$", errors),
                Integer(root, "minimumIndependentSplitUnits", "$", errors),
                Integer(root, "minimumKnownClaims", "$", errors),
                Decimal(candidates, "minimumCoverage", "$.candidateThresholds", errors),
                Decimal(candidates, "maximumAbstentionRate", "$.candidateThresholds", errors),
                Decimal(candidates, "maximumConfidentWrongRate", "$.candidateThresholds", errors),
                Decimal(candidates, "minimumF1", "$.candidateThresholds", errors)), errors);
        }
    }

    public static CorpusParseResult<AggregateResults> ParseAggregateResults(string json)
    {
        if (!TryRoot(json, CorpusValidation.AggregateResultsSchemaVersion, false, out var document, out var errors))
        {
            return new(null, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            errors.AddRange(CorpusValidation.AggregatePrivacyErrors(root));
            var producer = Object(root, "producer", "$", errors);
            var privacy = Object(root, "privacy", "$", errors);
            var slices = Array(root, "slices", "$", errors).Select((element, index) =>
            {
                var path = $"$.slices[{index}]";
                return new SliceMetrics(
                    ParseIntent(String(element, "intent", path, errors), path, errors),
                    ParseEvidenceClass(String(element, "evidenceClass", path, errors), path, errors),
                    String(element, "status", path, errors),
                    Integer(element, "numerator", path, errors),
                    Integer(element, "denominator", path, errors),
                    Integer(element, "excludedUnknowns", path, errors),
                    Integer(element, "excludedPredictionClaims", path, errors),
                    Integer(element, "independentSplitUnits", path, errors),
                    Integer(element, "attemptedKnownClaims", path, errors),
                    Integer(element, "truePositives", path, errors),
                    Integer(element, "falsePositives", path, errors),
                    Integer(element, "falseNegatives", path, errors),
                    Integer(element, "abstentions", path, errors),
                    Integer(element, "confidentWrong", path, errors),
                    Integer(element, "missingFrames", path, errors),
                    Integer(element, "reorderedFrames", path, errors),
                    Integer(element, "overlapDeduplicationErrors", path, errors),
                    Integer(element, "accuracyNumerator", path, errors),
                    Integer(element, "accuracyDenominator", path, errors),
                    Integer(element, "recallNumerator", path, errors),
                    Integer(element, "recallDenominator", path, errors),
                    Integer(element, "falsePositiveNumerator", path, errors),
                    Integer(element, "falsePositiveDenominator", path, errors),
                    Decimal(element, "coverage", path, errors),
                    Decimal(element, "accuracy", path, errors),
                    Decimal(element, "recall", path, errors),
                    Decimal(element, "falsePositiveRate", path, errors),
                    Decimal(element, "f1", path, errors),
                    Decimal(element, "abstentionRate", path, errors),
                    Decimal(element, "confidentWrongRate", path, errors),
                    Decimal(element, "confidenceIntervalLower", path, errors),
                    Decimal(element, "confidenceIntervalUpper", path, errors),
                    Integer(element, "performanceSampleCount", path, errors),
                    NullableDecimal(element, "meanElapsedMilliseconds", path, errors),
                    NullableDecimal(element, "maximumElapsedMilliseconds", path, errors));
            }).ToArray();
            return new(new AggregateResults(
                String(root, "runId", "$", errors),
                String(producer, "id", "$.producer", errors),
                String(producer, "version", "$.producer", errors),
                String(root, "corpusId", "$", errors),
                String(root, "planLock", "$", errors),
                String(root, "policyVersion", "$", errors),
                ParseEvidenceClass(String(root, "evidenceClass", "$", errors), "$", errors),
                Utc(root, "scoredUtc", "$", errors),
                new AggregatePrivacy(
                    Boolean(privacy, "safeToPublish", "$.privacy", errors),
                    Integer(privacy, "minimumIndependentSplitUnits", "$.privacy", errors),
                    Boolean(privacy, "containsPerSampleResults", "$.privacy", errors)),
                slices), errors);
        }
    }

    /// <summary>
    /// Writes the truth-free plan a producer receives. The bytes are a pure function of the plan:
    /// fixed property order, LF line endings on every platform, and canonical decimals, so the
    /// same private manifest emits byte-identical plans on Linux and Windows and a golden fixture
    /// can pin them.
    /// </summary>
    public static string SerializeRunPlan(RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", CorpusValidation.RunPlanSchemaVersion);
            writer.WriteString("runId", plan.RunId);
            writer.WriteStartObject("producer");
            writer.WriteString("id", plan.ProducerId);
            writer.WriteString("version", plan.ProducerVersion);
            writer.WriteEndObject();
            writer.WriteString("policyVersion", plan.PolicyVersion);
            writer.WriteString("corpusId", plan.CorpusId);
            writer.WriteString("nearDuplicateGraphVersion", plan.NearDuplicateGraphVersion);
            writer.WriteString("planLock", plan.PlanLock);
            writer.WriteStartArray("samples");
            foreach (var sample in plan.Samples)
            {
                writer.WriteStartObject();
                writer.WriteString("sampleId", sample.SampleId);
                writer.WriteString("split", FormatSplit(sample.Split));
                writer.WriteString("intent", FormatIntent(sample.Intent));
                writer.WriteString("evidenceClass", FormatEvidenceClass(sample.EvidenceClass));
                WriteContext(writer, sample.Context);
                WriteLineage(writer, sample.Lineage);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Removes a decimal's trailing zeros. System.Text.Json keeps the scale it parsed, so "1.0"
    /// and "1" would otherwise serialize, and lock, differently for one value.
    /// </summary>
    public static decimal CanonicalDecimal(decimal value) =>
        value == 0m
            ? 0m
            : decimal.Parse(
                value.ToString("0.############################", CultureInfo.InvariantCulture),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture);

    public static string CanonicalDecimalText(decimal value) => CanonicalDecimal(value).ToString(CultureInfo.InvariantCulture);

    public static string SerializeAggregateResults(AggregateResults aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", CorpusValidation.AggregateResultsSchemaVersion);
            writer.WriteString("runId", aggregate.RunId);
            writer.WriteStartObject("producer");
            writer.WriteString("id", aggregate.ProducerId);
            writer.WriteString("version", aggregate.ProducerVersion);
            writer.WriteEndObject();
            writer.WriteString("corpusId", aggregate.CorpusId);
            writer.WriteString("planLock", aggregate.PlanLock);
            writer.WriteString("policyVersion", aggregate.PolicyVersion);
            writer.WriteString("evidenceClass", FormatEvidenceClass(aggregate.EvidenceClass));
            writer.WriteString("scoredUtc", aggregate.ScoredUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartObject("privacy");
            writer.WriteBoolean("safeToPublish", aggregate.Privacy.SafeToPublish);
            writer.WriteNumber("minimumIndependentSplitUnits", aggregate.Privacy.MinimumIndependentSplitUnits);
            writer.WriteBoolean("containsPerSampleResults", aggregate.Privacy.ContainsPerSampleResults);
            writer.WriteEndObject();
            writer.WriteStartArray("slices");
            foreach (var slice in aggregate.Slices)
            {
                WriteSlice(writer, slice);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteContext(Utf8JsonWriter writer, CaptureContext context)
    {
        writer.WriteStartObject("provenance");
        writer.WriteString("schemaVersion", CorpusValidation.ProvenanceSchemaVersion);
        writer.WriteString("captureIntentId", context.CaptureIntentId);
        writer.WriteString("sessionId", context.SessionId);
        writer.WriteString("correlationId", context.CorrelationId);
        writer.WriteNumber("captureOrdinal", context.CaptureOrdinal);
        WriteNullableString(writer, "workspaceId", context.WorkspaceId);
        WriteNullableString(writer, "profileId", context.ProfileId);
        WriteNullableString(writer, "mapId", context.MapId);
        WriteNullableString(writer, "floorId", context.FloorId);
        WriteNullableString(writer, "planId", context.PlanId);
        writer.WriteStartArray("objectiveIds");
        foreach (var objectiveId in context.ObjectiveIds)
        {
            writer.WriteStringValue(objectiveId);
        }

        writer.WriteEndArray();
        WriteNullableString(writer, "selectedReference", context.SelectedReference);
        WriteNullableString(writer, "priorScanReference", context.PriorScanReference);
        writer.WriteString("deviceClass", context.DeviceClass);
        writer.WriteString("surface", context.Surface);
        writer.WriteStartObject("resolution");
        writer.WriteNumber("width", context.Width);
        writer.WriteNumber("height", context.Height);
        writer.WriteEndObject();
        writer.WriteNumber("uiScale", CanonicalDecimal(context.UiScale));
        writer.WriteString("locale", context.Locale);
        writer.WriteString("gameVersion", context.GameVersion);
        writer.WriteString("companionUiVersion", context.CompanionUiVersion);
        writer.WriteEndObject();
    }

    private static void WriteLineage(Utf8JsonWriter writer, SequenceLineage lineage)
    {
        writer.WriteStartObject("lineage");
        writer.WriteString("sequenceId", lineage.SequenceId);
        writer.WriteNumber("frameOrdinal", lineage.FrameOrdinal);
        writer.WriteString("viewportId", lineage.ViewportId);
        writer.WriteString("containerIdentity", lineage.ContainerIdentity);
        writer.WriteNumber("overlapWithPrevious", CanonicalDecimal(lineage.OverlapWithPrevious));
        WriteNullableString(writer, "parentContainerIdentity", lineage.ParentContainerIdentity);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableDecimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, CanonicalDecimal(number));
        }
        else
        {
            writer.WriteNull(name);
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
        Const(element, "schemaVersion", CorpusValidation.ConsentSchemaVersion, path, errors);
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
        Const(element, "schemaVersion", CorpusValidation.PrivacyReviewSchemaVersion, path, errors);
        return new PrivacyReviewEvidence(
            String(element, "reviewId", path, errors),
            String(element, "reviewHash", path, errors),
            String(element, "state", path, errors),
            String(element, "redaction", path, errors),
            Utc(element, "reviewedUtc", path, errors));
    }

    private static CaptureContext ParseContext(JsonElement element, string path, ICollection<string> errors)
    {
        Const(element, "schemaVersion", CorpusValidation.ProvenanceSchemaVersion, path, errors);
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

    private static void WriteSlice(Utf8JsonWriter writer, SliceMetrics slice)
    {
        writer.WriteStartObject();
        writer.WriteString("intent", FormatIntent(slice.Intent));
        writer.WriteString("evidenceClass", FormatEvidenceClass(slice.EvidenceClass));
        writer.WriteString("status", slice.Status);
        writer.WriteNumber("numerator", slice.Numerator);
        writer.WriteNumber("denominator", slice.Denominator);
        writer.WriteNumber("excludedUnknowns", slice.ExcludedUnknowns);
        writer.WriteNumber("excludedPredictionClaims", slice.ExcludedPredictionClaims);
        writer.WriteNumber("independentSplitUnits", slice.IndependentSplitUnits);
        writer.WriteNumber("attemptedKnownClaims", slice.AttemptedKnownClaims);
        writer.WriteNumber("truePositives", slice.TruePositives);
        writer.WriteNumber("falsePositives", slice.FalsePositives);
        writer.WriteNumber("falseNegatives", slice.FalseNegatives);
        writer.WriteNumber("abstentions", slice.Abstentions);
        writer.WriteNumber("confidentWrong", slice.ConfidentWrong);
        writer.WriteNumber("missingFrames", slice.MissingFrames);
        writer.WriteNumber("reorderedFrames", slice.ReorderedFrames);
        writer.WriteNumber("overlapDeduplicationErrors", slice.OverlapDeduplicationErrors);
        writer.WriteNumber("accuracyNumerator", slice.AccuracyNumerator);
        writer.WriteNumber("accuracyDenominator", slice.AccuracyDenominator);
        writer.WriteNumber("recallNumerator", slice.RecallNumerator);
        writer.WriteNumber("recallDenominator", slice.RecallDenominator);
        writer.WriteNumber("falsePositiveNumerator", slice.FalsePositiveNumerator);
        writer.WriteNumber("falsePositiveDenominator", slice.FalsePositiveDenominator);
        writer.WriteNumber("coverage", CanonicalDecimal(slice.Coverage));
        writer.WriteNumber("accuracy", CanonicalDecimal(slice.Accuracy));
        writer.WriteNumber("recall", CanonicalDecimal(slice.Recall));
        writer.WriteNumber("falsePositiveRate", CanonicalDecimal(slice.FalsePositiveRate));
        writer.WriteNumber("f1", CanonicalDecimal(slice.F1));
        writer.WriteNumber("abstentionRate", CanonicalDecimal(slice.AbstentionRate));
        writer.WriteNumber("confidentWrongRate", CanonicalDecimal(slice.ConfidentWrongRate));
        writer.WriteNumber("confidenceIntervalLower", CanonicalDecimal(slice.ConfidenceIntervalLower));
        writer.WriteNumber("confidenceIntervalUpper", CanonicalDecimal(slice.ConfidenceIntervalUpper));
        writer.WriteNumber("performanceSampleCount", slice.PerformanceSampleCount);
        WriteNullableDecimal(writer, "meanElapsedMilliseconds", slice.MeanElapsedMilliseconds);
        WriteNullableDecimal(writer, "maximumElapsedMilliseconds", slice.MaximumElapsedMilliseconds);
        writer.WriteEndObject();
    }

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
        RejectUnsupportedNestedSchemaVersions(document.RootElement, "$", errors);
        RejectDuplicateProperties(document.RootElement, "$", errors);
        errors.AddRange(CorpusValidation.PrivacyErrors(document.RootElement, privateManifest));
        return true;
    }

    /// <summary>
    /// Unknown properties are tolerated so v1 readers survive compatible additions, but a nested
    /// object that declares its own schema version is a versioned sub-document, and one this
    /// reader does not implement cannot be read as if it were v1. The positional checks in the
    /// consent, privacy-review, and provenance parsers only see the places v1 defines; a
    /// "provenance.v2" smuggled under an extension, or a "schemaVersion" on an object v1 never
    /// versioned, would otherwise pass silently. The root's own version is checked by the caller.
    /// </summary>
    private static void RejectUnsupportedNestedSchemaVersions(JsonElement element, string path, ICollection<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                if (path != "$" && property.NameEquals("schemaVersion") &&
                    (property.Value.ValueKind != JsonValueKind.String || !NestedSchemaVersions.Contains(property.Value.GetString()!)))
                {
                    errors.Add($"{propertyPath} names an unsupported nested schema version.");
                }

                RejectUnsupportedNestedSchemaVersions(property.Value, propertyPath, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in element.EnumerateArray())
            {
                RejectUnsupportedNestedSchemaVersions(value, $"{path}[{index}]", errors);
                index++;
            }
        }
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

    private static decimal? NullableDecimal(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            errors.Add($"{path}.{name} is required (number or null).");
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result))
        {
            return result;
        }

        errors.Add($"{path}.{name} must be a finite decimal number or null.");
        return null;
    }

    private static bool Boolean(JsonElement parent, string name, string path, ICollection<string> errors)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors.Add($"{path}.{name} is required and must be a boolean.");
        return false;
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

    internal static string FormatEvidenceClass(CorpusEvidenceClass value) => value switch
    {
        CorpusEvidenceClass.RealRaster => "real-raster",
        CorpusEvidenceClass.SyntheticRaster => "synthetic-raster",
        CorpusEvidenceClass.PostOcrEvidence => "post-ocr-evidence",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static string FormatSplit(CorpusSplit value) => value switch
    {
        CorpusSplit.Train => "train",
        CorpusSplit.Tune => "tune",
        CorpusSplit.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static string FormatIntent(BenchmarkIntent value) => value switch
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

    private static T Invalid<T>(string value, string path, string kind, ICollection<string> errors, T invalid)
    {
        errors.Add($"{path} contains unsupported {kind} '{value}'.");
        return invalid;
    }
}
