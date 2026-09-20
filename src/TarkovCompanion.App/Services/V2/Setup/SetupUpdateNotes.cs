using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.V2.Setup;

/// <summary>Turns a release's markdown notes into the plain lines Setup shows.</summary>
public static partial class SetupUpdateNotes
{
    /// <summary>Notes past this many characters are cut at a line, with an ellipsis.</summary>
    public const int MaximumLength = 1_200;

    public static string Plain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.ReplaceLineEndings("\n").Split('\n')
            .Select(line => Markup().Replace(line, "$1").Trim())
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? "• " + line[2..] : line);
        var text = string.Join('\n', lines).Trim();
        text = BlankRuns().Replace(text, "\n\n");
        if (text.Length <= MaximumLength)
        {
            return text;
        }

        var cut = text.LastIndexOf('\n', MaximumLength);
        return text[..(cut > 0 ? cut : MaximumLength)].TrimEnd() + "\n…";
    }

    // Heading marks, emphasis marks, backticks, and link syntax reduced to its text.
    [GeneratedRegex(@"^#{1,6}\s*|\*\*|__|`|\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankRuns();
}
