using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Everything worth knowing about one installation, in a form somebody can paste.
/// </summary>
/// <remarks>
/// Two players in one evening appeared in their group's member list and never on its map, and
/// both times the only way to make progress was for somebody else to notice and start asking
/// questions across Discord. The person with the problem could see nothing, and the person who
/// could read the answer was not at that machine.
///
/// What it carries is chosen to answer that class of question and nothing else: which build,
/// which folders were found, what the sync did, and — the part that actually settles it — the
/// shape of the screenshot names the game is writing.
///
/// What it does not carry is the point. It deliberately omits game logs, the group key, and
/// screenshot pixels. The current field-by-field redaction is not yet a complete outbound
/// allowlist, so diagnostic details can still contain paths, screenshot names, or coordinates;
/// #281 and #310 own the preview and complete-payload filter.
/// </remarks>
public static partial class SupportBundle
{
    /// <summary>How many recent screenshot names to describe.</summary>
    private const int NameSamples = 3;

    /// <summary>How much of the tail of the application's own log to include.</summary>
    private const int LogLines = 120;

    /// <summary>The last line of every bundle, saying what it leaves out and what it can still carry.</summary>
    /// <remarks>
    /// It said "no coordinates" while the log tail above it named screenshots in full, and then
    /// "review before sending" at the end of a report that Report a problem had already sent.
    /// It is true of the body it ends now; change it together with the filtering it describes.
    /// </remarks>
    public const string Footer =
        "Game logs, the group key, and screenshot pixels are excluded. " +
        "The log tail and details above can include folder paths and screenshot coordinates.";

    /// <summary>
    /// Replaces every digit with a zero, keeping every other character exactly.
    /// </summary>
    /// <remarks>
    /// A screenshot's name carries the player's own position, and the position is not what
    /// anybody needs: the *shape* is. Masking the digits keeps every separator, every decimal
    /// point, every bracket and any locale comma or exponent — which is the whole diagnosis —
    /// while the screenshot-names section says nothing about where anybody was standing.
    ///
    /// Only that section. The log tail is the application's own log, whose watcher lines name
    /// each screenshot in full, so the bundle as a whole can still carry coordinates
    /// (RISK-REPORT-REDACTION, owned by #281 and #310).
    /// </remarks>
    public static string MaskDigits(string value) => DigitPattern().Replace(value, "0");

    /// <summary>Builds the text somebody can paste into a conversation.</summary>
    public static string Describe(
        ApplicationRuntimeSnapshot snapshot,
        IReadOnlyList<string> recentScreenshotNames,
        string? logPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recentScreenshotNames);

        var assembly = typeof(SupportBundle).Assembly;
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";

        var report = new StringBuilder();
        report.AppendLine("## Tarkov Companion diagnostics");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"- build: {build}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- os: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- culture: {CultureInfo.CurrentCulture.Name} · numbers use '{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}' as the decimal separator");
        report.AppendLine();

        report.AppendLine("### Watching");
        var observation = snapshot.Observation;
        report.AppendLine(CultureInfo.InvariantCulture, $"- supported here: {observation.IsSupported}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- watching logs: {observation.IsWatchingLogs}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- watching screenshots: {observation.IsWatchingScreenshots}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- detail: {observation.Detail}");
        report.AppendLine();

        report.AppendLine("### Raid");
        report.AppendLine(CultureInfo.InvariantCulture, $"- state: {snapshot.Raid.State}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- map: {snapshot.Raid.MapId ?? "none"}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- has a position: {snapshot.Raid.LastKnownPosition is not null}");
        report.AppendLine();

        // The part that settles it. A name whose shape differs from what the parser expects is
        // invisible from every other angle: the game confirms the screenshot, the folder is
        // right, the file is there, and no position ever appears.
        report.AppendLine("### Screenshot names, digits masked");
        if (recentScreenshotNames.Count == 0)
        {
            report.AppendLine("- none seen yet this session");
        }
        else
        {
            foreach (var name in recentScreenshotNames.Take(NameSamples))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"- `{MaskDigits(name)}`");
            }

            report.AppendLine();
            report.AppendLine("Expected shape: `0000-00-00[00-00]_0.0, 0.0, 0.0_0.0, 0.0, 0.0, 0.0_0.00.png`");
        }

        report.AppendLine();
        report.AppendLine("### Data");
        report.AppendLine(CultureInfo.InvariantCulture, $"- availability: {snapshot.Data.Availability}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- items cached: {snapshot.Data.ItemCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- detail: {snapshot.Data.Detail}");
        report.AppendLine();

        report.AppendLine("### Sharing");
        report.AppendLine(CultureInfo.InvariantCulture, $"- {snapshot.Group.Detail}");
        report.AppendLine(CultureInfo.InvariantCulture, $"- others present: {snapshot.Group.Members.Count}");
        report.AppendLine();

        report.AppendLine("### Log tail");
        report.AppendLine("```");
        foreach (var line in TailOf(logPath))
        {
            report.AppendLine(Redact(line));
        }

        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine(Footer);
        return report.ToString();
    }

    /// <summary>
    /// Replaces a Windows user-folder segment and a group key in one log line.
    /// </summary>
    /// <remarks>
    /// A user's own folder name is a real name often enough to matter, and a group key in a log
    /// line would be the one secret protecting a room. Both are replaced rather than the line
    /// being dropped, because a redacted line still says what happened.
    ///
    /// That is two of the things SAFETY.md keeps out of a report, not all of them: other paths,
    /// screenshot names and the coordinates in them pass through unchanged.
    /// </remarks>
    public static string Redact(string line)
    {
        var redacted = UserPathPattern().Replace(line, @"$1\<user>");
        return GroupKeyPattern().Replace(redacted, "$1<redacted>");
    }

    private static IReadOnlyList<string> TailOf(string? logPath)
    {
        if (logPath is null || !File.Exists(logPath))
        {
            return ["(no log file)"];
        }

        try
        {
            return [.. File.ReadLines(logPath).TakeLast(LogLines)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ["(the log could not be read)"];
        }
    }

    [GeneratedRegex(@"\d", RegexOptions.CultureInvariant)]
    private static partial Regex DigitPattern();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users)\\[^\\""\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserPathPattern();

    [GeneratedRegex(@"(?i)(X-Group-Key:\s*|""key""\s*:\s*"")[^""\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex GroupKeyPattern();
}
