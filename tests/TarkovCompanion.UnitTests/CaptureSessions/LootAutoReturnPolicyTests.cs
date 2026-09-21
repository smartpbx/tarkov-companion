using TarkovCompanion.Application.Services.CaptureSessions;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// #572: the Loot page must return to the Raid map by itself, but only when the app put the
/// player there in the first place. Every case here is driven by a manual clock so "15 seconds
/// pass" is an assertion, not a real wait.
/// </summary>
public sealed class LootAutoReturnPolicyTests
{
    [Fact]
    public void AutoEnteredMidRaidCountsDown()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);

        policy.EnterLoot(handEntered: false, inRaid: true);

        Assert.True(policy.IsActive);
        Assert.True(policy.ShowsCountdown);
        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds), policy.Remaining);

        clock.Advance(TimeSpan.FromSeconds(14.9));
        Assert.False(policy.Tick());

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.True(policy.Tick());
        // Ticking again after firing must not fire twice.
        Assert.False(policy.Tick());
    }

    [Fact]
    public void HandEnteredNeverCountsDownOrMoves()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);

        policy.EnterLoot(handEntered: true, inRaid: true);

        Assert.False(policy.IsActive);
        Assert.Null(policy.Remaining);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(policy.Tick());
        Assert.False(policy.NonLootScreenshot());
    }

    [Fact]
    public void OutsideARaidNeverCountsDownOrMoves()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);

        // Opened by a capture, but not mid-raid (a player reviewing an old scan at home).
        policy.EnterLoot(handEntered: false, inRaid: false);

        Assert.False(policy.IsActive);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(policy.Tick());
        Assert.False(policy.NonLootScreenshot());
    }

    [Fact]
    public void NewLootScreenshotRestartsTheWait()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        clock.Advance(TimeSpan.FromSeconds(14));
        policy.NewLootScreenshot();

        // Had the wait not restarted, one more second would have fired it.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(policy.Tick());
        Assert.Equal(TimeSpan.FromSeconds(14), policy.Remaining);
    }

    [Fact]
    public void ReenteringLootFromAFreshCaptureAlsoRestartsTheWait()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        clock.Advance(TimeSpan.FromSeconds(10));
        // The shell re-shows the Loot page for a second loot screenshot; ShowLootScanResult calls
        // EnterLoot again exactly as it did the first time.
        policy.EnterLoot(handEntered: false, inRaid: true);

        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds), policy.Remaining);
    }

    [Fact]
    public void NonLootScreenshotReturnsAtOnceWithoutWaitingForTheClock()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.NonLootScreenshot());
        Assert.False(policy.IsActive);
        Assert.Null(policy.Remaining);

        // Firing once must not leave a stale deadline for Tick to fire again.
        Assert.False(policy.Tick());
    }

    [Fact]
    public void NonLootScreenshotIsANoOpWhenNotActive()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: true, inRaid: true);

        Assert.False(policy.NonLootScreenshot());
    }

    [Fact]
    public void StayPinsThePageAndSuppressesTheTimedReturn()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        policy.Stay(true);
        Assert.True(policy.IsPinned);
        Assert.False(policy.IsActive);
        Assert.Null(policy.Remaining);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(policy.Tick());
        Assert.False(policy.NonLootScreenshot());
    }

    [Fact]
    public void StaySurvivesANewLootScreenshotForTheRestOfTheRaid()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);
        policy.Stay(true);

        // A second loot screenshot re-decides the same page; the pin from "Stay" must hold.
        policy.EnterLoot(handEntered: false, inRaid: true);

        Assert.True(policy.IsPinned);
        Assert.False(policy.IsActive);
    }

    [Fact]
    public void UnstayResumesTheCountdown()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);
        policy.Stay(true);

        policy.Stay(false);

        Assert.True(policy.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds), policy.Remaining);
    }

    [Fact]
    public void LeavingClearsThePinAndTheCountdown()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);
        policy.Stay(true);

        policy.Leave();

        Assert.False(policy.IsPinned);
        Assert.False(policy.IsActive);
        Assert.Null(policy.Remaining);

        // The next entry to Loot in this raid starts from a clean slate.
        policy.EnterLoot(handEntered: false, inRaid: true);
        Assert.True(policy.IsActive);
        Assert.False(policy.IsPinned);
    }

    [Fact]
    public void OffConfigurationStopsTheTimedReturnButNotTheMovementReturn()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        policy.Configure(null);

        Assert.True(policy.IsActive);
        Assert.False(policy.ShowsCountdown);
        Assert.Null(policy.Remaining);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(policy.Tick());

        // The player moving away from loot is still honoured even with the timer off.
        Assert.True(policy.NonLootScreenshot());
    }

    [Fact]
    public void ConfigureClampsToTheDocumentedRange()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);

        policy.Configure(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.MinimumSeconds), policy.Timeout);

        policy.Configure(TimeSpan.FromSeconds(600));
        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.MaximumSeconds), policy.Timeout);

        policy.Configure(TimeSpan.FromSeconds(-5));
        Assert.Null(policy.Timeout);
    }

    [Fact]
    public void RaidEndingStopsTheCountdownWithoutForcingAMove()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);

        clock.Advance(TimeSpan.FromSeconds(5));
        policy.SetInRaid(false);

        Assert.False(policy.IsActive);
        Assert.Null(policy.Remaining);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(policy.Tick());
    }

    [Fact]
    public void RaidRestartingAfterEndingResumesTheCountdown()
    {
        var clock = new ManualClock();
        var policy = new LootAutoReturnPolicy(clock);
        policy.EnterLoot(handEntered: false, inRaid: true);
        policy.SetInRaid(false);

        policy.SetInRaid(true);

        Assert.True(policy.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds), policy.Remaining);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
