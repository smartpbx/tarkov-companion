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
        "pageHeading", "stateBanner", "fieldError", "statusBadge", "evidenceSummary",
        "emptyState", "card", "toolbar", "dialogHost", "liveRegion", "semanticTable",
        "chartLegend", "mapLegend", "touchTarget", "whyDisclosure", "pairedDevice", "captureQueue",
    ];

    /// <summary>
    /// Primitive id to the automation id of its static example in V2PrimitiveGallery.axaml. An
    /// example shows one synthetic state; it is not evidence that every state renders.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GalleryExamples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pageHeading"] = "v2-page-heading",
        ["stateBanner"] = "v2-state-banner",
        ["fieldError"] = "v2-field-error",
        ["statusBadge"] = "v2-status-badge",
        ["evidenceSummary"] = "v2-evidence-summary",
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
        ["captureQueue"] = "v2-capture-queue",
    };

    /// <summary>
    /// Primitives with a contract but no native implementation yet, and the issue that owns them.
    /// A dialog host needs the V2 shell's top-level window to place it and restore focus.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Deferred = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dialogHost"] = "#267",
    };

    public static readonly IReadOnlyList<string> CaptureStages =
    ["armed", "queued", "processing", "review", "corrected", "failed"];

    public static readonly IReadOnlyList<string> DeviceStates =
    ["follow", "control", "independent", "leasePending", "offline", "reconnecting", "acknowledgementLag", "conflict"];
}
