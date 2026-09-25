using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#886] Two idle squadmates in a raid exchange on the hold, not at the rate bound.
/// </summary>
/// <remarks>
/// Clayton's log from 2.0.1620 had every member re-publishing every 0.30 s for seven hours:
/// the trail's and the raid clock's ages grew between two publishes of the same thing, the relay
/// counted each one as a change, the change woke the other member's held exchange, and its
/// publish woke the first. This drives the relay's own <see cref="GroupRooms"/> and
/// <see cref="GroupRoomChanges"/> on a stepped clock with the client's exchange rules as the
/// desktop runs them: a held exchange of five seconds, ended early by somebody else's change or
/// by a revision already ahead of the one it saw, and no two exchanges closer than 300 ms.
/// </remarks>
public sealed class RelayWakeStormTests
{
    private const string Room = "room";
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_idle_members_with_trails_and_a_raid_clock_exchange_on_the_hold()
    {
        var run = new Squad();
        run.Until(TimeSpan.FromSeconds(30));

        // About six each, one per five-second hold; the storm was about a hundred.
        Assert.InRange(run.Clay.Exchanges, 5, 9);
        Assert.InRange(run.Geo.Exchanges, 5, 9);
    }

    /// <summary>Teammate latency: ages no longer wake anybody, and a real move still does, at once.</summary>
    [Fact]
    public void A_real_move_still_wakes_the_squadmate_at_once()
    {
        var run = new Squad();
        run.Until(TimeSpan.FromSeconds(12));
        var clayBefore = run.Clay.Exchanges;

        run.Geo.X = 400;
        run.Geo.LocalChange = true;
        var moved = run.Now;
        run.Until(TimeSpan.FromSeconds(12.5));

        Assert.True(run.Clay.Exchanges > clayBefore, "Clay should have been woken by Geo's move.");
        Assert.NotNull(run.Clay.LastAnswer);
        Assert.True(run.Clay.LastAnswer!.Value - moved <= Gap, $"Clay heard {(run.Clay.LastAnswer.Value - moved).TotalMilliseconds} ms after the move.");
        Assert.Equal(400, run.Clay.LastSeenGeoX);
    }

    private sealed class Member(string name, double x)
    {
        public string Name { get; } = name;

        public double X { get; set; } = x;

        public bool LocalChange { get; set; }

        public int Exchanges { get; set; }

        public long SeenRevision { get; set; }

        public DateTimeOffset LastStarted { get; set; } = DateTimeOffset.MinValue;

        /// <summary>When the exchange in flight answers, or null when none is in flight.</summary>
        public DateTimeOffset? HeldUntil { get; set; }

        public DateTimeOffset? LastAnswer { get; set; }

        public double? LastSeenGeoX { get; set; }
    }

    private sealed class Squad
    {
        private readonly SteppedClock _clock = new(Start);
        private readonly GroupRooms _rooms;
        private readonly GroupRoomChanges _changes;

        public Squad()
        {
            _rooms = new GroupRooms(_clock);
            _changes = new GroupRoomChanges(_clock);
        }

        public Member Clay { get; } = new("Clay", 100);

        public Member Geo { get; } = new("Geo", 200);

        public DateTimeOffset Now => _clock.GetUtcNow();

        public void Until(TimeSpan elapsed)
        {
            while (Now - Start < elapsed)
            {
                foreach (var member in new[] { Clay, Geo })
                {
                    Tick(member);
                }

                _clock.Advance(Step);
            }
        }

        private void Tick(Member member)
        {
            var now = Now;
            if (member.HeldUntil is { } until)
            {
                var ahead = _changes.RevisionFor(Room, member.Name) > member.SeenRevision;
                if (now < until && !ahead && !member.LocalChange)
                {
                    return;
                }

                // The answer: the room as it is now, with the revision it is at.
                member.SeenRevision = _changes.RevisionFor(Room, member.Name);
                member.HeldUntil = null;
                member.LastAnswer = now;
                if (member == Clay)
                {
                    member.LastSeenGeoX = _rooms.Read(Room, member.Name).Members.SingleOrDefault()?.X;
                }
            }

            if (now - member.LastStarted < Gap)
            {
                return;
            }

            member.LastStarted = now;
            member.LocalChange = false;
            member.Exchanges++;
            if (_rooms.Publish(Room, member.Name, Describe(member, now)))
            {
                _changes.Record(Room, member.Name);
            }

            member.HeldUntil = now + Hold;
        }

        /// <summary>An idle member in a raid: two screenshots behind them and one clock reading.</summary>
        private static GroupMemberState Describe(Member member, DateTimeOffset now)
        {
            var screenshots = Start - TimeSpan.FromSeconds(40);
            var clockRead = Start - TimeSpan.FromSeconds(25);
            return new GroupMemberState(member.Name, "customs", "InRaid", "pmc", member.X, 50, 90, (now - screenshots).TotalSeconds, [], [])
            {
                Trail =
                [
                    new GroupTrailPoint(member.X - 20, 40, (now - screenshots).TotalSeconds + 30),
                    new GroupTrailPoint(member.X - 10, 45, (now - screenshots).TotalSeconds + 12.5),
                ],
                RaidClockSeconds = 1800,
                RaidClockAgeSeconds = (now - clockRead).TotalSeconds,
            };
        }
    }

    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
