using TarkovCompanion.App.Services.TestChecklist;

namespace TarkovCompanion.UnitTests.TestChecklist;

public sealed class TestChecklistResultsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-checklist-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_root, "Config", TestChecklistResultsStore.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void A_result_survives_a_new_store_on_the_same_file()
    {
        var tested = new DateTimeOffset(2026, 9, 28, 17, 40, 0, TimeSpan.Zero);
        new TestChecklistResultsStore(FilePath).Set("raid.now", new(TestStatus.NeedsWork, "Too small", "2.0.1400", tested));

        var reread = new TestChecklistResultsStore(FilePath).Get("raid.now");

        Assert.Equal(new TestChecklistResult(TestStatus.NeedsWork, "Too small", "2.0.1400", tested), reread);
        Assert.Contains("\"needs-work\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_from_another_build_keeps_its_status_and_asks_for_a_retest()
    {
        var store = new TestChecklistResultsStore(FilePath);
        store.Set("raid.now", new(TestStatus.Works, null, "2.0.1400", DateTimeOffset.UtcNow));
        store.Set("raid.note-only", new(TestStatus.Untested, "Looked odd", null, null));

        var reread = new TestChecklistResultsStore(FilePath);

        Assert.False(reread.Get("raid.now")!.NeedsRetest("2.0.1400"));
        Assert.True(reread.Get("raid.now")!.NeedsRetest("2.0.1401"));
        Assert.Equal(TestStatus.Works, reread.Get("raid.now")!.Status);
        Assert.False(reread.Get("raid.note-only")!.NeedsRetest("2.0.1401"));
    }

    [Fact]
    public void Clearing_empties_the_file_as_well_as_the_store()
    {
        var store = new TestChecklistResultsStore(FilePath);
        store.Set("raid.now", new(TestStatus.Broken, "x", "b", DateTimeOffset.UtcNow));

        store.Clear();

        Assert.Empty(store.All);
        Assert.Empty(new TestChecklistResultsStore(FilePath).All);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_set_aside_rather_than_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ broken");

        var store = new TestChecklistResultsStore(FilePath);
        store.Set("raid.now", new(TestStatus.Works, null, "b", DateTimeOffset.UtcNow));

        Assert.Single(new TestChecklistResultsStore(FilePath).All);
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(FilePath)!), path => File.ReadAllText(path) == "{ broken");
    }
}
