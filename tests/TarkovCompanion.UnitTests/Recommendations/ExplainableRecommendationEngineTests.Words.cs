using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.UnitTests.Localization;

namespace TarkovCompanion.UnitTests.Recommendations;

public sealed partial class ExplainableRecommendationEngineTests
{
    /// <summary>
    /// Every engine these tests build is this one, so every result they check is also checked for
    /// its words (#314): each reason, caveat and summary carries a phrase whose English is the
    /// stored sentence byte for byte. <c>policies.CreateEngine()</c> still builds the real engine.
    /// </summary>
    private sealed class ExplainableRecommendationEngine
    {
        private readonly global::TarkovCompanion.Application.Services.Recommendations.ExplainableRecommendationEngine _engine = new();

        public global::TarkovCompanion.Core.Abstractions.V2.RecommendationResult Evaluate(
            ExplainableRecommendationRequest request,
            CancellationToken cancellationToken = default) =>
            AdviceWordsCheck.SaidAsBefore(_engine.Evaluate(request, cancellationToken));
    }
}
