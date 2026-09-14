namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Platform-neutral primitive identities, mirrored by the semantic JSON manifest. They are data
/// only: feature workflows own their commands and never obtain any game-facing capability here.
/// </summary>
public static class V2PrimitiveContracts
{
    public static readonly IReadOnlyList<string> Required =
    [
        "pageHeading", "stateBanner", "fieldError", "statusBadge", "evidenceSummary",
        "emptyState", "card", "toolbar", "dialogHost", "liveRegion", "semanticTable",
        "chartLegend", "mapLegend", "touchTarget", "whyDisclosure", "pairedDevice", "captureQueue",
    ];

    public static readonly IReadOnlyList<string> CaptureStages =
    ["armed", "queued", "processing", "review", "corrected", "failed"];

    public static readonly IReadOnlyList<string> DeviceStates =
    ["follow", "control", "independent", "leasePending", "offline", "reconnecting", "acknowledgementLag", "conflict"];
}
