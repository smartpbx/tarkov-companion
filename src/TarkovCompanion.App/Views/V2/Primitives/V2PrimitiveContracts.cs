namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Platform-neutral primitive identities, mirrored by the semantic JSON manifest. They are data
/// only: feature workflows own their commands and never obtain any game-facing capability here.
/// </summary>
/// <remarks>
/// The first version listed every primitive as supplied, including a dialog host that did not
/// exist. Each identity is now either demonstrated in the native gallery, under an automation id
/// the contract test looks for, or deferred with the issue that owns it.
/// </remarks>
public static class V2PrimitiveContracts
{
    public static readonly IReadOnlyList<string> Required =
    [
        "pageHeading", "stateBanner", "fieldError", "statusBadge", "evidenceSummary", "credibilityChip",
        "emptyState", "card", "toolbar", "dialogHost", "liveRegion", "semanticTable", "chartLegend",
        "mapLegend", "touchTarget", "whyDisclosure", "pairedDevice", "sharingState", "recoveryAction",
        "captureQueue", "captureProgress", "correctionAction", "initiatingContextLink",
    ];

    /// <summary>
    /// Primitive id to the automation id of its static example in V2PrimitiveGallery.axaml. An
    /// example shows one synthetic state; it is not evidence that every state renders.
    /// </summary>
    /// <remarks>
    /// Each id sits on an element in the UIA control view that has an accessible name. Half of them
    /// once sat on bare panels, which Avalonia keeps out of the control view, so a screen reader or
    /// a UIA tree dump could never have found them.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> GalleryExamples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pageHeading"] = "v2-page-heading",
        ["stateBanner"] = "v2-state-banner",
        ["fieldError"] = "v2-field-error-input",
        ["statusBadge"] = "v2-status-badge",
        ["evidenceSummary"] = "v2-evidence-summary",
        ["credibilityChip"] = "v2-credibility-historical",
        ["emptyState"] = "v2-empty-state",
        ["card"] = "v2-empty-state",
        ["toolbar"] = "v2-toolbar",
        ["liveRegion"] = "v2-live-polite",
        ["semanticTable"] = "v2-semantic-table",
        ["chartLegend"] = "v2-chart-legend",
        ["mapLegend"] = "v2-map-legend",
        ["touchTarget"] = "v2-touch-target",
        ["whyDisclosure"] = "v2-why-disclosure",
        ["pairedDevice"] = "v2-paired-device",
        ["sharingState"] = "v2-sharing-state",
        ["recoveryAction"] = "v2-device-reconnect",
        ["captureQueue"] = "v2-capture-queue",
        ["captureProgress"] = "v2-capture-progress",
        ["correctionAction"] = "v2-capture-correct",
        ["initiatingContextLink"] = "v2-capture-context",
    };

    /// <summary>
    /// Primitives with a contract but no native implementation yet, and the issue that owns them.
    /// A dialog host needs the V2 shell's top-level window to place it and restore focus.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Deferred = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dialogHost"] = "#267",
    };

    /// <summary>
    /// Compatibility indexes from manifest 1.0. CaptureStages includes the historical
    /// <c>corrected</c> id; new adapters use CaptureProgressStages and CaptureOutcomes so they do
    /// not treat a correction as a pipeline phase.
    /// </summary>
    public static readonly IReadOnlyList<string> CaptureStages =
    ["armed", "queued", "processing", "review", "corrected", "failed"];

    /// <summary>
    /// Compatibility index from manifest 1.0. New adapters compose the mode, control,
    /// connectivity, acknowledgement, and sharing lists below.
    /// </summary>
    public static readonly IReadOnlyList<string> DeviceStates =
    ["follow", "control", "independent", "leasePending", "offline", "reconnecting", "acknowledgementLag", "conflict"];

    /// <summary>
    /// The adapter-facing identities below split independent facts instead of treating every
    /// combination as a new state. In particular, corrected is a capture outcome and not a phase,
    /// while connectivity and acknowledgement do not change a paired device's interaction mode.
    /// </summary>
    public static readonly IReadOnlyList<string> EvidenceClasses =
    ["observed", "inferred", "manual", "estimated", "historical", "modelled", "potential"];

    public static readonly IReadOnlyList<string> CaptureProgressStages =
    ["armed", "queued", "processing", "review", "complete", "cancelled", "failed"];

    public static readonly IReadOnlyList<string> CaptureOutcomes = ["corrected"];

    public static readonly IReadOnlyList<string> CaptureIntents =
    ["auto", "loot", "stash", "ammo", "keys", "questItems", "extractsAndMap", "healthAndCharacter", "flea"];

    public static readonly IReadOnlyList<string> CaptureActions =
    ["correct", "retry", "openInitiatingContext"];

    public static readonly IReadOnlyList<string> PairedUnavailableCaptureIntents = ["flea"];

    public static readonly IReadOnlyList<string> DeviceModes =
    ["follow", "controlPending", "control", "independent"];

    public static readonly IReadOnlyList<string> DeviceControlStates =
    ["desktop", "pendingApproval", "pairedDeviceLease"];

    public static readonly IReadOnlyList<string> DeviceConnectivityStates =
    ["connected", "offline", "reconnecting"];

    public static readonly IReadOnlyList<string> DeviceAcknowledgementStates =
    ["current", "lagging", "conflict"];

    public static readonly IReadOnlyList<string> SharingScopes =
    ["private", "pairedDevices", "team"];

    public static readonly IReadOnlyList<string> RecoveryActions =
    ["reconnect", "retry", "sync", "resolveConflict"];
}
