namespace TarkovCompanion.Core.Common;

/// <summary>Whether a wall-clock stamp is recent, counting a stamp from the future as not.</summary>
/// <remarks>
/// [#799] "now - then &lt; window" is true for every stamp ahead of now. A cache stamped by a
/// clock that was four hours fast, and read after the clock was put right, looked fresh for four
/// hours: the squad's quest list and the traffic prior stopped updating for the evening. A stamp
/// from the future is a stamp from a clock that has since been set, so it is simply stale.
/// </remarks>
public static class WallClockAge
{
    public static bool IsWithin(DateTimeOffset nowUtc, DateTimeOffset thenUtc, TimeSpan window)
    {
        var age = nowUtc - thenUtc;
        return age >= TimeSpan.Zero && age < window;
    }
}
