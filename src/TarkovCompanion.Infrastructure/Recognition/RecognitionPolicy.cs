using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Infrastructure.Recognition;

public enum RecognitionDecision
{
    NoMatch,
    Candidate,
    Ambiguous,
    AutoSelected,
}

public static class RecognitionPolicy
{
    public const double AutoSelectThreshold = 0.90;

    public const double AmbiguityThreshold = 0.70;

    public const double CandidateFloor = 0.45;

    public static RecognitionDecision Classify(Confidence confidence) => confidence.Value switch
    {
        >= AutoSelectThreshold => RecognitionDecision.AutoSelected,
        >= AmbiguityThreshold => RecognitionDecision.Ambiguous,
        >= CandidateFloor => RecognitionDecision.Candidate,
        _ => RecognitionDecision.NoMatch,
    };
}
