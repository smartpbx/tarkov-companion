using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class FollowZoomSettingTests
{
    [Theory]
    [InlineData(double.NaN, FollowZoomSetting.Default)]
    [InlineData(double.NegativeInfinity, FollowZoomSetting.Default)]
    [InlineData(-10, FollowZoomSetting.Minimum)]
    [InlineData(99, FollowZoomSetting.Maximum)]
    [InlineData(4.25, 4.25)]
    public void Clamp_rejects_non_finite_values_and_bounds_the_camera(double value, double expected) =>
        Assert.Equal(expected, FollowZoomSetting.Clamp(value));

    [Fact]
    public void A_change_is_clamped_persisted_and_loaded_by_the_next_cockpit()
    {
        var layout = new MemoryLayout();
        var first = new FollowZoomSetting(layout);

        Assert.Equal(3, first.Value);
        Assert.Equal(3.5, first.ChangeBy(1));
        Assert.Equal("3.5", layout.Get(WorkspaceLayoutKeys.RaidFollowZoom));

        first.Set(50);
        var restarted = new FollowZoomSetting(layout);

        Assert.Equal(FollowZoomSetting.Maximum, restarted.Value);
        Assert.Equal("8", layout.Get(WorkspaceLayoutKeys.RaidFollowZoom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    public void Invalid_stored_values_use_the_default(string? stored)
    {
        var layout = new MemoryLayout();
        if (stored is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidFollowZoom, stored);
        }

        Assert.Equal(FollowZoomSetting.Default, new FollowZoomSetting(layout).Value);
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
