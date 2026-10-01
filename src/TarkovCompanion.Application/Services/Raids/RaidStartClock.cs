namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// [#985] Puts the game server's raid start on the clock the rest of the companion reads.
/// </summary>
/// <remarks>
/// <para>
/// A <c>userConfirmed</c> notification is dated by its ObjectId, which is the game server's own
/// time (<see cref="EftLogParser"/>). Everything it is later compared with — the raid clock, the
/// opening five minutes of spawns, a raid's length in History — is read off this PC's clock. On
/// a PC whose clock is wrong the two disagree by however wrong it is, and nothing said so.
/// </para>
/// <para>
/// Measured on the owner's real logs of 2026-09-24 and 2026-09-25: the PC was four hours fast,
/// so every PMC raid began "4 h 02 min ago" by the PC's clock and the opening window of possible
/// PMC spawns had closed before the loading screen did: 8 PMC raids out of 8 in those two
/// sessions. On 2026-09-23 the same PC was right, and its PMC raids opened the window.
/// </para>
/// <para>
/// A clock that is wrong is wrong by a time zone, and a time zone is a whole number of quarter
/// hours: the RTC kept as UTC and read as local time, a zone set to the wrong city. So the
/// server's start, accurate to the second, is moved by the nearest whole quarter hour between
/// the two clocks at the moment the line was read. Reading a line takes seconds, which rounds
/// to nothing, and a PC that is right is left exactly where it was.
/// </para>
/// <para>
/// Only for a line read as it was written. A raid recovered at startup is dated by the replay
/// (<see cref="RaidReplayDecision"/>), whose "read at" is the restart and says nothing about
/// the clock. A gap wider than any time zone is not a clock either, but a line read long after
/// it was written, and is left alone.
/// </para>
/// </remarks>
public static class RaidStartClock
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    /// <summary>The widest a time zone puts local time from UTC.</summary>
    private static readonly TimeSpan WidestZone = TimeSpan.FromHours(14);

    /// <summary>The server's start, on the clock that read the line saying so.</summary>
    /// <param name="serverStartUtc">The raid start the game server stamped.</param>
    /// <param name="readUtc">When this PC read the line, by this PC's clock.</param>
    public static DateTimeOffset OnThisPc(DateTimeOffset serverStartUtc, DateTimeOffset readUtc)
    {
        var gap = readUtc - serverStartUtc;
        if (gap.Duration() > WidestZone + Quarter)
        {
            return serverStartUtc;
        }

        var quarters = Math.Round(gap / Quarter, MidpointRounding.AwayFromZero);
        return serverStartUtc + (Quarter * quarters);
    }

    /// <summary>The start a live line gives a raid entered on it.</summary>
    public static DateTimeOffset For(DateTimeOffset? serverStartUtc, bool resumed, DateTimeOffset readUtc) =>
        serverStartUtc is not { } server ? readUtc
            : resumed ? server
            : OnThisPc(server, readUtc);
}
