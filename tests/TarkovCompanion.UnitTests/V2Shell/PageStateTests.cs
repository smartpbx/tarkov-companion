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

    /// <summary>[#935] A replacement on the page's own thread re-reads at once; from another thread it is posted to the page's context.</summary>
    [Fact]
    public void A_replaced_layout_is_read_again_on_the_thread_that_owns_the_page()
    {
        var store = Restart();
        var context = new RecordingContext();
        var reads = 0;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            WorkspaceLayoutReplaced.Reread(store, () => reads++);
            store.Replace(new Dictionary<string, string>());
            Assert.Equal(1, reads);
            Assert.Equal(0, context.Posts);

            // Joined, not awaited: this context never runs what is posted to it.
            var other = new Thread(() => store.Replace(new Dictionary<string, string>()));
            other.Start();
            other.Join();
            Assert.Equal(1, reads);
            Assert.Equal(1, context.Posts);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>[#935] A field set to what is already stored writes nothing, not even an empty entry.</summary>
    [Fact]
    public void Setting_what_is_stored_writes_nothing()
    {
        var store = new MemoryStore();
        var page = new PageState(store, WorkspaceLayoutKeys.PageKeys);

        page.SetEnum("filter", DayOfWeek.Sunday, DayOfWeek.Sunday);

        Assert.Empty(store);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => Posts++;
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
