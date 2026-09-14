using System.Collections.Frozen;

namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>Marks a recognition payload. Implementing it outside the allowlist does not make a type sendable.</summary>
public interface IRecognitionPayload;

/// <summary>Marks a historical or modelled intelligence payload.</summary>
public interface IIntelligencePayload;

/// <summary>Marks a revisioned workspace-state payload.</summary>
public interface IWorkspaceStatePayload;

/// <summary>
/// The closed set of payloads the generic v2 envelopes accept. The marker interfaces are public,
/// so a type elsewhere could implement one; the exact-type allowlist is what keeps an enemy
/// position or a control command from riding an envelope. Adding a payload is a contract change:
/// a new sealed type here, a minor version, and a review. The sets are frozen, so a caller cannot
/// cast one back to a mutable collection and add a type.
/// </summary>
public static class V2WirePayloads
{
    public static FrozenSet<Type> Recognition { get; } = new[]
    {
        typeof(RecognizedItem),
        typeof(GridRecognition),
        typeof(LootRecognition),
        typeof(StashRecognition),
        typeof(AmmoRecognition),
        typeof(KeyRecognition),
        typeof(QuestItemRecognition),
        typeof(FleaPageRecognition),
        typeof(ExtractMapRecognition),
        typeof(HealthCharacterRecognition),
        typeof(UnresolvedContextRecognition),
    }.ToFrozenSet();

    public static FrozenSet<Type> Intelligence { get; } = new[]
    {
        typeof(ZoneTrafficIntensity),
        typeof(RouteCorridorPressure),
        typeof(EncounterLikelihood),
    }.ToFrozenSet();

    public static FrozenSet<Type> WorkspaceState { get; } = new[]
    {
        typeof(CaptureIntentState),
        typeof(MapMarkState),
    }.ToFrozenSet();

    internal static void Require(FrozenSet<Type> allowed, Type payload, string kind)
    {
        if (!allowed.Contains(payload))
        {
            throw new ArgumentException($"{payload.Name} is not an allowlisted v2 {kind} payload.");
        }
    }
}
