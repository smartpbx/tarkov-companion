using System.Reflection;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The second screen, as one embedded page.
/// </summary>
/// <remarks>
/// Embedded rather than copied beside the binary. A server deployed as a single file and a page
/// that has to sit next to it is a deployment that works until somebody moves the file, and
/// this server is deliberately the sort of thing a group runs on whatever they have lying
/// around.
///
/// Read once and held. It is fourteen kilobytes and it never changes while the process is
/// running, so reading it per request would be a stream and a string allocation to produce the
/// same bytes.
/// </remarks>
public static class Tablet
{
    private const string ResourceName = "TarkovCompanion.GroupServer.Tablet.index.html";

    public static string Page { get; } = Read();

    private static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded page '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
