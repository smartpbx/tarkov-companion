namespace TarkovCompanion.EftSimulator;

public sealed record SimulatorScenario(
    string Id,
    string Heading,
    string Detail,
    string Accent,
    string LogEvent,
    string ScreenshotFilename);

public static class SimulatorScenarioCatalog
{
    public static IReadOnlyList<SimulatorScenario> All { get; } =
    [
        new(
            "RaidStart_Customs",
            "Synthetic harbor approach",
            "Raid start · generic map telemetry",
            "#56B8C6",
            "raid-start map=synthetic-harbor mode=development",
            "2026-01-15[20-01]_2.5, 3.0, -18.0_0, 0, 0, 1_60 (0).png"),
        new(
            "Inspect_GraphicsCard",
            "Synthetic circuit board",
            "Single-item inspection · electronics",
            "#C6A15B",
            "scan context=single-item item=synthetic-circuit-board",
            "2026-01-15[20-02]_4.0, 3.0, -16.0_0, 0.130526, 0, 0.991445_120 (1).png"),
        new(
            "Inspect_AmmoPack",
            "Training cartridge carton",
            "Single-item inspection · inert simulator prop",
            "#D18B72",
            "scan context=single-item item=training-cartridge-carton",
            "2026-01-15[20-03]_6.0, 3.0, -14.0_0, 0.258819, 0, 0.965926_180 (2).png"),
        new(
            "Inspect_Key",
            "Numbered brass token",
            "Single-item inspection · generic access token",
            "#C6A15B",
            "scan context=single-item item=numbered-brass-token",
            "2026-01-15[20-04]_8.0, 3.0, -12.0_0, 0.382683, 0, 0.92388_240 (3).png"),
        new(
            "Inspect_Consumable",
            "Sealed ration pouch",
            "Single-item inspection · synthetic provision",
            "#77B895",
            "scan context=single-item item=sealed-ration-pouch",
            "2026-01-15[20-05]_10.0, 3.0, -10.0_0, 0.5, 0, 0.866025_300 (4).png"),
        new(
            "ExtractList_Customs",
            "Available departure gates",
            "Harbor Gate · Rail Yard · synthetic names",
            "#56B8C6",
            "scan context=extract-list extracts=harbor-gate,rail-yard",
            "2026-01-15[20-06]_12.0, 3.0, -8.0_0, 0.608761, 0, 0.793353_360 (5).png"),
        new(
            "Container_Mixed",
            "Mixed supply crate",
            "Bolts · wire · medkit · generic shapes",
            "#77B895",
            "scan context=container items=synthetic-bolts,synthetic-wire,synthetic-medkit",
            "2026-01-15[20-07]_14.0, 3.0, -6.0_0, 0.707107, 0, 0.707107_420 (6).png"),
        new(
            "Flea_VisibleListings",
            "Visible market rows",
            "Informational fixture prices only · no transactions",
            "#C6A15B",
            "scan context=visible-listings rows=3 informational=true",
            "2026-01-15[20-08]_16.0, 3.0, -4.0_0, 0.793353, 0, 0.608761_480 (7).png"),
        new(
            "PositionUpdate",
            "Last known position",
            "Fixture coordinates · timestamped evidence",
            "#56B8C6",
            "position map=synthetic-harbor x=18 y=3 z=-2 source=screenshot-filename",
            "2026-01-15[20-09]_18.0, 3.0, -2.0_0, 0.866025, 0, 0.5_540 (8).png"),
        new(
            "RaidEnd",
            "Synthetic raid complete",
            "Outcome: extracted · no live-game observation",
            "#77B895",
            "raid-end outcome=extracted mode=development",
            "2026-01-15[20-31]_20.0, 3.0, 0.0_0, 0.92388, 0, 0.382683_1860 (9).png"),
    ];

    public static bool TryGet(string id, out SimulatorScenario? scenario)
    {
        scenario = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return scenario is not null;
    }

    public static SimulatorScenario Get(string id) =>
        TryGet(id, out var scenario)
            ? scenario!
            : throw new ArgumentException(
                $"Unknown simulator scenario '{id}'. Valid scenarios: {string.Join(", ", All.Select(item => item.Id))}.",
                nameof(id));
}
