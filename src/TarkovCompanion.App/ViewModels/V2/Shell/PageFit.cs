namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>How Intel › Ammo lays out, given its own width.</summary>
/// <param name="DetailBeside">Whether the chosen round's panel stands beside the table rather than under it.</param>
/// <param name="CaliberListMinimum">The least the caliber list is given.</param>
/// <param name="ClassesOnOwnLine">Whether each round's six class chips go on a line under its numbers.</param>
public readonly record struct AmmoPageFit(bool DetailBeside, double CaliberListMinimum, bool ClassesOnOwnLine);

/// <summary>
/// [#832] What the Intel and Plan pages give up when the page is narrow.
/// </summary>
/// <remarks>
/// Every width here is the page's own: the shell less the rail. On a 1920x1080 window that is
/// about 1752 at 100%, 1112 at 150% and 900 at 200% (the rail drops its words below a 1100-wide
/// shell). The fixed columns these pages had were chosen at 100%: at 200% Ammo's 460-wide round
/// panel and 300-wide caliber list left the table about 110 wide and cut every chip off, and
/// Hideout's two 600-wide sections in a card about 430 wide were drawn off its left-hand edge.
/// Nothing here moves at 100% on 1920x1080; the tests pin that.
/// </remarks>
public static class PageFit
{
    /// <summary>A side panel's width: its full width while the rest of the page keeps <paramref name="mainMinimum"/>, never under <paramref name="minimum"/>.</summary>
    public static double SidePanelWidth(double pageWidth, double full, double minimum, double mainMinimum) =>
        double.IsFinite(pageWidth) && pageWidth > 0
            ? Math.Clamp(pageWidth - mainMinimum, Math.Min(minimum, full), full)
            : full;

    /// <summary>Ammo's round panel, as it was designed.</summary>
    public const double AmmoDetailWidth = 460;

    /// <summary>The caliber list's floor at 100%.</summary>
    public const double AmmoCaliberListWidth = 300;

    /// <summary>The caliber list's floor on a narrow page: ".300 Blackout" and its count still fit.</summary>
    public const double AmmoCaliberListNarrowWidth = 240;

    /// <summary>Below this the caliber list takes its narrow floor.</summary>
    public const double AmmoNarrowPageWidth = 1000;

    /// <summary>The page's margins (16 each side) and the gap between the list and the table.</summary>
    public const double AmmoPageInsets = 48;

    /// <summary>
    /// The table pane a round needs to keep its chips on its own line: a 160-wide name, the five
    /// number columns (64+64+84+64+72), the 204 of chips, six 10 gaps, the row's 20 and the pane's 28.
    /// </summary>
    public const double AmmoTableOneLineWidth = 160 + 348 + 204 + 60 + 20 + 28;

    /// <summary>What the page must be for the round panel to stand beside a full table.</summary>
    public const double AmmoDetailBesideMinimumWidth =
        AmmoPageInsets + AmmoCaliberListWidth + AmmoTableOneLineWidth + AmmoDetailWidth;

    /// <summary>How Intel › Ammo lays out on a page this wide.</summary>
    public static AmmoPageFit Ammo(double pageWidth)
    {
        if (!double.IsFinite(pageWidth) || pageWidth <= 0)
        {
            return new AmmoPageFit(true, AmmoCaliberListWidth, false);
        }

        var beside = pageWidth >= AmmoDetailBesideMinimumWidth;
        var caliberMinimum = !beside && pageWidth < AmmoNarrowPageWidth ? AmmoCaliberListNarrowWidth : AmmoCaliberListWidth;
        var shared = (beside ? pageWidth - AmmoDetailWidth : pageWidth) - AmmoPageInsets;
        // The list is a 0.24* column capped at 380, as the page declares it.
        var caliber = Math.Clamp(shared * 0.24 / 1.24, caliberMinimum, 380);
        return new AmmoPageFit(beside, caliberMinimum, shared - caliber < AmmoTableOneLineWidth);
    }

    /// <summary>Loadout's kit panel, as it was designed.</summary>
    public const double LoadoutKitWidth = 520;

    /// <summary>The narrowest the kit panel goes: two slot tiles side by side.</summary>
    public const double LoadoutKitMinimumWidth = 400;

    /// <summary>What the search column keeps before the kit panel narrows.</summary>
    public const double LoadoutMainMinimumWidth = 480;

    /// <summary>
    /// The search column's content a suggestions heading needs beside its rules label and the
    /// 220-wide map picker: about 200 for the heading and 200 for the label, and the gaps.
    /// </summary>
    public const double LoadoutSuggestionsOneLineWidth = 640;

    /// <summary>Whether Loadout's suggestions put the rules label and map picker under the heading.</summary>
    /// <remarks>The search column is what the kit panel leaves, less its margins (32) and the card's padding (28).</remarks>
    public static bool LoadoutSuggestionsStacked(double pageWidth) =>
        double.IsFinite(pageWidth) && pageWidth > 0 &&
        pageWidth - SidePanelWidth(pageWidth, LoadoutKitWidth, LoadoutKitMinimumWidth, LoadoutMainMinimumWidth) - 60
            < LoadoutSuggestionsOneLineWidth;

    /// <summary>The narrowest the event editor holds a quest window's two dates beside its picker.</summary>
    public const double EventsEditorMinimumWidth = 380;

    /// <summary>
    /// Whether Plan › Events puts the applicable items under the editor instead of beside it.
    /// </summary>
    /// <remarks>
    /// The editor is half of a column that is 1/1.3 of the page less its insets. At 200% that half
    /// was about 310 and every picker was squeezed to its first letter.
    /// </remarks>
    public static bool EventsStacked(double pageWidth) =>
        double.IsFinite(pageWidth) && pageWidth > 0 &&
        (((pageWidth - 48) / 1.3) - 18) / 2 < EventsEditorMinimumWidth;

    /// <summary>Hideout's station list, as it was designed.</summary>
    public const double HideoutListWidth = 400;

    /// <summary>Hideout's station list on a narrow page.</summary>
    public const double HideoutListNarrowWidth = 320;

    /// <summary>One Hideout section, as it was designed; two sit side by side on a wide page.</summary>
    public const double HideoutSectionWidth = 600;

    /// <summary>The narrowest Hideout section.</summary>
    public const double HideoutSectionMinimumWidth = 280;

    /// <summary>The station list's width on a page this wide.</summary>
    public static double HideoutList(double pageWidth) =>
        double.IsFinite(pageWidth) && pageWidth > 0 && pageWidth < AmmoNarrowPageWidth
            ? HideoutListNarrowWidth
            : HideoutListWidth;

    /// <summary>
    /// One Hideout section's width: whatever the detail card holds, up to the designed 600.
    /// </summary>
    /// <remarks>The card's margin (32), its padding (40) and room for the scroll bar (12).</remarks>
    public static double HideoutSection(double pageWidth) =>
        double.IsFinite(pageWidth) && pageWidth > 0
            ? Math.Clamp(pageWidth - HideoutList(pageWidth) - 84, HideoutSectionMinimumWidth, HideoutSectionWidth)
            : HideoutSectionWidth;
}
