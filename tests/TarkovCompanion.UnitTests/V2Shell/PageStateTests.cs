using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Infrastructure.Workspaces;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>[#902 P8] One key per page, read back after a restart, defaults never written.</summary>
public sealed class PageStateTests : IDisposable
{
    private readonly LayoutFile _file = new();

    private IWorkspaceLayoutStore Restart() => _file.Restart();

    public void Dispose() => _file.Dispose();

    [Fact]
    public void Fields_survive_a_restart_and_a_default_is_removed_rather_than_written()
    {
        var page = new PageState(Restart(), WorkspaceLayoutKeys.PageKeys);
        page.SetEnum("filter", DayOfWeek.Friday, DayOfWeek.Sunday);
        page.SetBool("ready-now", true, false);
        page.SetInt("class", 4, 0);

        var after = new PageState(Restart(), WorkspaceLayoutKeys.PageKeys);
        Assert.Equal(DayOfWeek.Friday, after.Enum("filter", DayOfWeek.Sunday));
        Assert.True(after.Bool("ready-now", false));
        Assert.Equal(4, after.Int("class", 0));

        after.SetEnum("filter", DayOfWeek.Sunday, DayOfWeek.Sunday);
        after.SetBool("ready-now", false, false);
        after.SetInt("class", 0, 0);
        Assert.Equal(string.Empty, Restart().Get(WorkspaceLayoutKeys.PageKeys));
    }

    [Fact]
    public void Two_owners_of_one_page_key_keep_each_others_fields()
    {
        var store = Restart();
        var risk = new PageState(store, WorkspaceLayoutKeys.PageLoot);
        var verdict = new PageState(store, WorkspaceLayoutKeys.PageLoot);

        risk.Set("risk", "High");
        verdict.Set("verdict", "Swap");

        var after = new PageState(Restart(), WorkspaceLayoutKeys.PageLoot);
        Assert.Equal("High", after.Get("risk"));
        Assert.Equal("Swap", after.Get("verdict"));
    }

    [Fact]
    public void A_value_with_separators_round_trips_and_junk_falls_back_to_defaults()
    {
        var page = new PageState(Restart(), WorkspaceLayoutKeys.PageDebrief);
        page.Set("tag", "loot=good; night");

        Assert.Equal("loot=good; night", new PageState(Restart(), WorkspaceLayoutKeys.PageDebrief).Get("tag"));
        Assert.Empty(PageState.Parse("=x;;novalue"));
        var junk = new PageState(new MemoryStore { [WorkspaceLayoutKeys.PageAmmo] = "sort=Nonsense;class=99;flag=maybe;kind=3" }, WorkspaceLayoutKeys.PageAmmo);
        Assert.Equal(DayOfWeek.Monday, junk.Enum("sort", DayOfWeek.Monday));
        Assert.Equal(0, junk.Int("class", 0, 0, 6));
        Assert.True(junk.Bool("flag", true));
        Assert.Equal(DayOfWeek.Monday, junk.Enum("kind", DayOfWeek.Monday));
    }

    private sealed class MemoryStore : Dictionary<string, string>, IWorkspaceLayoutStore
    {
        public string? Get(string key) => TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value) => this[key] = value;
    }
}

/// <summary>A workspace-layout file; each <see cref="Restart"/> reads it into a new store, as a restarted app does.</summary>
internal sealed class LayoutFile : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"page-state-{Guid.NewGuid():N}");

    public LayoutFile() => Directory.CreateDirectory(_directory);

    public IWorkspaceLayoutStore Restart() => new JsonFileWorkspaceLayoutStore(Path.Combine(_directory, "workspace-layout.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
