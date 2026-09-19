using System.Globalization;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.PlayerTime;

/// <summary>
/// The fixed, non-UTC clock every time-presentation test runs under.
/// </summary>
/// <remarks>
/// Every test used to run on a CI box whose zone is UTC, where "local" and "UTC" print the same
/// string, so a screen that showed UTC to a player at UTC-4 passed everything. Pinning a zone that
/// is never UTC (and never the machine's own) is what makes a wrong conversion fail here. The
/// culture is pinned too, so an expected "09/15/2026 14:00" does not depend on the runner's locale.
/// </remarks>
internal static class PlayerClock
{
    /// <summary>The reporting player's own offset, with no daylight saving to move it.</summary>
    public static readonly TimeZoneInfo UtcMinusFour = Fixed("Test/UTC-4", TimeSpan.FromHours(-4));

    /// <summary>A half-hour zone, so an offset that is not a whole number of hours is exercised.</summary>
    public static readonly TimeZoneInfo UtcPlusFiveThirty = Fixed("Test/UTC+5:30", new TimeSpan(5, 30, 0));

    public static IDisposable Pin(TimeZoneInfo? zone = null) => new Scope(zone ?? UtcMinusFour);

    private static TimeZoneInfo Fixed(string id, TimeSpan offset) =>
        TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);

    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly IDisposable _zone;

        public Scope(TimeZoneInfo zone)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            _zone = LocalTime.UseZone(zone);
        }

        public void Dispose()
        {
            _zone.Dispose();
            CultureInfo.CurrentCulture = _culture;
        }
    }
}
