using System.Globalization;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>
/// #284: a photographed flea screen shows offers as they stood when the screenshot was taken.
/// The companion never refreshes the market, so the ranking says when it saw them and, once a
/// few minutes have gone by, that they may have sold since.
/// </summary>
public static class FleaOfferAge
{
    /// <summary>After this long a photographed offer is worded as possibly gone.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>True once the offers were photographed <see cref="StaleAfter"/> or more ago.</summary>
    public static bool IsStale(DateTimeOffset observedUtc, DateTimeOffset now) => now - observedUtc >= StaleAfter;

    /// <summary>"Offers as seen at 14:32", then "Offers as seen at 14:32 · 12 min ago, may be gone".</summary>
    public static string Describe(DateTimeOffset observedUtc, DateTimeOffset now, CultureInfo? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        var seen = $"Offers as seen at {LocalTime.ShortTime(observedUtc, format)}";
        if (!IsStale(observedUtc, now))
        {
            return seen;
        }

        var age = now - observedUtc;
        var ago = age < TimeSpan.FromHours(1)
            ? $"{((int)age.TotalMinutes).ToString(format)} min ago"
            : age < TimeSpan.FromDays(1)
                ? $"{((int)age.TotalHours).ToString(format)} h ago"
                : $"on {LocalTime.Date(observedUtc, format)}";
        return $"{seen} · {ago}, may be gone";
    }
}
