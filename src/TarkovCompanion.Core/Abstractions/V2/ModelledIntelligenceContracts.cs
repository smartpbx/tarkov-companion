using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum RaidPhase
{
    Early = 1,
    Mid,
    Late,
}

/// <summary>How often a map zone saw traffic in a phase, relative to the map's busiest zone.</summary>
public sealed record ZoneTrafficIntensity(
    string MapId,
    string ZoneId,
    RaidPhase Phase,
    double RelativeIntensity) : IIntelligencePayload
{
    public string MapId { get; } = V2ContractGuard.Required(MapId, nameof(MapId));

    public string ZoneId { get; } = V2ContractGuard.Required(ZoneId, nameof(ZoneId));

    public RaidPhase Phase { get; } = V2ContractGuard.Defined(Phase, nameof(Phase));

    public double RelativeIntensity { get; } = IntelligenceRange.Unit(RelativeIntensity, nameof(RelativeIntensity));
}

/// <summary>Relative route pressure along a named corridor in a phase.</summary>
public sealed record RouteCorridorPressure(
    string MapId,
    string CorridorId,
    RaidPhase Phase,
    double RelativePressure) : IIntelligencePayload
{
    public string MapId { get; } = V2ContractGuard.Required(MapId, nameof(MapId));

    public string CorridorId { get; } = V2ContractGuard.Required(CorridorId, nameof(CorridorId));

    public RaidPhase Phase { get; } = V2ContractGuard.Defined(Phase, nameof(Phase));

    public double RelativePressure { get; } = IntelligenceRange.Unit(RelativePressure, nameof(RelativePressure));
}

/// <summary>The modelled chance of meeting another player in a zone during a phase; not a sighting.</summary>
public sealed record EncounterLikelihood(
    string MapId,
    string ZoneId,
    RaidPhase Phase,
    double Probability) : IIntelligencePayload
{
    public string MapId { get; } = V2ContractGuard.Required(MapId, nameof(MapId));

    public string ZoneId { get; } = V2ContractGuard.Required(ZoneId, nameof(ZoneId));

    public RaidPhase Phase { get; } = V2ContractGuard.Defined(Phase, nameof(Phase));

    public double Probability { get; } = IntelligenceRange.Unit(Probability, nameof(Probability));
}

/// <summary>The only kinds of evidence a historical summary or a model may consume.</summary>
public enum IntelligenceInputKind
{
    StaticMapData = 1,
    PublicStructuredData,
    HistoricalAggregate,
    CuratedKnowledge,
    PrivateLocalFeedback,
}

/// <summary>
/// A named model input. Its kind fixes which source classes may back it, so a screenshot, a
/// paired-device action, or another model's estimate cannot enter as "feedback" or "data".
/// </summary>
public sealed record IntelligenceInputReference
{
    public IntelligenceInputReference(string evidenceId, IntelligenceInputKind kind, EvidenceProvenance provenance)
    {
        EvidenceId = V2ContractGuard.Required(evidenceId, nameof(evidenceId));
        Kind = V2ContractGuard.Defined(kind, nameof(kind));
        Provenance = V2ContractGuard.NotNull(provenance, nameof(provenance));

        var allowed = kind switch
        {
            IntelligenceInputKind.StaticMapData =>
                provenance.SourceClass is EvidenceSourceClass.PublicStructuredData or EvidenceSourceClass.CuratedData,
            IntelligenceInputKind.PublicStructuredData => provenance.SourceClass == EvidenceSourceClass.PublicStructuredData,
            IntelligenceInputKind.HistoricalAggregate => provenance.SourceClass == EvidenceSourceClass.HistoricalAggregate,
            IntelligenceInputKind.CuratedKnowledge => provenance.SourceClass == EvidenceSourceClass.CuratedData,
            IntelligenceInputKind.PrivateLocalFeedback =>
                provenance.SourceClass is EvidenceSourceClass.UserEntered or EvidenceSourceClass.GameWrittenLog,
            _ => false,
        };

        if (!allowed)
        {
            throw new ArgumentException($"{kind} input cannot be backed by {provenance.SourceClass} evidence.", nameof(provenance));
        }
    }

    public string EvidenceId { get; }

    public IntelligenceInputKind Kind { get; }

    public EvidenceProvenance Provenance { get; }
}

/// <summary>A bounded summary of past observations, never a claim about present player state.</summary>
public sealed record HistoricalIntelligence<T>
    where T : class, IIntelligencePayload
{
    public HistoricalIntelligence(
        string intelligenceId,
        EvidencedValue<T> value,
        IReadOnlyList<IntelligenceInputReference> inputs)
    {
        V2WirePayloads.Require(V2WirePayloads.Intelligence, typeof(T), "intelligence");
        IntelligenceId = V2ContractGuard.Required(intelligenceId, nameof(intelligenceId));
        Value = V2ContractGuard.NotNull(value, nameof(value));
        Inputs = IntelligenceProvenance.Validate(value, EvidenceSourceClass.HistoricalAggregate, inputs);
    }

    public string IntelligenceId { get; }

    public EvidencedValue<T> Value { get; }

    public IReadOnlyList<IntelligenceInputReference> Inputs { get; }
}

/// <summary>A versioned estimate from historical/public inputs, never a current observation.</summary>
public sealed record ModelledIntelligence<T>
    where T : class, IIntelligencePayload
{
    public ModelledIntelligence(
        string intelligenceId,
        EvidencedValue<T> estimate,
        IReadOnlyList<IntelligenceInputReference> inputs,
        string explanation)
    {
        V2WirePayloads.Require(V2WirePayloads.Intelligence, typeof(T), "intelligence");
        IntelligenceId = V2ContractGuard.Required(intelligenceId, nameof(intelligenceId));
        Explanation = V2ContractGuard.Required(explanation, nameof(explanation));
        Estimate = V2ContractGuard.NotNull(estimate, nameof(estimate));
        Inputs = IntelligenceProvenance.Validate(estimate, EvidenceSourceClass.ModelledEstimate, inputs);
    }

    public string IntelligenceId { get; }

    public EvidencedValue<T> Estimate { get; }

    public IReadOnlyList<IntelligenceInputReference> Inputs { get; }

    public string Explanation { get; }
}

internal static class IntelligenceRange
{
    public static double Unit(double value, string parameterName) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Expected a value between 0 and 1.");
}

internal static class IntelligenceProvenance
{
    public static IReadOnlyList<IntelligenceInputReference> Validate<T>(
        EvidencedValue<T> value,
        EvidenceSourceClass sourceClass,
        IReadOnlyList<IntelligenceInputReference> inputs)
    {
        // A candidate is serialized with its own provenance, so an estimate could otherwise travel
        // as a candidate labelled with an observational source class.
        if (value.Provenance.SourceClass != sourceClass ||
            value.Candidates.Any(candidate => candidate.Provenance.SourceClass != sourceClass))
        {
            throw new ArgumentException($"The value and its candidates must use {sourceClass} provenance.", nameof(value));
        }

        var copy = V2ContractGuard.List(inputs, nameof(inputs));
        if (copy.Count == 0)
        {
            throw new ArgumentException("Intelligence must identify its inputs.", nameof(inputs));
        }

        // Data-through is the newest input the output represents, so no input may be newer.
        var dataThrough = value.Provenance.DataThroughUtc!.Value;
        if (copy.Any(input => input.Provenance.EvidenceThroughUtc > dataThrough))
        {
            throw new ArgumentException("An input cannot be newer than the output's data-through time.", nameof(inputs));
        }

        return copy;
    }
}
