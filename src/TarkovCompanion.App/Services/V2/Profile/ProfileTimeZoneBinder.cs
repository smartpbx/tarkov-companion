using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.Services.V2.Profile;

/// <summary>
/// Keeps <see cref="LocalTime"/> in the active profile's time zone (#269), so every absolute time
/// the app shows follows a switch of profile or a change of the setting.
/// </summary>
/// <remarks>
/// Times already on screen redraw on their page's next refresh; nothing here pushes them. The game's
/// own log lines are still read in the machine's zone, because that is the zone the game wrote them
/// in: this changes what the player reads, not how a timestamp is parsed.
/// </remarks>
internal static class ProfileTimeZoneBinder
{
    public static IProfileRuntimeContextService Attach(IProfileRuntimeContextService context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ContextChanged += change => Apply(change.Snapshot);
        Apply(context.Current);
        return context;
    }

    /// <summary>The zone to show times in for this context: the active profile's, or null for the machine's.</summary>
    internal static TimeZoneInfo? ZoneFor(ProfileRuntimeContextSnapshot snapshot) =>
        ProfileTimeZone.Resolve(snapshot?.ActiveProfile?.Context.Locale.TimeZone);

    private static void Apply(ProfileRuntimeContextSnapshot snapshot) => LocalTime.UseProfileZone(ZoneFor(snapshot));
}
