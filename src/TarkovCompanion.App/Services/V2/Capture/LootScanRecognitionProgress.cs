using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>The pixel-free, correlated stream produced while one loot grid is being matched.</summary>
/// <remarks>
/// Matching cells is deliberately parallel, so item events are completion-ordered. The final
/// result owns presentation order. Session, artifact, correlation and decode revision travel on
/// every event so a cancelled scan or an older scan finishing late cannot add rows to its
/// replacement.
/// </remarks>
public interface ILootScanRecognitionProgressSource
{
    event EventHandler<LootScanRecognitionStarted>? LootRecognitionStarted;

    event EventHandler<LootScanItemMatched>? LootItemMatched;

    event EventHandler<LootScanRecognitionStopped>? LootRecognitionStopped;
}

public sealed record LootScanRecognitionStarted(
    CaptureSessionId SessionId,
    string ArtifactId,
    CaptureCorrelationId CorrelationId,
    int DecodeRevision,
    CaptureContextMetadata Context,
    string ContentSha256,
    DateTimeOffset StartedUtc);

public sealed record LootScanItemMatched(
    CaptureSessionId SessionId,
    string ArtifactId,
    CaptureCorrelationId CorrelationId,
    int DecodeRevision,
    GridCellObservation Cell);

public sealed record LootScanRecognitionStopped(
    CaptureSessionId SessionId,
    string ArtifactId,
    CaptureCorrelationId CorrelationId,
    int DecodeRevision,
    bool WasCancelled);
