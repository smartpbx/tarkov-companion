using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public sealed record IntelligenceInputReference(
    string EvidenceId,
    EvidenceProvenance Provenance)
{
    public EvidenceProvenance Provenance { get; } =
        Provenance ?? throw new ArgumentNullException(nameof(Provenance));

    public string EvidenceId { get; } = V2ContractGuard.Required(EvidenceId, nameof(EvidenceId));
}

/// <summary>A bounded summary of past observations, never a claim about present player state.</summary>
public sealed record HistoricalIntelligence<T>
{
    public HistoricalIntelligence(
        string intelligenceId,
        EvidencedValue<T> value,
        IReadOnlyList<IntelligenceInputReference> inputs)
    {
        IntelligenceId = Required(intelligenceId, nameof(intelligenceId));
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!IntelligenceProvenance.UsesOnly(value, EvidenceSourceClass.HistoricalAggregate))
        {
            throw new ArgumentException(
                "Historical intelligence and its candidates must use historical-aggregate provenance.",
                nameof(value));
        }

        if (inputs.Count == 0)
        {
            throw new ArgumentException("Historical intelligence must identify its inputs.", nameof(inputs));
        }

        Value = value;
        Inputs = V2ContractGuard.List(inputs, nameof(inputs));
    }

    public string IntelligenceId { get; }

    public EvidencedValue<T> Value { get; }

    public IReadOnlyList<IntelligenceInputReference> Inputs { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

/// <summary>A versioned estimate from historical/public inputs, never a current observation.</summary>
public sealed record ModelledIntelligence<T>
{
    public ModelledIntelligence(
        string intelligenceId,
        EvidencedValue<T> estimate,
        IReadOnlyList<IntelligenceInputReference> inputs,
        string explanation)
    {
        IntelligenceId = Required(intelligenceId, nameof(intelligenceId));
        Explanation = Required(explanation, nameof(explanation));
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!IntelligenceProvenance.UsesOnly(estimate, EvidenceSourceClass.ModelledEstimate))
        {
            throw new ArgumentException(
                "Modelled intelligence and its candidates must use modelled-estimate provenance.",
                nameof(estimate));
        }

        if (inputs.Count == 0)
        {
            throw new ArgumentException("Modelled intelligence must identify its inputs.", nameof(inputs));
        }

        Estimate = estimate;
        Inputs = V2ContractGuard.List(inputs, nameof(inputs));
    }

    public string IntelligenceId { get; }

    public EvidencedValue<T> Estimate { get; }

    public IReadOnlyList<IntelligenceInputReference> Inputs { get; }

    public string Explanation { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

internal static class IntelligenceProvenance
{
    // A candidate is serialized with its own provenance, so an estimate could otherwise travel
    // as a candidate labelled with an observational source class.
    public static bool UsesOnly<T>(EvidencedValue<T> value, EvidenceSourceClass sourceClass) =>
        value.Provenance.SourceClass == sourceClass &&
        value.Candidates.All(candidate => candidate.Provenance.SourceClass == sourceClass);
}
