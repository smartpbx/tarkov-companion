using System.Text;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// Reads the updater's own log for why the last apply failed.
/// </summary>
/// <remarks>
/// <c>Update.exe</c> writes to <c>%LOCALAPPDATA%\velopack\velopack_&lt;appId&gt;.log</c>, not to
/// anything of this application's, and it is the only place the reason is ever recorded. Only the
/// tail is read: the file grows for as long as the application is installed.
/// </remarks>
public static class VelopackApplyLog
{
    private const string Marker = "Apply error:";
    private const int TailBytes = 256 * 1024;
    private const int ReasonLimit = 200;

    /// <summary>Where the updater logs for this application, or null off Windows.</summary>
    public static string? PathFor(string appId)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(appId)
            ? null
            : Path.Combine(local, "velopack", $"velopack_{appId}.log");
    }

    /// <summary>The last "Apply error" in the log at <paramref name="path"/>, or null.</summary>
    public static string? ReadLastApplyError(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // Shared for writing too: the updater may be appending to it right now.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return LastApplyError(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The reason on the last "Apply error" line of <paramref name="log"/>, or null.</summary>
    public static string? LastApplyError(string? log)
    {
        if (string.IsNullOrEmpty(log))
        {
            return null;
        }

        var at = log.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + Marker.Length;
        var end = log.IndexOfAny(['\r', '\n'], start);
        var reason = (end < 0 ? log[start..] : log[start..end]).Trim();
        if (reason.Length == 0)
        {
            return null;
        }

        return reason.Length <= ReasonLimit ? reason : reason[..ReasonLimit].TrimEnd() + "…";
    }
}
