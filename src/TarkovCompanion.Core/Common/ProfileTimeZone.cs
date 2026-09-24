namespace TarkovCompanion.Core.Common;

/// <summary>
/// A profile's time zone setting (#269), turned into the zone <see cref="LocalTime"/> shows times in.
/// </summary>
/// <remarks>
/// "system" means the machine's zone, and so does "Etc/UTC": #458 wrote that id into every profile
/// as a placeholder before anybody could choose, so reading it as a real choice would have moved
/// every existing player's clock to UTC the day this shipped. A player who wants UTC picks "UTC",
/// which is a different id for the same zone. An id this machine does not know (a profile moved
/// from another operating system) also falls back to the machine's zone rather than failing.
/// </remarks>
public static class ProfileTimeZone
{
    public const string System = "system";

    /// <summary>What <c>new ProfileLocale(...)</c> was given before the setting existed.</summary>
    public const string LegacyPlaceholder = "Etc/UTC";

    public static bool IsSystem(string? id) =>
        string.IsNullOrWhiteSpace(id)
        || string.Equals(id.Trim(), System, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id.Trim(), LegacyPlaceholder, StringComparison.Ordinal);

    /// <summary>The zone the setting names, or null for "the machine's zone".</summary>
    public static TimeZoneInfo? Resolve(string? id) =>
        IsSystem(id) || !TimeZoneInfo.TryFindSystemTimeZoneById(id!.Trim(), out var zone) ? null : zone;

    /// <summary>Whether a player may store this id: "system", or a zone this machine knows.</summary>
    public static bool IsValid(string? id) => IsSystem(id) || Resolve(id) is not null;
}
