using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

public sealed class AppDataPathsTests
{
    /// <summary>
    /// The player's data must never live inside the directory the installer owns.
    /// </summary>
    /// <remarks>
    /// This is here because it happened. The installer was told to use the pack id
    /// "TarkovCompanion", which makes its install directory %LOCALAPPDATA%\TarkovCompanion,
    /// and that is exactly where this type puts the database, cache, config and logs. An
    /// install therefore renamed a 114 MB database aside to make room for the program, and
    /// only because that install then failed was the rename target still there to recover
    /// from. A successful one would have been worse, and silent.
    ///
    /// The fix was to give the installer a different directory, so this asserts the property
    /// that fix relies on rather than the fix itself: whatever either side is called, the data
    /// root must not sit under the running application.
    /// </remarks>
    [Fact]
    public void TheDataRootIsNotInsideTheInstalledApplication()
    {
        var paths = AppDataPaths.Resolve();
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));

        Assert.False(
            root.Equals(installed, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"The data root {root} is inside the application directory {installed}; an update would destroy it.");
    }

    [Fact]
    public void EveryFolderSitsUnderTheRoot()
    {
        var paths = AppDataPaths.Resolve();
        var root = Path.GetFullPath(paths.Root);

        foreach (var folder in new[] { paths.Database, paths.Cache, paths.Logs, paths.Config, paths.DebugCaptures, paths.Support })
        {
            Assert.StartsWith(root, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        }
    }
}
