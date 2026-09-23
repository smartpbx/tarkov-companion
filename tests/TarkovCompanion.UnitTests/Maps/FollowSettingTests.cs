using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.Maps;

public sealed class FollowSettingTests
{
    [Fact]
    public void Follow_choice_survives_restart_and_automatic_map_fits()
    {
        var layout = new MemoryLayout();
        using var first = Map(layout);

        Assert.True(first.FollowsPlayer);
        first.ToggleFollowPlayer();
        Assert.Equal("off", layout.Get(WorkspaceLayoutKeys.RaidFollow));

        // Map/artwork switches fit their new bounds. That is not a new player choice.
        first.RequestFit();
        Assert.False(first.FollowsPlayer);

        using var restarted = Map(layout);
        Assert.False(restarted.FollowsPlayer);
        restarted.ToggleFollowPlayer();
        restarted.RequestFit();

        Assert.True(restarted.FollowsPlayer);
        Assert.Equal("on", layout.Get(WorkspaceLayoutKeys.RaidFollow));
    }

    [Fact]
    public void A_manual_pan_turns_follow_off_and_remembers_it()
    {
        var layout = new MemoryLayout();
        using var map = Map(layout);

        map.ReportManualPan();

        Assert.False(map.FollowsPlayer);
        Assert.Equal("off", layout.Get(WorkspaceLayoutKeys.RaidFollow));
        using var nextRaid = Map(layout);
        Assert.False(nextRaid.FollowsPlayer);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("ON", true)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void Stored_values_are_bounded_to_the_two_supported_choices(string? stored, bool expected)
    {
        var layout = new MemoryLayout();
        if (stored is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidFollow, stored);
        }

        Assert.Equal(expected, new FollowSetting(layout).Value);
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
