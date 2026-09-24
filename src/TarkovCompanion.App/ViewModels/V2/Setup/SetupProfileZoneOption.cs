using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>[#269] One time zone a profile's times can be shown in.</summary>
/// <param name="Id">What is stored: "system" or a zone id this machine knows.</param>
/// <param name="Label">What the list shows.</param>
public sealed record SetupProfileZoneOption(string Id, string Label)
{
    private static IReadOnlyList<SetupProfileZoneOption>? _known;

    public override string ToString() => Label;

    /// <summary>"System time", "UTC", then every zone this machine knows, plus the stored one if it does not.</summary>
    public static IReadOnlyList<SetupProfileZoneOption> All(string? stored)
    {
        var known = _known ??= Build();
        if (ProfileTimeZone.IsSystem(stored) || known.Any(option => option.Id == stored))
        {
            return known;
        }

        return [.. known, new(stored!, SetupText.ZoneNotOnThisComputer(stored!))];
    }

    public static SetupProfileZoneOption Find(IReadOnlyList<SetupProfileZoneOption> options, string? stored) =>
        ProfileTimeZone.IsSystem(stored)
            ? options[0]
            : options.FirstOrDefault(option => option.Id == stored) ?? options[0];

    /// <summary>"System time", or the zone's id, for the one-line profile summary.</summary>
    public static string ShortLabel(string? stored) => ProfileTimeZone.IsSystem(stored) ? SetupText.ZoneSystemTime : stored!;

    private static IReadOnlyList<SetupProfileZoneOption> Build()
    {
        var list = new List<SetupProfileZoneOption> { new(ProfileTimeZone.System, SetupText.ZoneSystemTime), new("UTC", "UTC") };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            // The placeholder id reads as "system" (see ProfileTimeZone); UTC is offered once, above.
            if (ProfileTimeZone.IsSystem(zone.Id) || zone.Id is "UTC" or "Etc/UCT" or "UCT" or "Universal" or "Zulu")
            {
                continue;
            }

            list.Add(new(zone.Id, zone.DisplayName));
        }

        return list;
    }
}
