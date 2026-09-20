using System.Net;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// [#317] What a wrong key costs, which used to be nothing.
/// </summary>
/// <remarks>
/// RISK-RELAY-KEY-BRUTEFORCE and RISK-ADMIN-KEY-BRUTEFORCE were the same finding twice: both
/// secrets were compared in fixed time and guessed at whatever rate the relay could answer. A
/// group key has an eight-character floor and an admin key had no floor at all, so the length was
/// doing all of the work and there was no second control behind it.
/// </remarks>
public sealed class RelayAttemptLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Somebody_who_mistypes_once_pays_nothing()
    {
        var limiter = new RelayAttemptLimiter(new MovableClock(Start));

        for (var attempt = 0; attempt < RelayAttemptLimiter.FreeAttempts; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
            Assert.Equal(TimeSpan.Zero, limiter.DelayFor("10.0.0.5"));
        }
    }

    [Fact]
    public void The_delay_grows_after_the_free_attempts_and_stops_at_two_seconds()
    {
        var limiter = new RelayAttemptLimiter(new MovableClock(Start));
        var seen = new List<TimeSpan>();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
            seen.Add(limiter.DelayFor("10.0.0.5"));
        }

        Assert.Equal(TimeSpan.Zero, seen[RelayAttemptLimiter.FreeAttempts - 1]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), seen[RelayAttemptLimiter.FreeAttempts]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), seen[RelayAttemptLimiter.FreeAttempts + 1]);
        Assert.All(seen, delay => Assert.True(delay <= RelayAttemptLimiter.LongestDelay));
        Assert.Equal(RelayAttemptLimiter.LongestDelay, seen[^1]);
    }

    [Fact]
    public void Twenty_wrong_keys_inside_the_window_are_refused_for_a_minute()
    {
        var time = new MovableClock(Start);
        var limiter = new RelayAttemptLimiter(time);

        for (var attempt = 0; attempt < RelayAttemptLimiter.RefusalThreshold; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
        }

        Assert.True(limiter.IsRefused("10.0.0.5", out var until));
        Assert.Equal(Start + RelayAttemptLimiter.Refusal, until);

        // Sixty seconds, not longer, because this buckets by remote address: a relay behind a
        // reverse proxy sees the proxy, and a long lockout there is an outage for everybody.
        time.Advance(RelayAttemptLimiter.Refusal + TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsRefused("10.0.0.5", out _));
    }

    [Fact]
    public void One_caller_cannot_refuse_another()
    {
        var limiter = new RelayAttemptLimiter(new MovableClock(Start));

        for (var attempt = 0; attempt < RelayAttemptLimiter.RefusalThreshold; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
        }

        Assert.True(limiter.IsRefused("10.0.0.5", out _));
        Assert.False(limiter.IsRefused("10.0.0.6", out _));
        Assert.Equal(TimeSpan.Zero, limiter.DelayFor("10.0.0.6"));
    }

    [Fact]
    public void Getting_it_right_clears_what_getting_it_wrong_cost()
    {
        var limiter = new RelayAttemptLimiter(new MovableClock(Start));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
        }

        Assert.True(limiter.DelayFor("10.0.0.5") > TimeSpan.Zero);

        limiter.Record("10.0.0.5", authorised: true);

        Assert.Equal(TimeSpan.Zero, limiter.DelayFor("10.0.0.5"));
        Assert.False(limiter.IsRefused("10.0.0.5", out _));
    }

    [Fact]
    public void Failures_older_than_the_window_are_forgotten()
    {
        var time = new MovableClock(Start);
        var limiter = new RelayAttemptLimiter(time);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            limiter.Record("10.0.0.5", authorised: false);
        }

        time.Advance(RelayAttemptLimiter.Window + TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, limiter.DelayFor("10.0.0.5"));
        limiter.Record("10.0.0.5", authorised: false);
        Assert.Equal(TimeSpan.Zero, limiter.DelayFor("10.0.0.5"));
    }

    /// <summary>The caller is the transport's address, never a header a caller writes.</summary>
    /// <remarks>
    /// Counting `X-Forwarded-For` would let one caller be as many callers as it liked, which is
    /// the exact thing being limited.
    /// </remarks>
    [Fact]
    public void A_caller_is_named_by_its_address_and_an_unknown_one_still_counts()
    {
        Assert.Equal("10.0.0.5", RelayAttemptLimiter.CallerOf(IPAddress.Parse("10.0.0.5")));
        Assert.Equal("unknown", RelayAttemptLimiter.CallerOf(null));
    }

    [Fact]
    public void The_record_of_callers_is_bounded()
    {
        var limiter = new RelayAttemptLimiter(new MovableClock(Start));

        for (var caller = 0; caller < RelayAttemptLimiter.MaximumTracked + 64; caller++)
        {
            limiter.Record($"10.{caller / 65536 % 256}.{caller / 256 % 256}.{caller % 256}", authorised: false);
        }

        // The dictionary is the one thing here an attacker chooses the size of.
        Assert.True(limiter.Tracked <= RelayAttemptLimiter.MaximumTracked);
    }

    /// <summary>
    /// The limiter is composed, and composed around the paths that check a key.
    /// </summary>
    /// <remarks>
    /// A limiter nobody calls throttles nothing, and this one is wired in `Program.cs`'s top-level
    /// statements, which no test in this suite can execute — the relay's own HTTP host fixture
    /// builds its routes directly and never runs that file. So this reads the composition rather
    /// than exercising it, the way the shell's host-contract tests read markup. It is a guard
    /// against the wiring being deleted, not proof that a live relay answers 429; that belongs to
    /// an authorized staging run, which the register still asks for.
    /// </remarks>
    [Fact]
    public void The_limiter_is_wired_into_the_relay_around_the_paths_that_check_a_key()
    {
        var program = File.ReadAllText(RepositoryFile("src", "TarkovCompanion.GroupServer", "Program.cs"));

        Assert.Contains("new RelayAttemptLimiter(", program, StringComparison.Ordinal);
        Assert.Contains("RelayAccess.IsGroupPath(path) || RelayAccess.IsAdminPath(path)", program, StringComparison.Ordinal);
        Assert.Contains("attempts.IsRefused(caller, out var until)", program, StringComparison.Ordinal);
        Assert.Contains("attempts.DelayFor(caller)", program, StringComparison.Ordinal);
        // The outcome, not the request: a room polled quickly is not a wrong key, and a status
        // that says nothing about the key neither counts nor clears.
        Assert.Contains("if (status is 401 or 403)", program, StringComparison.Ordinal);
        Assert.Contains("attempts.Record(caller, authorised: false);", program, StringComparison.Ordinal);
        Assert.Contains("attempts.Record(caller, authorised: true);", program, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status429TooManyRequests", program, StringComparison.Ordinal);
    }

    /// <summary>The relay's own outbound reads have a ceiling, not only a clock.</summary>
    /// <remarks>
    /// [#317, RISK-EXTERNAL-DATA-BOUNDS] `GetByteArrayAsync` buffers whatever arrives, so a
    /// thirty-second timeout bounded how long an upstream had to hand this relay a body, not how
    /// large it could be. The desktop reader has had a ceiling for a while; these two had none.
    /// </remarks>
    [Fact]
    public void The_relays_outbound_clients_cap_what_they_will_buffer()
    {
        var program = File.ReadAllText(RepositoryFile("src", "TarkovCompanion.GroupServer", "Program.cs"));

        Assert.Equal(2, program.Split("MaxResponseContentBufferSize").Length - 1);
    }

    private static string RepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return Path.Combine([directory.FullName, .. segments]);
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }

    /// <summary>A clock a test can move, since the suite has no time-testing package.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
