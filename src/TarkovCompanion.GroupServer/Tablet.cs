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
    private const string RelayCryptoResourceName = "TarkovCompanion.GroupServer.Tablet.relay-crypto.js";

    public static string Page { get; } = Read(ResourceName);

    /// <summary>
    /// The relay-frame sealing/opening this page's live sync uses (v2r-tablet-marks-sync), served
    /// separately so it stays plain, `require`-able JavaScript for Node tests
    /// (<c>scripts/test-relay-crypto.mjs</c>) rather than text embedded inside <see cref="Page"/>.
    /// </summary>
    public static string RelayCryptoScript { get; } = Read(RelayCryptoResourceName);

    private static string Read(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded page '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
