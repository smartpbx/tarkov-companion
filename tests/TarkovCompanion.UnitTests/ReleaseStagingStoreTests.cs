using TarkovCompanion.Infrastructure.Updates;

namespace TarkovCompanion.UnitTests;

public sealed class ReleaseStagingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-release-staging-{Guid.NewGuid():N}");

    [Fact]
    public void DeleteRemovesOnlyTheOwnedGeneratedDirectory()
    {
        var store = new FileSystemReleaseStagingStore();
        var staging = store.Create(_root);
        File.WriteAllText(Path.Combine(staging, "artifact.bin"), "verified bytes");
        File.WriteAllText(Path.Combine(_root, "caller-owned.txt"), "keep");

        store.Delete(_root, staging);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "caller-owned.txt")));
    }

    [Fact]
    public void DeleteRefusesAPathNotCreatedDirectlyUnderTheConfiguredRoot()
    {
        var store = new FileSystemReleaseStagingStore();
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "caller-owned");
        try
        {
            Assert.Throws<InvalidOperationException>(() => store.Delete(_root, outside));
            Assert.Equal("caller-owned", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
