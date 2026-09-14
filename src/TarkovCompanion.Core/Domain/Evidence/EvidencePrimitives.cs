namespace TarkovCompanion.Core.Domain.Evidence;

/// <summary>Where a claim came from, without implying more authority than the source has.</summary>
public enum EvidenceSourceClass
{
    Unknown,
    UserEntered,
    GameWrittenScreenshot,
    ExternalVisiblePixels,
    GameWrittenLog,
    ScreenshotFilename,
    PublicStructuredData,
    CuratedData,
    HistoricalAggregate,
    ModelledEstimate,
    PairedDeviceAction,
}

/// <summary>What a confidence score means; an absent score is not a score of zero.</summary>
public enum EvidenceConfidenceKind
{
    Unscored,
    Deterministic,
    ProviderScore,
    CalibratedEstimate,
}

public sealed record EvidenceConfidence
{
    public EvidenceConfidence(
        EvidenceConfidenceKind kind,
        double? score = null,
        string? calibrationReference = null)
    {
        if (score is { } value && (double.IsNaN(value) || value is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Confidence must be between 0 and 1.");
        }

        if (kind == EvidenceConfidenceKind.Unscored && score is not null)
        {
            throw new ArgumentException("Unscored evidence cannot carry a numeric score.", nameof(score));
        }

        if (kind != EvidenceConfidenceKind.Unscored && score is null)
        {
            throw new ArgumentException("Scored evidence must carry a numeric score.", nameof(score));
        }

        if (kind == EvidenceConfidenceKind.Deterministic && score != 1)
        {
            throw new ArgumentException("Deterministic evidence has confidence 1.", nameof(score));
        }

        if (kind == EvidenceConfidenceKind.CalibratedEstimate &&
            string.IsNullOrWhiteSpace(calibrationReference))
        {
            throw new ArgumentException(
                "Calibrated confidence must name its calibration reference.",
                nameof(calibrationReference));
        }

        Kind = kind;
        Score = score;
        CalibrationReference = EvidenceGuard.TrimOptional(calibrationReference);
    }

    public EvidenceConfidenceKind Kind { get; }

    public double? Score { get; }

    public string? CalibrationReference { get; }

    public static EvidenceConfidence Unscored { get; } = new(EvidenceConfidenceKind.Unscored);

    public static EvidenceConfidence Certain { get; } = new(EvidenceConfidenceKind.Deterministic, 1);
}

/// <summary>How much of the relevant population or surface an observation represents.</summary>
public sealed record EvidenceCoverage
{
    public EvidenceCoverage(long? sampleSize = null, double? fraction = null, string? description = null)
    {
        if (sampleSize is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), "Sample size cannot be negative.");
        }

        if (fraction is { } value && (double.IsNaN(value) || value is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "Coverage fraction must be between 0 and 1.");
        }

        if (sampleSize is null && fraction is null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Coverage must provide a sample size, fraction, or description.");
        }

        SampleSize = sampleSize;
        Fraction = fraction;
        Description = EvidenceGuard.TrimOptional(description);
    }

    public long? SampleSize { get; }

    public double? Fraction { get; }

    public string? Description { get; }
}

public sealed record ProducerIdentity
{
    public ProducerIdentity(string name, string version, string? modelVersion = null)
    {
        Name = EvidenceGuard.Required(name, nameof(name));
        Version = EvidenceGuard.Required(version, nameof(version));
        ModelVersion = EvidenceGuard.TrimOptional(modelVersion);
    }

    public string Name { get; }

    public string Version { get; }

    public string? ModelVersion { get; }
}

/// <summary>The mandatory audit trail for an observed, curated, historical, or generated claim.</summary>
public sealed record EvidenceProvenance
{
    public EvidenceProvenance(
        EvidenceSourceClass sourceClass,
        string sourceIdentifier,
        DateTimeOffset observedUtc,
        EvidenceConfidence confidence,
        ProducerIdentity producer,
        DateTimeOffset? dataThroughUtc = null,
        DateTimeOffset? generatedUtc = null,
        EvidenceCoverage? coverage = null,
        string? reference = null)
    {
        ArgumentNullException.ThrowIfNull(confidence);
        ArgumentNullException.ThrowIfNull(producer);

        SourceClass = sourceClass;
        SourceIdentifier = EvidenceGuard.Required(sourceIdentifier, nameof(sourceIdentifier));
        ObservedUtc = EvidenceGuard.Utc(observedUtc, nameof(observedUtc));
        DataThroughUtc = EvidenceGuard.UtcOptional(dataThroughUtc, nameof(dataThroughUtc));
        GeneratedUtc = EvidenceGuard.UtcOptional(generatedUtc, nameof(generatedUtc));
        Confidence = confidence;
        Producer = producer;
        Coverage = coverage;
        Reference = EvidenceGuard.TrimOptional(reference);

        if (DataThroughUtc > ObservedUtc)
        {
            throw new ArgumentException("Data-through time cannot be later than observation time.", nameof(dataThroughUtc));
        }

        if (GeneratedUtc > ObservedUtc)
        {
            throw new ArgumentException("Generation time cannot be later than observation time.", nameof(generatedUtc));
        }

        if (DataThroughUtc is { } dataThrough && GeneratedUtc is { } generated && dataThrough > generated)
        {
            throw new ArgumentException("Data-through time cannot be later than generation time.", nameof(dataThroughUtc));
        }

        if (sourceClass is EvidenceSourceClass.HistoricalAggregate or EvidenceSourceClass.ModelledEstimate)
        {
            ValidateIntelligenceProvenance();
        }
    }

    public EvidenceSourceClass SourceClass { get; }

    public string SourceIdentifier { get; }

    public DateTimeOffset ObservedUtc { get; }

    public DateTimeOffset? DataThroughUtc { get; }

    public DateTimeOffset? GeneratedUtc { get; }

    public EvidenceConfidence Confidence { get; }

    public EvidenceCoverage? Coverage { get; }

    public ProducerIdentity Producer { get; }

    public string? Reference { get; }

    private void ValidateIntelligenceProvenance()
    {
        if (DataThroughUtc is null || GeneratedUtc is null || Coverage is null)
        {
            throw new ArgumentException(
                "Historical and modelled intelligence requires data-through time, generation time, and coverage.");
        }

        if (Confidence.Kind == EvidenceConfidenceKind.Unscored)
        {
            throw new ArgumentException("Historical and modelled intelligence requires scored confidence.");
        }

        if (string.IsNullOrWhiteSpace(Producer.ModelVersion))
        {
            throw new ArgumentException("Historical and modelled intelligence requires a model version.");
        }
    }
}

public enum EvidenceCoordinateSpace
{
    SourcePixels,
    CaptureRegionPixels,
    GridCellPixels,
}

public sealed record EvidenceRegion
{
    public EvidenceRegion(int x, int y, int width, int height, EvidenceCoordinateSpace coordinateSpace)
    {
        if (x < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
        CoordinateSpace = coordinateSpace;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public EvidenceCoordinateSpace CoordinateSpace { get; }
}

internal static class EvidenceGuard
{
    public static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static string? TrimOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }

        return value.ToUniversalTime();
    }

    public static DateTimeOffset? UtcOptional(DateTimeOffset? value, string parameterName) =>
        value is null ? null : Utc(value.Value, parameterName);
}
