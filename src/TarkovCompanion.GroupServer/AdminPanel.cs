using System.Reflection;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The operator's page, as one embedded file.
/// </summary>
/// <remarks>
/// Embedded and read once, for the same reasons as <see cref="Tablet"/>: this server is
/// deliberately the sort of thing a group runs on whatever they have lying around, and a
/// deployment that is one file stays one file.
/// </remarks>
public static class AdminPanel
{
    private const string ResourceName = "TarkovCompanion.GroupServer.Admin.index.html";

    public static string Page { get; } = Read();

    private static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded page '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
