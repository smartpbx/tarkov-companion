using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovCompanion.Application.Services.Ask;

/// <summary>
/// The small grammar the Ask box reads questions with (#712 T8, decision 3: rules first).
/// </summary>
/// <remarks>
/// Deterministic on purpose. A question either fits one of these shapes and gets an answer from the
/// app's own data, or it does not and the box says so; nothing here guesses what an unfamiliar
/// sentence might mean. The shapes are the ones the epic lists and Clayton's examples use. The
/// order matters where two could fit: "best extract" is an extract question, not an ammunition one,
/// and "what do I need for X" is a needs question even when X is an item.
/// </remarks>
public static partial class AskGrammar
{
    private static readonly string[] QuestionStarts =
    [
        "what", "where", "which", "who", "how", "is", "are", "do", "does", "can", "best", "should", "nearest", "closest",
    ];

    /// <summary>Whether the palette should treat what was typed as a question rather than a command search.</summary>
    /// <remarks>
    /// A leading or trailing question mark always counts; so does a question word followed by more
    /// words ("theme" is a setting, "what is theme" is a question), and any text the grammar parses.
    /// </remarks>
    public static bool LooksLikeQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('?') || trimmed.EndsWith('?'))
        {
            return trimmed.Trim('?').Trim().Length > 0;
        }

        var words = Clean(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (words.Length >= 2 && QuestionStarts.Contains(words[0], StringComparer.Ordinal)) || Parse(trimmed) is not null;
    }

    /// <summary>The question's intent and subject, or null when it fits no shape.</summary>
    public static AskQuestion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = Clean(text);
        if (clean.Length == 0)
        {
            return null;
        }

        if (Extract().IsMatch(clean))
        {
            return new(text.Trim(), AskIntent.BestExtract, string.Empty);
        }

        if (ParseAmmo(text.Trim(), clean) is { } ammo)
        {
            return ammo;
        }

        foreach (var (pattern, intent) in Shapes)
        {
            var match = pattern.Match(clean);
            if (match.Success && Subject(match.Groups["s"].Value) is { Length: > 0 } subject)
            {
                return new(text.Trim(), intent, subject);
            }
        }

        return null;
    }

    /// <summary>
    /// The shapes, most specific first. Every one captures its subject as <c>s</c>.
    /// </summary>
    private static readonly (Regex Pattern, AskIntent Intent)[] Shapes =
    [
        // Where an item comes from.
        (new(@"^(?:where|how)\s+(?:can|do|should)\s+i\s+(?:get|buy|find|obtain|craft|make)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemSources),
        (new(@"^(?:where|how)\s+to\s+(?:get|buy|find|obtain|craft|make)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemSources),
        (new(@"^who\s+(?:sells|has|trades)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemSources),
        (new(@"^(?:buy|get|craft|barter|barters|crafts|trader|traders)\s+(?:for\s+)?(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemSources),
        (new(@"^(?:is|are)\s+(?<s>.+?)\s+(?:sold|craftable|barterable)(?:\s+anywhere)?$", RegexOptions.CultureInvariant), AskIntent.ItemSources),

        // What an item is used for.
        (new(@"^where\s+(?:is|are)\s+(?:the\s+)?(?<s>.+?)\s+(?:used|needed)(?:\s+for)?$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^what\s+(?:is|are)\s+(?:the\s+)?(?<s>.+?)\s+(?:used|needed|for)(?:\s+for)?$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^(?:is|are)\s+(?<s>.+?)\s+(?:needed|used|required|worth keeping)(?:\s+(?:for|in)\s+(?:anything|something|any\s+quests?|quests?|hideout))?(?:\s+anywhere)?$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^(?:what|where)\s+does\s+(?:the\s+)?(?<s>.+?)\s+(?:open|unlock|go)$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^(?:do|should)\s+i\s+(?:need|keep)\s+(?:the\s+)?(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^(?:uses\s+(?:of|for)|used\s+for)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.ItemUses),
        (new(@"^(?<s>.+?)\s+(?:uses|used for|used where)$", RegexOptions.CultureInvariant), AskIntent.ItemUses),

        // What a quest or a hideout level needs.
        (new(@"^(?:what|which)\s+(?:items\s+)?(?:do|does)\s+(?:i|you|it)\s+need\s+(?:for|to\s+(?:do|finish|complete|build|upgrade))\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.Needs),
        (new(@"^what\s+(?:is|are)\s+(?:needed|required)\s+for\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.Needs),
        (new(@"^what\s+(?:does|do)\s+(?<s>.+?)\s+(?:need|require|ask for)$", RegexOptions.CultureInvariant), AskIntent.Needs),
        (new(@"^how\s+(?:do\s+i|to)\s+(?:do|finish|complete|build|upgrade)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.Needs),
        (new(@"^(?:requirements|reqs|needs|need)\s+(?:for|of)\s+(?<s>.+)$", RegexOptions.CultureInvariant), AskIntent.Needs),
        (new(@"^(?<s>.+?)\s+(?:requirements|reqs|needs)$", RegexOptions.CultureInvariant), AskIntent.Needs),
    ];

    private static AskQuestion? ParseAmmo(string text, string clean)
    {
        var caliber = Caliber().Match(clean);
        var armor = ArmorClass().Match(clean);
        var mentionsAmmo = AmmoWord().IsMatch(clean);
        if (!caliber.Success || !(mentionsAmmo || armor.Success || BestWord().IsMatch(clean) || PriceCap().IsMatch(clean)))
        {
            return null;
        }

        int? armorClass = armor.Success &&
            int.TryParse(armor.Groups["c"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedClass) &&
            parsedClass is >= 1 and <= 6
                ? parsedClass
                : null;
        return new(text, AskIntent.Ammo, caliber.Groups["c"].Value.Replace(" ", string.Empty, StringComparison.Ordinal), armorClass, Cap(clean));
    }

    /// <summary>"under 1k", "below ₽800", "< 1.5k": roubles per round.</summary>
    private static long? Cap(string clean)
    {
        var match = PriceCap().Match(clean);
        if (!match.Success ||
            !decimal.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var scale = match.Groups["u"].Value switch
        {
            "k" => 1_000m,
            "m" => 1_000_000m,
            _ => 1m,
        };
        return (long)Math.Round(number * scale, MidpointRounding.AwayFromZero);
    }

    /// <summary>The subject without the articles, filler and punctuation a question wraps it in.</summary>
    private static string Subject(string raw)
    {
        var subject = raw.Trim();
        subject = LeadingFiller().Replace(subject, string.Empty);
        subject = TrailingFiller().Replace(subject, string.Empty);
        return subject.Trim();
    }

    /// <summary>Lower case, "×" as "x", question marks and quotes gone, one space between words.</summary>
    /// <remarks>Dots and commas stay: "5.45" and "7.62x39" are names, not punctuation.</remarks>
    internal static string Clean(string text)
    {
        var lowered = text.ToLowerInvariant().Replace('×', 'x').Replace('₽', ' ').Replace('’', '\'');
        lowered = Contraction().Replace(lowered, "$1 is");
        lowered = Punctuation().Replace(lowered, " ");
        return Spaces().Replace(lowered, " ").Trim();
    }

    [GeneratedRegex(@"\b(?:extract|extracts|exfil|exfils|extraction|way\s+out|get\s+out|exit|exits)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Extract();

    // "5.45", "5.45x39", "7.62x39", "9x19", "12/70", ".366", "12 gauge", "366 tkm", "4.6x30".
    [GeneratedRegex(@"(?<c>\b\d{1,2}[.,]\d{1,2}(?:\s*x\s*\d{2,3}(?:r|mm)?)?\b|\b\d{1,2}\s*x\s*\d{2,3}(?:r|mm)?\b|\b12\s*/\s*70\b|\b20\s*/\s*70\b|\b23\s*x\s*75\b|\.\d{3}\b|\b\d{2}\s*(?:ga|gauge)\b|\b366\b|\b338\b|\b300\s*blk\b|\b300\b)", RegexOptions.CultureInvariant)]
    private static partial Regex Caliber();

    [GeneratedRegex(@"\b(?:class|level|lvl|tier|armou?r\s+class|vs\s+class|vs)\s*(?<c>[1-6])\b|\bc(?<c>[1-6])\b|\b(?<c>[1-6])\s+armou?r\b", RegexOptions.CultureInvariant)]
    private static partial Regex ArmorClass();

    [GeneratedRegex(@"\b(?:ammo|ammunition|round|rounds|bullet|bullets|cartridge|cartridges|shells?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmmoWord();

    [GeneratedRegex(@"\b(?:best|good|top|strongest|cheap|cheapest)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BestWord();

    [GeneratedRegex(@"(?:\bunder|\bbelow|\bless\s+than|\bcheaper\s+than|<|\bmax)\s*(?:rub\s*)?(?<n>\d+(?:[.,]\d+)?)\s*(?<u>k|m)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex PriceCap();

    [GeneratedRegex(@"^(?:(?:a|an|the|my|any|some|this|that)\s+)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingFiller();

    [GeneratedRegex(@"(?:\s+(?:for\s+anything|for\s+something|anywhere|at\s+all|please|now|quest|task))+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFiller();

    [GeneratedRegex(@"[?!""“”'’`;:()\[\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(what|where|who|how|which)'s\b", RegexOptions.CultureInvariant)]
    private static partial Regex Contraction();
}
