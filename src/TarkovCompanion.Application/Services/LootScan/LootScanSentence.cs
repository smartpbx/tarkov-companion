using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Every sentence the loot-scan planner gives for a cell or the whole scan (#314). The App says
/// each through its string table; the English there is the planner's old sentence word for word,
/// which the planner still writes beside the code so a stored scan reads the same in any language.
/// </summary>
[PhraseCodes("Intel.Advice.LootScan")]
public enum LootScanSentence
{
    CaptureChanged = 1,
    LootCoveragePartial,
    CarriedCoveragePartial,
    CapacityUnavailable,
    ItemUnresolved,
    EarlierRevision,
    IdentityBeforeCapacity,
    RecommendationMissing,
    BindingMismatch,
    RulesetMismatch,
    Incomplete,
    Expired,
    EconomicsIncomplete,
    EconomicsMismatch,
    CarriedIncomplete,
    VisibleFit,
    CarriedUnread,
    SearchBudget,
    SwapEvidenceIncomplete,
    NoSupportedFit,
    SwapIncomingUnknown,
    SwapCostExceeds,
    BoundedSwap,
    InputsBindingMismatch,
    InputTooLarge,
    OutputBindingMismatch,
    ValidationBudget,
    EvaluationInvalid,
    ContractVersion,
    IncompleteAmbiguous,
    MetadataTooLarge,
    ReasonMissing,
    PrecedenceUndefined,
    PrecedencePriority,
    PrecedenceOrder,
    ActionMismatch,
    OpportunityCostIncomplete,
    Valid,
    EvidenceExpired,
    EvidenceUnreliable,
}
