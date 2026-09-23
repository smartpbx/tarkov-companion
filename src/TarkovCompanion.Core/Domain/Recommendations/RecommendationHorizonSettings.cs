namespace TarkovCompanion.Core.Domain.Recommendations;

/// <summary>How many prerequisite steps beyond the current one contribute to item advice.</summary>
public enum RecommendationHorizon
{
    NextOnly = 1,
    NextThree,
    NextFive,
    All,
}

/// <summary>The player's independent quest and hideout look-ahead choices.</summary>
public sealed record RecommendationHorizonSettings(
    RecommendationHorizon Quest,
    RecommendationHorizon Hideout)
{
    public const int SchemaVersion = 1;

    public static RecommendationHorizonSettings Default { get; } = new(
        RecommendationHorizon.NextFive,
        RecommendationHorizon.NextThree);

    public RecommendationHorizonSettings Normalized() => new(
        Defined(Quest, Default.Quest),
        Defined(Hideout, Default.Hideout));

    public static int Steps(RecommendationHorizon horizon) => horizon switch
    {
        RecommendationHorizon.NextOnly => 1,
        RecommendationHorizon.NextThree => 3,
        RecommendationHorizon.NextFive => 5,
        RecommendationHorizon.All => int.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(horizon)),
    };

    private static RecommendationHorizon Defined(
        RecommendationHorizon value,
        RecommendationHorizon fallback) => Enum.IsDefined(value) ? value : fallback;
}
