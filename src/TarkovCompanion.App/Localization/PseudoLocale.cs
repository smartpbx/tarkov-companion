using System.Text;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// The "qps-ploc" pseudo-locale: English with every letter accented, about 35% longer, in brackets.
/// </summary>
/// <remarks>
/// A developer's view, never a player's (#314). Three things become visible at a glance: copy that
/// is still hard-coded (it stays plain English beside bracketed text), a label that clips or wraps
/// once a translation runs longer than the English (German and Russian often run a third longer),
/// and a label assembled from fragments (brackets appear mid-sentence). Format placeholders such as
/// {0:N0} are left exactly as they are, or formatting would break.
/// </remarks>
public static class PseudoLocale
{
    public const string Name = "qps-ploc";

    private const double Growth = 0.35;

    private static readonly Dictionary<char, char> Accents = new()
    {
        ['a'] = 'á', ['b'] = 'ƀ', ['c'] = 'ç', ['d'] = 'ď', ['e'] = 'é', ['f'] = 'ƒ', ['g'] = 'ĝ',
        ['h'] = 'ĥ', ['i'] = 'í', ['j'] = 'ĵ', ['k'] = 'ķ', ['l'] = 'ľ', ['m'] = 'ɱ', ['n'] = 'ñ',
        ['o'] = 'ö', ['p'] = 'þ', ['q'] = 'ǫ', ['r'] = 'ŕ', ['s'] = 'š', ['t'] = 'ţ', ['u'] = 'ü',
        ['v'] = 'ṽ', ['w'] = 'ŵ', ['x'] = 'ẋ', ['y'] = 'ý', ['z'] = 'ž',
        ['A'] = 'Á', ['B'] = 'Ɓ', ['C'] = 'Ç', ['D'] = 'Ď', ['E'] = 'É', ['F'] = 'Ƒ', ['G'] = 'Ĝ',
        ['H'] = 'Ĥ', ['I'] = 'Í', ['J'] = 'Ĵ', ['K'] = 'Ķ', ['L'] = 'Ľ', ['M'] = 'Ṁ', ['N'] = 'Ñ',
        ['O'] = 'Ö', ['P'] = 'Þ', ['Q'] = 'Ǫ', ['R'] = 'Ŕ', ['S'] = 'Š', ['T'] = 'Ţ', ['U'] = 'Ü',
        ['V'] = 'Ṽ', ['W'] = 'Ŵ', ['X'] = 'Ẋ', ['Y'] = 'Ý', ['Z'] = 'Ž',
    };

    public static bool Is(string? cultureName) =>
        string.Equals(cultureName, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Accents, lengthens and brackets one piece of copy, leaving {placeholders} alone.</summary>
    public static string Transform(string english)
    {
        ArgumentNullException.ThrowIfNull(english);
        var builder = new StringBuilder(english.Length * 2 + 4);
        builder.Append('[');
        var letters = 0;
        for (var index = 0; index < english.Length; index++)
        {
            var character = english[index];
            if (character is '{' or '}' && index + 1 < english.Length && english[index + 1] == character)
            {
                // An escaped brace is a literal, not a placeholder.
                builder.Append(character).Append(character);
                index++;
                continue;
            }

            if (character == '{')
            {
                var close = english.IndexOf('}', index);
                if (close > index)
                {
                    builder.Append(english, index, close - index + 1);
                    index = close;
                    continue;
                }
            }

            builder.Append(Accents.TryGetValue(character, out var accented) ? accented : character);
            letters++;
        }

        var padding = (int)Math.Ceiling(letters * Growth);
        if (padding > 0)
        {
            builder.Append(' ');
            for (var index = 1; index < padding; index++)
            {
                builder.Append('·');
            }
        }

        return builder.Append(']').ToString();
    }

    /// <summary>The whole English table, transformed.</summary>
    public static IReadOnlyDictionary<string, UiString> Of(IReadOnlyDictionary<string, UiString> english) =>
        english.ToDictionary(
            entry => entry.Key,
            entry => new UiString(Transform(entry.Value.Other), entry.Value.One is { } one ? Transform(one) : null),
            StringComparer.Ordinal);
}
