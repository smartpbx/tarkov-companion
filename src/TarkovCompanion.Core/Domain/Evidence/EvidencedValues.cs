using System.Text.Json.Serialization;

namespace TarkovCompanion.Core.Domain.Evidence;

public enum ResultCompleteness
{
    Unknown = 0,
    Unavailable,
    Partial,
    Complete,
}

/// <summary>Freshness is separate because a complete result may still be stale.</summary>
public enum FreshnessState
{
    Unknown = 0,
    Current,
    Stale,
}

public sealed record ResultStatus
{
    public ResultStatus(
        ResultCompleteness completeness,
        FreshnessState freshness,
        string? code = null,
        string? detail = null)
    {
        Completeness = EvidenceGuard.Defined(completeness, nameof(completeness));
        Freshness = EvidenceGuard.Defined(freshness, nameof(freshness));
        Code = EvidenceGuard.TrimOptional(code);
        Detail = EvidenceGuard.TrimOptional(detail);
    }

    public ResultCompleteness Completeness { get; }

    public FreshnessState Freshness { get; }

    public string? Code { get; }

    public string? Detail { get; }
}

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
    User = 1,
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
        OriginClass = EvidenceGuard.Defined(originClass, nameof(originClass));
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
/// <remarks>
/// <see cref="Value"/> is the current value: the last correction's value when corrections exist,
/// otherwise what was recognized. <see cref="RecognizedValue"/> is always the original. Each
/// correction starts from the value the previous one left, so a history cannot be spliced.
/// Only scalar values (strings and value types) are corrected in place; a composite payload is
/// corrected through its own evidenced fields, which keeps the chain comparison exact after a
/// serialization round trip.
/// </remarks>
public sealed record EvidencedValue<T>
{
    private static readonly bool IsScalar = typeof(T) == typeof(string) || typeof(T).IsValueType;

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
        Candidates = EvidenceGuard.ReadOnly(candidates ?? [], nameof(candidates));
        Corrections = EvidenceGuard.ReadOnly(corrections ?? [], nameof(corrections));

        // An undetermined claim must not look like a read zero, false, or name, and a complete
        // one must say what it found. Presence is tested rather than equality with default, so
        // EvidencedValue<int> cannot be Unknown at all: undetermined numbers need int?.
        // Candidates may still describe an ambiguity while the value itself stays absent.
        if (status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable &&
            value is not null)
        {
            throw new ArgumentException($"A {status.Completeness} result cannot carry a value.", nameof(value));
        }

        if (status.Completeness == ResultCompleteness.Complete && value is null)
        {
            throw new ArgumentException("A complete result must carry its value.", nameof(value));
        }

        ValidateCorrections(value, provenance);
    }

    public string FieldId { get; }

    public T? Value { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    public EvidenceRegion? Bounds { get; }

    public IReadOnlyList<EvidenceCandidate<T>> Candidates { get; }

    public IReadOnlyList<EvidenceCorrection<T>> Corrections { get; }

    [JsonIgnore]
    public T? RecognizedValue => Corrections.Count == 0 ? Value : Corrections[0].OriginalValue;

    private void ValidateCorrections(T? value, EvidenceProvenance provenance)
    {
        if (Corrections.Count == 0)
        {
            return;
        }

        if (!IsScalar)
        {
            throw new ArgumentException(
                "Composite values are corrected through their evidenced fields, not replaced whole.",
                "corrections");
        }

        var comparer = EqualityComparer<T?>.Default;
        for (var index = 0; index < Corrections.Count; index++)
        {
            var correction = Corrections[index];
            if (correction.Sequence != index + 1)
            {
                throw new ArgumentException("Corrections must be contiguous and ordered from sequence one.", "corrections");
            }

            var previousUtc = index == 0 ? provenance.ObservedUtc : Corrections[index - 1].CorrectedUtc;
            if (correction.CorrectedUtc < previousUtc)
            {
                throw new ArgumentException("A correction cannot predate the evidence or correction before it.", "corrections");
            }

            if (index > 0 && !comparer.Equals(correction.OriginalValue, Corrections[index - 1].CorrectedValue))
            {
                throw new ArgumentException("Each correction must start from the value the previous one left.", "corrections");
            }
        }

        if (!comparer.Equals(value, Corrections[^1].CorrectedValue))
        {
            throw new ArgumentException("The current value must be the last correction's value.", nameof(value));
        }
    }
}
