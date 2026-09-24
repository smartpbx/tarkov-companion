using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.Localization;

/// <summary>The Raid page's words for enum values, so no label is made from an enum's name.</summary>
public static partial class RaidText
{
    public static string MarkScope(RaidMarkScope scope) => scope == RaidMarkScope.Private ? JustMe : Squad;

    public static string MarkLifetime(RaidMarkLifetime lifetime) => lifetime switch
    {
        RaidMarkLifetime.Ping => LifetimePing,
        RaidMarkLifetime.UntilRemoved => LifetimeUntilRemoved,
        RaidMarkLifetime.FiveMinutes => LifetimeFiveMinutes,
        RaidMarkLifetime.FifteenMinutes => LifetimeFifteenMinutes,
        RaidMarkLifetime.ThisRaid => LifetimeThisRaid,
        _ => lifetime.ToString(),
    };

    /// <summary>"4m 12s left", "until removed" or "this raid", as <see cref="RaidMarkLifetimes.TimeLeft"/> counts it.</summary>
    internal static string MarkTimeLeft(RaidMark mark, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (mark.State.ExpiresUtc is { } expires)
        {
            var left = expires - nowUtc;
            if (left <= TimeSpan.Zero)
            {
                return MarkExpiring;
            }

            return left >= TimeSpan.FromMinutes(1)
                ? MarkMinutesSecondsLeft((int)left.TotalMinutes, left.Seconds)
                : MarkSecondsLeft(Math.Max(1, (int)Math.Ceiling(left.TotalSeconds)));
        }

        return mark.Lifetime == RaidMarkLifetime.ThisRaid ? MarkThisRaid : MarkUntilRemoved;
    }

    public static string CoOpVisibility(CoOpExtractVisibility visibility) => visibility switch
    {
        CoOpExtractVisibility.Hidden => CoOpHidden,
        CoOpExtractVisibility.Dim => CoOpDim,
        CoOpExtractVisibility.Normal => CoOpNormal,
        _ => visibility.ToString(),
    };
}

/// <summary>The palette colours' names, said by a screen reader and shown on hover (#314).</summary>
public static partial class RaidText
{
    /// <summary>The name of a <see cref="TarkovCompanion.Core.Common.MarkPalette"/> colour, by its hex; Core's English for one not in the table.</summary>
    public static string MarkColourName(TarkovCompanion.Core.Common.MarkColour colour)
    {
        ArgumentNullException.ThrowIfNull(colour);
        var key = $"Raid.MarkColour.{colour.Hex.TrimStart('#')}";
        return UiText.English.ContainsKey(key) ? UiText.Get(key) : colour.Name;
    }
}
