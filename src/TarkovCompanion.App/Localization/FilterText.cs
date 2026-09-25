namespace TarkovCompanion.App.Localization;

/// <summary>
/// [#902 P8] The words beside a list that a remembered filter has emptied, shared by every page.
/// </summary>
/// <remarks>
/// Filters now outlive a visit and a restart, so a chip set last week can greet the player with
/// an empty list. Each page says why in its own words and offers this one button to show all.
/// </remarks>
public static class FilterText
{
    public static string ShowAll => UiText.Get("Filter.ShowAll");
}
