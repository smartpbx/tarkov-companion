using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.RecognitionCorpus;

public static class CorpusValidation
{
    private static readonly HashSet<string> ForbiddenInterchangeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "filename", "fileName", "sourceFilename", "sourcePath", "absolutePath", "path", "pixels", "ocrText", "truth", "labels",
    };

    private static readonly HashSet<string> ForbiddenPrivateManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "filename", "fileName", "sourceFilename", "sourcePath", "absolutePath", "path", "pixels", "ocrText", "consentRecord",
    };

    public static IReadOnlyList<string> ValidateManifest(CorpusManifest manifest, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.CorpusId) || string.IsNullOrWhiteSpace(manifest.NearDuplicateGraphVersion))
        {
            errors.Add("A manifest requires a corpus id and versioned near-duplicate graph.");
        }

        if (manifest.Samples is null || manifest.Samples.Count == 0)
        {
            return ["A manifest requires at least one sample."];
        }

        var sampleIds = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var sequenceFrames = new Dictionary<string, List<CorpusSample>>(StringComparer.Ordinal);
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

            if (sample.EvidenceClass is CorpusEvidenceClass.RealRaster or CorpusEvidenceClass.SyntheticRaster)
            {
                if (!Sha256(sample.DecodedPixelSha256) || !hashes.Add(sample.DecodedPixelSha256!))
                {
                    errors.Add("Raster samples require a unique canonical decoded-pixel SHA-256.");
                }
            }
            else if (sample.DecodedPixelSha256 is not null)
            {
                errors.Add("Post-OCR evidence must not claim a pixel hash.");
            }

            if (sample.NearDuplicateHashes.Any(hash => !Sha256(hash)))
            {
                errors.Add($"Sample {sample.SampleId} has a malformed perceptual-near-duplicate hash.");
            }

            if (sample.EvidenceClass == CorpusEvidenceClass.RealRaster)
            {
                ValidateRealEligibility(sample, nowUtc, errors);
            }

            if (!OpaqueId(sample.Context.CaptureIntentId) || !OpaqueId(sample.Context.SessionId) ||
                !OpaqueId(sample.Context.CorrelationId) || sample.Context.CaptureOrdinal < 0 ||
                sample.Context.Width < 1 || sample.Context.Height < 1 || sample.Context.UiScale <= 0 ||
                string.IsNullOrWhiteSpace(sample.Context.Locale) || string.IsNullOrWhiteSpace(sample.Context.GameVersion) ||
                string.IsNullOrWhiteSpace(sample.Context.CompanionUiVersion))
            {
                errors.Add($"Sample {sample.SampleId} has an invalid immutable context snapshot.");
            }

            if (sample.Lineage.FrameOrdinal < 0 || sample.Lineage.OverlapWithPrevious is < 0 or > 1 ||
                !OpaqueId(sample.Lineage.SequenceId) || string.IsNullOrWhiteSpace(sample.Lineage.ViewportId) ||
                string.IsNullOrWhiteSpace(sample.Lineage.ContainerIdentity))
            {
                errors.Add($"Sample {sample.SampleId} has invalid sequence lineage.");
            }

            if (!sequenceFrames.TryGetValue(sample.Lineage.SequenceId, out var frames))
            {
                frames = [];
                sequenceFrames.Add(sample.Lineage.SequenceId, frames);
            }

            frames.Add(sample);
            var truthIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var truth in sample.Truth ?? [])
            {
                if (!truthIds.Add(truth.TruthId) || string.IsNullOrWhiteSpace(truth.Kind) ||
                    (truth.State == TruthState.Known && truth.Value is null) ||
                    (truth.State == TruthState.Unknown && truth.Value is not null))
                {
                    errors.Add($"Sample {sample.SampleId} has invalid truth-with-unknowns.");
                }
            }
        }

        foreach (var (_, frames) in sequenceFrames)
        {
            var ordered = frames.OrderBy(frame => frame.Lineage.FrameOrdinal).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].Lineage.FrameOrdinal != index)
                {
                    errors.Add($"Sequence {ordered[index].Lineage.SequenceId} must have contiguous ordered capture frames.");
                    break;
                }
            }

            // A visible item can persist across overlapping scroll frames. Its truth id remains
            // stable so the scorer counts it once; conflicting repeated truth is a bad manifest,
            // never an excuse to award a duplicate true positive.
            foreach (var repeated in ordered.SelectMany(sample => sample.Truth).GroupBy(truth => truth.TruthId, StringComparer.Ordinal))
            {
                if (repeated.Select(truth => (truth.State, truth.Kind, truth.Value)).Distinct().Skip(1).Any())
                {
                    errors.Add($"Sequence {ordered[0].Lineage.SequenceId} repeats truth {repeated.Key} with conflicting values.");
                }
            }
        }

        foreach (var session in manifest.Samples.Where(sample => sample is not null && sample.Context is not null)
                     .GroupBy(sample => sample.Context.SessionId, StringComparer.Ordinal))
        {
            var ordinals = session.Select(sample => sample.Context.CaptureOrdinal).OrderBy(ordinal => ordinal).ToArray();
            if (ordinals.Where((ordinal, index) => ordinal != index).Any())
            {
                errors.Add($"Capture session {session.Key} must have contiguous capture ordinals from zero.");
            }
        }

        if (errors.All(error => !error.Contains("null", StringComparison.OrdinalIgnoreCase)))
        {
            errors.AddRange(SplitPlanner.ValidateUnits(manifest.Samples));
        }
        return errors;
    }

    /// <summary>Unknown JSON is intentionally tolerated; only prohibited privacy fields are rejected.</summary>
    public static IReadOnlyList<string> ValidateTruthFreeInterchange(string json, string requiredSchemaVersion)
    {
        using var document = JsonDocument.Parse(json);
        var errors = new List<string>();
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) ||
            !string.Equals(version.GetString(), requiredSchemaVersion, StringComparison.Ordinal))
        {
            errors.Add($"Expected {requiredSchemaVersion}.");
        }

        Visit(document.RootElement, errors, ForbiddenInterchangeNames);
        return errors;
    }

    public static IReadOnlyList<string> ValidatePrivateManifestInterchange(string json)
    {
        using var document = JsonDocument.Parse(json);
        var errors = new List<string>();
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) ||
            !string.Equals(version.GetString(), "manifest.v1", StringComparison.Ordinal))
        {
            errors.Add("Expected manifest.v1.");
        }

        Visit(document.RootElement, errors, ForbiddenPrivateManifestNames);
        return errors;
    }

    public static void RejectRepositoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = new FileInfo(Path.GetFullPath(path)).Directory;
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException("Recognition corpus roots and private manifests must be outside every repository or worktree.");
            }

            directory = directory.Parent;
        }
    }

    public static string CanonicalPixelHash(ReadOnlySpan<byte> decodedPixels) =>
        Convert.ToHexStringLower(SHA256.HashData(decodedPixels));

    public static void RequireExpectedPixelHash(string expectedHash, ReadOnlySpan<byte> decodedPixels)
    {
        if (!string.Equals(expectedHash, CanonicalPixelHash(decodedPixels), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decoded pixels changed from the manifest's canonical SHA-256.");
        }
    }

    /// <summary>
    /// The private importer calls this with the private consent record and private human review;
    /// a manifest's hash-shaped summary alone is never enough to make real pixels eligible.
    /// </summary>
    public static IReadOnlyList<string> VerifyRealEligibility(
        CorpusSample sample,
        ConsentEvidence privateConsent,
        PrivacyReviewEvidence privateReview,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(privateConsent);
        ArgumentNullException.ThrowIfNull(privateReview);
        var errors = new List<string>();
        if (sample.EvidenceClass != CorpusEvidenceClass.RealRaster || sample.Consent is null ||
            !string.Equals(sample.Consent.ConsentHash, privateConsent.ConsentHash, StringComparison.Ordinal))
        {
            errors.Add("The sample does not carry a matching private consent-record hash.");
        }

        var reviewed = sample.PrivacyReview;
        if (reviewed is null || reviewed != privateReview)
        {
            errors.Add("The sample does not carry the matching private privacy review.");
        }

        ValidateRealEligibility(sample with { Consent = privateConsent, PrivacyReview = privateReview }, nowUtc, errors);
        return errors;
    }

    private static void ValidateRealEligibility(CorpusSample sample, DateTimeOffset nowUtc, ICollection<string> errors)
    {
        var consent = sample.Consent;
        if (consent is null || !Sha256(consent.ConsentHash) || !consent.AllowedUses.Contains("benchmark", StringComparer.Ordinal) ||
            consent.RetentionExpiresUtc <= nowUtc || !string.Equals(consent.RevocationState, "active", StringComparison.Ordinal))
        {
            errors.Add($"Real sample {sample.SampleId} lacks active benchmark consent and retention.");
        }

        var review = sample.PrivacyReview;
        if (review is null || !string.Equals(review.State, "approved", StringComparison.Ordinal) ||
            review.RedactionState is not ("not-required" or "applied"))
        {
            errors.Add($"Real sample {sample.SampleId} lacks approved privacy/redaction review.");
        }
    }

    private static void Visit(JsonElement element, ICollection<string> errors, IReadOnlySet<string> forbiddenNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (forbiddenNames.Contains(property.Name))
                {
                    errors.Add($"Truth-free interchange cannot contain {property.Name}.");
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
        else if (element.ValueKind == JsonValueKind.String && Path.IsPathFullyQualified(element.GetString() ?? string.Empty))
        {
            errors.Add("Truth-free interchange cannot contain an absolute filesystem path.");
        }
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');

    private static bool OpaqueId(string? value) => value is { Length: >= 16 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
