using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.Localization;

/// <summary>[#712 2-5] The Ask box's words (#314 recipe: <c>Ask.*</c> in the tables).</summary>
/// <remarks>
/// The Application decides each answer line as a <see cref="Phrase"/> (<see cref="AskLine"/>) and
/// this says it. Quest, item, station, trader, map and exit names are data and are shown as the
/// catalog writes them.
/// </remarks>
public static class AskText
{
    public static string Heading => UiText.Get("Ask.Heading");
    public static string Asking => UiText.Get("Ask.Asking");
    public static string Hint => UiText.Get("Ask.Hint");

    public static string Also(string names) => UiText.Format("Ask.Also", names);
    public static string Closest(string names) => UiText.Format("Ask.Closest", names);
    public static string TryClosest(string name) => UiText.Format("Ask.TryClosest", name);

    public static string Line(Phrase phrase) => PhraseText.Say(phrase);

    public static string Intent(AskIntent? intent) => intent is { } value ? UiText.Get($"Ask.Intent.{value}") : Heading;

    public static string Unanswered(AskUnanswered reason, string subject) => reason switch
    {
        AskUnanswered.NoMatch or AskUnanswered.Ambiguous when subject.Length > 0 => UiText.Format($"Ask.Unanswered.{reason}", subject),
        AskUnanswered.NoMatch or AskUnanswered.Ambiguous => UiText.Get("Ask.Unanswered.NotUnderstood"),
        _ => UiText.Get($"Ask.Unanswered.{reason}"),
    };

    /// <summary>Where a fact came from, with its age: an absolute catalog time, a relative screenshot age.</summary>
    public static string Source(AskSource source, DateTimeOffset nowUtc) => source.Kind switch
    {
        AskSourceKind.Catalog when source.AsOfUtc is { } asOf => UiText.Format("Ask.Source.CatalogAsOf", LocalTime.Moment(asOf)),
        AskSourceKind.Catalog => UiText.Get("Ask.Source.Catalog"),
        AskSourceKind.Profile => UiText.Get("Ask.Source.Profile"),
        AskSourceKind.Screenshot when source.AsOfUtc is { } taken => UiText.Format("Ask.Source.Screenshot", UnitText.Ago(nowUtc - taken)),
        AskSourceKind.Screenshot => UiText.Get("Ask.Source.ScreenshotUnknown"),
        AskSourceKind.Modelled => UiText.Get("Ask.Source.Modelled"),
        AskSourceKind.LocalModel => UiText.Get("Ask.Source.LocalModel"),
        _ => string.Empty,
    };

    public static string Link(AskLink link) => link.Kind == AskLinkKind.IntelItem
        ? UiText.Format("Ask.Link.IntelItem", link.Name)
        : UiText.Get($"Ask.Link.{link.Kind}");
}
