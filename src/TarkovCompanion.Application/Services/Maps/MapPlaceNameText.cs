namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// [#931] What a catalog place name is written as on the plan: English where there is an English
/// name for it, the catalog's own text otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Reported from Interchange: the third floor of the mall wrote "АПТЕКА", "МУЗЕЙ ИСТОРИИ" and
/// "ЗАКРЫТО НА РЕМОНТ" among English shop names. The catalog publishes one text per label and no
/// translation, and those eight are copied off the shop signs in the game.
/// </para>
/// <para>
/// Only a sign that says what the place is gets an English name here: a pharmacy is a pharmacy
/// in any language, and "closed for repair" is not a name at all. A brand keeps its own spelling
/// ("ТАРЗДРАВ", "ПУШКИН", "НА-СВЯЗИ"), because that is what the sign in the raid says and a
/// transliteration nobody has seen would be a name for nowhere. An English text from the catalog
/// always wins; this table is consulted only for text the catalog gives in Russian.
/// </para>
/// </remarks>
public static class MapPlaceNameText
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["АПТЕКА"] = "Pharmacy",
        ["МУЗЕЙ ИСТОРИИ"] = "History Museum",
        ["МЕБЕЛЬ МК"] = "MK Furniture",
        ["ЗАКРЫТО НА РЕМОНТ"] = "Closed for repair",
        ["СКОРО ОТКРЫТИЕ"] = "Opening soon",
    };

    /// <summary>The text to write for a catalog place name.</summary>
    public static string For(string catalogText)
    {
        ArgumentNullException.ThrowIfNull(catalogText);
        var trimmed = catalogText.Trim();
        return English.TryGetValue(trimmed, out var english) ? english : catalogText;
    }
}
