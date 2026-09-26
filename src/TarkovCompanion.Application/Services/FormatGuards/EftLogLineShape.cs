using System.Text.RegularExpressions;

namespace TarkovCompanion.Application.Services.FormatGuards;

/// <summary>What one line says about the log's format: nothing, "known", or "not a shape we know".</summary>
public enum LineShape
{
    /// <summary>A blank line or the inside of a multi-line entry, which proves nothing either way.</summary>
    Neutral,
    Recognised,
    Unrecognised,
}

/// <summary>Which of the game's log files a line came from, by the file's name.</summary>
public enum EftLogKind
{
    Other,
    Application,
    Backend,
    Output,
}

/// <summary>
/// [#712 0-3] Classifies a log line by shape, for <see cref="FormatHealthMonitor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every parser here ignores a line it does not recognise, on purpose, so a game update that
/// reshapes the lines does not stop anything: it makes raid detection, maps and quests go quiet.
/// This is the check that notices. It does not parse anything the parsers do not already read.
/// </para>
/// <para>
/// Measured on 1.1.5.1.47510 (six sessions, 2026-09-22..25): every non-blank line of
/// <c>application</c> that does not start inside a JSON block opens with the header, and 99.8% of
/// <c>backend</c> (the rest are HTTP error bodies). <c>output</c> is 17% header, because Unity writes
/// stack frames under its errors, so there only a line that begins with a digit is judged: a header
/// that changed shape still begins with its date. All 793 <c>message received:</c> lines in
/// <c>backend</c> fit the notification envelope, and the payload's own <c>type</c> repeated the
/// announced one every time.
/// </para>
/// </remarks>
public static partial class EftLogLineShape
{
    /// <summary>The kind of file, from the word its name ends with ("… application_000.log").</summary>
    public static EftLogKind KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileNameWithoutExtension(path);
        return EndsWithWord(name, "application") ? EftLogKind.Application
            : EndsWithWord(name, "backend") ? EftLogKind.Backend
            : EndsWithWord(name, "output") ? EftLogKind.Output
            : EftLogKind.Other;
    }

    /// <summary>Whether the header is there, not there, or the line cannot say.</summary>
    public static LineShape Header(string? line, EftLogKind kind)
    {
        if (string.IsNullOrWhiteSpace(line) || IsContinuation(line[0]))
        {
            return LineShape.Neutral;
        }

        if (HeaderPattern().IsMatch(line))
        {
            return LineShape.Recognised;
        }

        // Unity's stack frames under an error ("UnityEngine.Debug:Log(Object)") are most of output.
        return kind == EftLogKind.Output && !char.IsAsciiDigit(line[0]) ? LineShape.Neutral : LineShape.Unrecognised;
    }

    /// <summary>The game build in the header's second field ("1.1.5.1.47510"), where the header is there.</summary>
    public static string? HeaderVersion(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = HeaderPattern().Match(line);
        return match.Success ? match.Groups["version"].Value : null;
    }

    /// <summary>
    /// Whether a notification line still has the envelope the raid, quest and flea parsers read.
    /// </summary>
    /// <remarks>
    /// Neutral for every line that is not a websocket message or a notification at all, which is
    /// nearly all of them. Only the envelope is checked, never the payload's fields: those differ
    /// by notification kind and are the parsers' business (and their fixture packs').
    /// </remarks>
    public static LineShape Notification(string? line)
    {
        if (string.IsNullOrEmpty(line)
            || (!line.Contains("message received:", StringComparison.Ordinal)
                && !line.Contains("NOTIFICATION", StringComparison.Ordinal)))
        {
            return LineShape.Neutral;
        }

        var match = EnvelopePattern().Match(line);
        return match.Success && string.Equals(match.Groups["announced"].Value, match.Groups["type"].Value, StringComparison.Ordinal)
            ? LineShape.Recognised
            : LineShape.Unrecognised;
    }

    private static bool IsContinuation(char first) =>
        char.IsWhiteSpace(first) || first is '{' or '}' or '[' or ']' or ',' or '"';

    private static bool EndsWithWord(string name, string word) =>
        name.Length > word.Length
            ? name.Contains(' ' + word + "_", StringComparison.OrdinalIgnoreCase)
              || name.Contains('-' + word + "_", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith(word + "_", StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, word, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}(?: [+-]\d{2}:\d{2})?\|(?<version>\d+(?:\.\d+){2,})\|[A-Za-z]+\|[A-Za-z-]+\|",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(
        @"NOTIFICATION \S+ (?<announced>[A-Za-z_]+) \[\{""type"":""(?<type>[A-Za-z_]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnvelopePattern();
}
