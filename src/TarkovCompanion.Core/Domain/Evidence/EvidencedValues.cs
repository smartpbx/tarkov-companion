namespace TarkovCompanion.Core.Domain.Evidence;

public enum ResultCompleteness
{
    Unknown,
    Unavailable,
    Partial,
    Complete,
}

/// <summary>Freshness is separate because a complete result may still be stale.</summary>
public enum FreshnessState
{
    Unknown,
    Current,
    Stale,
}

public sealed record ResultStatus(
    ResultCompleteness Completeness,
    FreshnessState Freshness,
    string? Code = null,
    string? Detail = null);

public sealed record EvidenceCandidate<T>
{
    public EvidenceCandidate(
        string candidateId,
        string displayName,
        T value,
        EvidenceProvenance provenance,
        EvidenceRegion? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(provenance);

        CandidateId = EvidenceGuard.Required(candidateId, nameof(candidateId));
        DisplayName = EvidenceGuard.Required(displayName, nameof(displayName));
        Value = value;
        Provenance = provenance;
        Bounds = bounds;
    }

    public string CandidateId { get; }

    public string DisplayName { get; }

    public T Value { get; }

    public EvidenceProvenance Provenance { get; }

    public EvidenceRegion? Bounds { get; }
}

public enum CorrectionOriginClass
{
    User,
    DesktopApplication,
    PairedDevice,
    ReviewedImport,
}

/// <summary>A correction augments the original evidence; it never rewrites or discards it.</summary>
public sealed record EvidenceCorrection<T>
{
    public EvidenceCorrection(
        long sequence,
        T? originalValue,
        T correctedValue,
        DateTimeOffset correctedUtc,
        CorrectionOriginClass originClass,
        string originIdentifier,
        string? reason = null)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Correction sequence starts at one.");
        }

        ArgumentNullException.ThrowIfNull(correctedValue);
        Sequence = sequence;
        OriginalValue = originalValue;
        CorrectedValue = correctedValue;
        CorrectedUtc = EvidenceGuard.Utc(correctedUtc, nameof(correctedUtc));
        OriginClass = originClass;
        OriginIdentifier = EvidenceGuard.Required(originIdentifier, nameof(originIdentifier));
        Reason = EvidenceGuard.TrimOptional(reason);
    }

    public long Sequence { get; }

    public T? OriginalValue { get; }

    public T CorrectedValue { get; }

    public DateTimeOffset CorrectedUtc { get; }

    public CorrectionOriginClass OriginClass { get; }

    public string OriginIdentifier { get; }

    public string? Reason { get; }
}

/// <summary>A value plus everything needed to inspect, revise, and honestly display the claim.</summary>
public sealed record EvidencedValue<T>
{
    public EvidencedValue(
        string fieldId,
        T? value,
        ResultStatus status,
        EvidenceProvenance provenance,
        EvidenceRegion? bounds = null,
        IReadOnlyList<EvidenceCandidate<T>>? candidates = null,
        IReadOnlyList<EvidenceCorrection<T>>? corrections = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(provenance);

        FieldId = EvidenceGuard.Required(fieldId, nameof(fieldId));
        Value = value;
        Status = status;
        Provenance = provenance;
        Bounds = bounds;
        Candidates = candidates?.ToArray() ?? [];
        Corrections = corrections?.ToArray() ?? [];

        for (var index = 0; index < Corrections.Count; index++)
        {
            if (Corrections[index].Sequence != index + 1)
            {
                throw new ArgumentException("Corrections must be contiguous and ordered from sequence one.", nameof(corrections));
            }
        }
    }

    public string FieldId { get; }

    public T? Value { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    public EvidenceRegion? Bounds { get; }

    public IReadOnlyList<EvidenceCandidate<T>> Candidates { get; }

    public IReadOnlyList<EvidenceCorrection<T>> Corrections { get; }
}
