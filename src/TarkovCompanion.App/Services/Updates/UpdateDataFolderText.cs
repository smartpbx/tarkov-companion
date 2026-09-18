namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// The sentence on the Updates section saying where the player's data is.
/// </summary>
/// <remarks>
/// Every portable zip started with an empty data folder, because a portable build keeps its
/// data beside its executable and each zip was extracted somewhere new. An installed build
/// keeps it under LocalAppData in a folder the installer does not own, so an update replaces
/// the program and leaves the data alone. This says which of the two is true for the running
/// build, and it is computed from the real paths rather than asserted, so the day the data
/// root ends up inside the folder an update replaces, the page says that instead.
/// </remarks>
public static class UpdateDataFolderText
{
    public static string Describe(string dataRoot, string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        return IsInside(dataRoot, applicationDirectory)
            ? $"Data: {dataRoot} · beside this build, so it stays with this folder"
            : $"Data: {dataRoot} · kept across updates";
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or somewhere under it.</summary>
    public static bool IsInside(string path, string directory)
    {
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
