using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.V2.Setup;

/// <summary>
/// Hides the part of a file path that names a person, until somebody chooses to show it.
/// </summary>
/// <remarks>
/// Setup used to print the screenshot folder, the log folder and the database path in the clear:
/// <c>C:\Users\&lt;their name&gt;\Documents\Escape from Tarkov\Screenshots</c>. Those lines end up in
/// screenshots pasted into Discord for support, which is the moment the user name leaves the machine.
/// A masked path keeps what is useful for finding a folder (where it hangs off, and its last two
/// segments) and drops the middle, so "is it the right folder" is still answerable without the name.
/// </remarks>
public static partial class SetupPathMask
{
    private const string UserProfileToken = "%USERPROFILE%";

    /// <summary>Masks every absolute path inside <paramref name="text"/> and leaves the rest of it alone.</summary>
    /// <param name="text">A line that may hold paths: "Screenshots C:\Users\x\Shots · logs D:\Logs".</param>
    /// <param name="userProfile">The profile folder to name as <c>%USERPROFILE%</c>; the current user's when omitted.</param>
    public static string Mask(string? text, string? userProfile = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        return AbsolutePath().Replace(text, match => MaskPath(match.Value, profile));
    }

    /// <summary>Whether <paramref name="text"/> holds a path that <see cref="Mask"/> would change.</summary>
    public static bool ContainsPath(string? text) => !string.IsNullOrEmpty(text) && AbsolutePath().IsMatch(text);

    private static string MaskPath(string path, string profile)
    {
        var trimmed = path.TrimEnd();
        var tail = path[trimmed.Length..];
        var separator = trimmed.Contains('\\') ? '\\' : '/';
        var segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        string root;
        int skip;
        if (profile.Length > 0 &&
            trimmed.StartsWith(profile, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == profile.Length || trimmed[profile.Length] is '\\' or '/'))
        {
            root = UserProfileToken;
            skip = profile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
        }
        else if (segments.Length > 0 && segments[0].EndsWith(':'))
        {
            root = segments[0];
            skip = 1;
        }
        else
        {
            root = string.Empty;
            skip = 0;
        }

        var below = segments[Math.Min(skip, segments.Length)..];
        // Two or fewer segments below the root say nothing about who owns them: keep them whole.
        var kept = below.Length <= 2
            ? string.Join(separator, below)
            : $"…{separator}{string.Join(separator, below[^2..])}";
        var head = root.Length > 0 ? root + separator : separator.ToString();
        return kept.Length == 0 ? root + tail : head + kept + tail;
    }

    // A drive-letter path, a UNC path, or a rooted path with at least two segments (so "and/or" and a
    // date are not paths, and neither is the "//" in a web address). It runs to the next " · " (how
    // Setup separates two paths in one line), a line break, or the end.
    [GeneratedRegex(@"(?:(?<!\w)[A-Za-z]:[\\/]|\\\\[^\\\s·|]+\\|(?<![\w:./])/(?:[^/\s·|]+/)+)[^\r\n·|]*", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();
}
