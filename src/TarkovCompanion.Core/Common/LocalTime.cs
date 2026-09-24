using System.Globalization;

namespace TarkovCompanion.Core.Common;

/// <summary>
/// The one place a stored instant becomes the clock time the player reads. Every user-visible
/// absolute time goes through here; storage, the protocol, the database and the logs stay UTC and
/// never do.
/// </summary>
/// <remarks>
/// Before this, each view model chose for itself: some called <c>ToLocalTime</c>, some printed
/// <c>:u</c>, and the self-test printed "2026-09-19 03:03:39 UTC" to a player at UTC-4, so half
/// the product read four hours out. Nothing caught it because every test ran on a CI box whose
/// zone is UTC, where local and UTC are the same string. <see cref="Zone"/> is therefore a value
/// a test can pin (<see cref="UseZone"/>) rather than the process-wide zone, which a test cannot
/// change without racing every other test.
/// Relative times ("4h ago") are not formatted here and should stay relative.
/// </remarks>
public static class LocalTime
{
    private static readonly AsyncLocal<TimeZoneInfo?> Pinned = new();
    private static TimeZoneInfo? _profileZone;

    /// <summary>
    /// The player's own zone: the one a test pinned for this async flow, else the active profile's
    /// chosen zone (#269), else the machine's.
    /// </summary>
    public static TimeZoneInfo Zone => Pinned.Value ?? Volatile.Read(ref _profileZone) ?? TimeZoneInfo.Local;

    /// <summary>
    /// Sets the zone the active profile asks for, or null for the machine's. Called by the app when
    /// the active profile changes; a test pins with <see cref="UseZone"/> instead, because this one
    /// is process-wide and would race every other test.
    /// </summary>
    public static void UseProfileZone(TimeZoneInfo? zone) => Volatile.Write(ref _profileZone, zone);

    /// <summary>
    /// Pins <see cref="Zone"/> for the current async flow until the returned scope is disposed.
    /// Nothing outside a test should call this.
    /// </summary>
    public static IDisposable UseZone(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var previous = Pinned.Value;
        Pinned.Value = zone;
        return new Restore(previous);
    }

    /// <summary>The same instant expressed with the player's UTC offset at that moment (DST included).</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary>Short date and short time in the player's culture and zone, such as "9/18/2026 11:03 PM".</summary>
    public static string Moment(DateTimeOffset instant, CultureInfo? culture = null) =>
        ToLocal(instant).ToString("g", culture ?? CultureInfo.CurrentCulture);

    public static string? Moment(DateTimeOffset? instant, CultureInfo? culture = null) =>
        instant is { } value ? Moment(value, culture) : null;

    /// <summary>Short date in the player's culture and zone.</summary>
    public static string Date(DateTimeOffset instant, CultureInfo? culture = null) =>
        ToLocal(instant).ToString("d", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Long time of day (with seconds) in the player's culture and zone.</summary>
    public static string Time(DateTimeOffset instant, CultureInfo? culture = null) =>
        ToLocal(instant).ToString("T", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Short time of day (no seconds) in the player's culture and zone.</summary>
    public static string ShortTime(DateTimeOffset instant, CultureInfo? culture = null) =>
        ToLocal(instant).ToString("t", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// "2026-09-18 23:03" in the player's zone, the same in every culture. For diagnostics and
    /// exports read next to log lines or spreadsheets, where a fixed order beats a familiar one.
    /// </summary>
    public static string Sortable(DateTimeOffset instant) =>
        ToLocal(instant).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"2026-09-18", the player's calendar day, the same in every culture.</summary>
    public static string SortableDate(DateTimeOffset instant) =>
        ToLocal(instant).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"2026-09-18 23:03:39" in the player's zone, the same in every culture.</summary>
    public static string SortableSeconds(DateTimeOffset instant) =>
        ToLocal(instant).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// ISO-8601 with the player's numeric offset, such as "2026-09-18T23:03:39.1234567-04:00". For
    /// machine-readable output: it is local for a person reading it and still names the exact
    /// instant for a program, so it cannot be mistaken for UTC or for another zone.
    /// </summary>
    public static string Iso(DateTimeOffset instant) =>
        ToLocal(instant).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>"20260918-230339" in the player's zone, for a name a person will see in a folder.</summary>
    public static string FileStamp(DateTimeOffset instant) =>
        ToLocal(instant).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The player's offset from UTC at <paramref name="instant"/>, such as "UTC-04:00", worded the
    /// way the map's time labels word it. For text that leaves the machine, where a reader in
    /// another zone needs to be told which clock the times use.
    /// </summary>
    public static string Offset(DateTimeOffset instant) =>
        ToLocal(instant).ToString("'UTC'zzz", CultureInfo.InvariantCulture);

    private sealed class Restore(TimeZoneInfo? previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }
}
