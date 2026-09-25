using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#279] The gallery's empty, loading, degraded and error scenes. The hold is what makes a
/// loading state photographable without a sleep, so it must hold exactly what it names, let go
/// when told, and never hold a load it was not asked to.
/// </summary>
/// <remarks>
/// <see cref="LoadHold"/> is process-wide, so these hold made-up surfaces only: a real "plan"
/// held here would stall any Plan test running beside them.
/// </remarks>
public sealed class GalleryStateSceneTests
{
    [Fact]
    public async Task A_load_that_is_not_held_goes_straight_on()
    {
        LoadHold.Hold(["test-held"]);
        try
        {
            var other = LoadHold.WaitIfHeldAsync("test-other", CancellationToken.None);
            Assert.True(other.IsCompletedSuccessfully);
            Assert.False(LoadHold.IsWaiting("test-other"));
        }
        finally
        {
            LoadHold.ReleaseAll();
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_held_load_waits_at_its_hold_until_released_and_says_so()
    {
        LoadHold.Hold(["test-plan"]);
        var held = LoadHold.WaitIfHeldAsync("test-plan", CancellationToken.None);

        Assert.False(held.IsCompleted);
        Assert.True(LoadHold.IsWaiting("test-plan"));
        Assert.True(LoadHold.AnyWaiting);

        LoadHold.ReleaseAll();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(LoadHold.IsWaiting("test-plan"));
        // Released means nothing is held any more: the next load goes straight through.
        Assert.True(LoadHold.WaitIfHeldAsync("test-plan", CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_held_load_whose_page_closes_is_cancelled_and_stops_waiting()
    {
        LoadHold.Hold(["test-debrief"]);
        try
        {
            using var closing = new CancellationTokenSource();
            var held = LoadHold.WaitIfHeldAsync("test-debrief", closing.Token);
            closing.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(LoadHold.IsWaiting("test-debrief"));
        }
        finally
        {
            LoadHold.ReleaseAll();
        }
    }

    [Fact]
    public async Task Holding_again_after_a_release_holds_again()
    {
        LoadHold.Hold(["test-stash"]);
        LoadHold.ReleaseAll();
        LoadHold.Hold(["test-stash"]);
        var held = LoadHold.WaitIfHeldAsync("test-stash", CancellationToken.None);
        Assert.False(held.IsCompleted);

        LoadHold.ReleaseAll();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("empty", GallerySceneKind.Empty, true)]
    [InlineData("loading", GallerySceneKind.Loading, true)]
    [InlineData("Degraded", GallerySceneKind.Degraded, true)]
    [InlineData("error", GallerySceneKind.Error, true)]
    [InlineData("inspect", GallerySceneKind.Inspect, false)]
    [InlineData("routestops", GallerySceneKind.RouteStops, false)]
    [InlineData("spawnlines", GallerySceneKind.SpawnLines, false)]
    [InlineData("page", GallerySceneKind.Page, false)]
    public void The_gallery_names_each_scene_and_only_the_four_states_are_state_scenes(string name, GallerySceneKind kind, bool isState)
    {
        Assert.Equal(kind, GallerySceneKinds.Parse(name));
        Assert.Equal(isState, GalleryStateScene.IsState(kind));
    }

    [Fact]
    public void An_unknown_scene_names_the_ones_there_are()
    {
        var refused = Assert.Throws<ArgumentException>(() => GallerySceneKinds.Parse("stale"));
        Assert.Contains("degraded", refused.Message, StringComparison.Ordinal);
        Assert.Contains("routestops", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Degraded_data_is_six_days_old_and_data_already_older_keeps_its_age()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var fresh = new RuntimeDataState(DataAvailability.Current, 4200, 9, now.AddMinutes(-3), string.Empty);

        Assert.Equal(now.AddDays(-6), GalleryStateScene.Aged(fresh, now).UpdatedUtc);
        Assert.Equal(4200, GalleryStateScene.Aged(fresh, now).ItemCount);

        var older = fresh with { UpdatedUtc = now.AddDays(-9) };
        Assert.Same(older, GalleryStateScene.Aged(older, now));

        var undated = fresh with { UpdatedUtc = null };
        Assert.Equal(now.AddDays(-6), GalleryStateScene.Aged(undated, now).UpdatedUtc);
    }

    [Fact]
    public void The_lost_relay_is_the_last_squad_heard_marked_stale_with_the_relays_own_phrase()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var lost = GalleryStateScene.LostRelay(now);

        Assert.True(lost.IsSharing);
        Assert.Equal(2, lost.Members.Count);
        Assert.Equal(now.AddSeconds(-95), lost.StaleSince);
        Assert.Equal(GroupStatus.LastHeard, lost.Status!.Code);
        var cause = Assert.IsType<TarkovCompanion.Core.Common.Phrase>(lost.Status.Arguments[0]);
        Assert.Equal(GroupStatus.ServerUnreachable, cause.Code);
    }
}
