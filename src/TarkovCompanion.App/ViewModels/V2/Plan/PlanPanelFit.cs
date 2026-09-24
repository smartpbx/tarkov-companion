using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>How Plan › Quests lays out its objectives panel, given what the map placement chose.</summary>
public enum PlanPanelArrangement
{
    /// <summary>Down the side of the map, pieces stacked in one column.</summary>
    Beside = 0,

    /// <summary>Under the map, Requirements and Open in Raid in a column beside the objectives.</summary>
    BelowSideBySide,

    /// <summary>Under the map, pieces stacked, because the panel is too narrow for two columns.</summary>
    BelowStacked,

    /// <summary>The map preview is put away and the panel takes its place.</summary>
    PanelOnly,
}

/// <summary>
/// [#828] What Plan › Quests does with its objectives panel when the shell is narrow.
/// </summary>
/// <remarks>
/// At 200% interface scale a 1920x1080 window is a 960x540 shell. Under the map, the panel was
/// about 530 wide, the Requirements column kept its fixed 380, and the objectives were left a
/// column about 130 wide (#828 measured ~170) with every row clipped. At 150% the same fixed 380
/// clipped the right-hand end of each 404-wide objective row. So the side column now takes only
/// what the objectives leave it, and when that is too little to hold a requirement the pieces
/// stack. Stacked they need height, and a 540-tall shell has none to spare: there the map
/// preview, which would have been a strip about 110 tall under its own presentation bar, is put
/// away and Open in Raid is where the map is. At 100% on 1920x1080 none of this moves anything:
/// the panel is about 1410 wide and the side column keeps its full 380.
/// </remarks>
public static class PlanPanelFit
{
    /// <summary>What the objectives card needs beside the side column: one 404-wide row and its insets.</summary>
    /// <remarks>Card margin 16, card padding 12 + 8, the list's own 8, and the row's 404.</remarks>
    public const double ObjectivesMinimumWidth = 448;

    /// <summary>The side column's own left and right margins.</summary>
    public const double SidePieceMargins = 32;

    /// <summary>The side column's width when there is room for it, as the 1920x1080 layout was designed.</summary>
    public const double SidePieceFullWidth = 380;

    /// <summary>The narrowest side column that still holds a requirement's name and its count.</summary>
    public const double SidePieceMinimumWidth = 240;

    /// <summary>The height the stacked panel needs: objectives heading and two rows, Requirements, Open in Raid.</summary>
    public const double StackedPanelMinimumHeight = 480;

    /// <summary>The least map worth drawing above a stacked panel; its presentation bar alone is about 70.</summary>
    public const double MapMinimumHeight = 240;

    /// <summary>The narrowest panel that can hold the objectives and the side column side by side.</summary>
    public const double SideBySideMinimumWidth = ObjectivesMinimumWidth + SidePieceMinimumWidth + SidePieceMargins;

    /// <summary>The side column's width in a panel under the map this wide.</summary>
    public static double SidePieceWidth(double panelWidth) =>
        double.IsFinite(panelWidth)
            ? Math.Clamp(panelWidth - ObjectivesMinimumWidth - SidePieceMargins, SidePieceMinimumWidth, SidePieceFullWidth)
            : SidePieceFullWidth;

    /// <summary>The least map worth drawing beside the panel.</summary>
    /// <remarks>
    /// <see cref="MapPanelLayout.Choose"/> keeps Beside when Beside draws nothing at all, which on a
    /// shell narrower than the panel left a map card of no width and a panel clipped on the right.
    /// </remarks>
    public const double MapMinimumWidthBeside = 300;

    /// <summary>The arrangement for a map placement, given the width and height the map and panel share.</summary>
    public static PlanPanelArrangement Arrange(MapPanelPlacement placement, double width, double height, double panelWidthBeside)
    {
        if (placement == MapPanelPlacement.Beside &&
            (!double.IsFinite(width) || width - panelWidthBeside >= MapMinimumWidthBeside))
        {
            return PlanPanelArrangement.Beside;
        }

        if (!double.IsFinite(width) || width >= SideBySideMinimumWidth)
        {
            return PlanPanelArrangement.BelowSideBySide;
        }

        return !double.IsFinite(height) || height - StackedPanelMinimumHeight >= MapMinimumHeight
            ? PlanPanelArrangement.BelowStacked
            : PlanPanelArrangement.PanelOnly;
    }
}
