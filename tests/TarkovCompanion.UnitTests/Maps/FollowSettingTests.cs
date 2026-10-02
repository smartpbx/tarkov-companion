using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.Maps;

/// <summary>[#992] Follow is saved per map, only by the Follow control; a drag pauses it.</summary>
public sealed class FollowSettingTests
{
    [Fact]
    public void Follow_choice_survives_restart_and_automatic_map_fits()
    {
        var layout = new MemoryLayout();
        using var first = Map(layout);

        Assert.False(first.FollowsPlayer);
        first.ToggleFollowPlayer();
        Assert.Equal("*:on", layout.Get(WorkspaceLayoutKeys.RaidFollow));

        // Map/artwork switches fit their new bounds. That is not a new player choice.
        first.RequestFit();
        Assert.True(first.FollowsPlayer);

        using var restarted = Map(layout);
        Assert.True(restarted.FollowsPlayer);
    }

    [Fact]
    public void A_manual_pan_pauses_follow_and_saves_nothing()
    {
        var layout = new MemoryLayout();
        using var map = Map(layout);
        map.ToggleFollowPlayer();
        var saved = layout.Get(WorkspaceLayoutKeys.RaidFollow);

        map.ReportManualPan();
        map.SetZoom(2);

        Assert.True(map.FollowsPlayer);
        Assert.True(map.IsFollowPaused);
        Assert.False(map.IsFollowingNow);
        Assert.Equal(saved, layout.Get(WorkspaceLayoutKeys.RaidFollow));

        // A tap on "Follow paused" resumes; it does not turn Follow off.
        map.ToggleFollowPlayer();
        Assert.True(map.IsFollowingNow);
        Assert.Equal(saved, layout.Get(WorkspaceLayoutKeys.RaidFollow));

        using var nextRaid = Map(layout);
        Assert.True(nextRaid.IsFollowingNow);
    }

    [Fact]
    public void A_pan_with_follow_off_does_not_pause_anything()
    {
        using var map = Map(new MemoryLayout());
        map.ReportManualPan();
        Assert.False(map.IsFollowPaused);
    }

    [Theory]
    [InlineData(null, "woods", false)]
    [InlineData("", "woods", false)]
    [InlineData("on", "woods", true)] // the old single value: the answer for maps with no entry
    [InlineData("ON", "woods", true)]
    [InlineData("off", "woods", false)]
    [InlineData("woods:on", "woods", true)]
    [InlineData("woods:on", "customs", false)]
    [InlineData("*:on,customs:off", "customs", false)]
    [InlineData("*:on,customs:off", "woods", true)]
    [InlineData("Woods:on", "woods", true)]
    [InlineData("woods:maybe", "woods", false)]
    public void Stored_values_are_read_per_map(string? stored, string map, bool expected)
    {
        var layout = new MemoryLayout();
        if (stored is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidFollow, stored);
        }

        Assert.Equal(expected, new FollowSetting(layout).For(map));
    }

    [Fact]
    public void Setting_one_map_keeps_the_others_and_the_old_value()
    {
        var layout = new MemoryLayout();
        layout.Set(WorkspaceLayoutKeys.RaidFollow, "on");
        var setting = new FollowSetting(layout);

        setting.Set("customs", false);
        setting.Set("woods", true);

        Assert.Equal("*:on,customs:off,woods:on", layout.Get(WorkspaceLayoutKeys.RaidFollow));
        var reread = new FollowSetting(layout);
        Assert.False(reread.For("customs"));
        Assert.True(reread.For("woods"));
        Assert.True(reread.For("shoreline"));
    }

    private static MapViewModel Map(IWorkspaceLayoutStore layout) => new(
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        layout: layout);

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
