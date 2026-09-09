using System.Globalization;
using System.Text;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class OcrTextNormalizer
{
    private static readonly HashSet<char> LookupConfusions = ['@', '$', '|', '!'];

    public string NormalizeForLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var token = new StringBuilder();

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character) || LookupConfusions.Contains(character))
            {
                token.Append(character);
                continue;
            }

            AppendLookupToken(result, token);
        }

        AppendLookupToken(result, token);
        return result.ToString();
    }

    public string NormalizeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                result.Append(character);
            }
            else
            {
                result.Append(character switch
                {
                    'o' or 'O' => '0',
                    'i' or 'I' or 'l' or 'L' or '|' or '!' => '1',
                    's' or 'S' => '5',
                    _ => string.Empty,
                });
            }
        }

        return result.ToString();
    }

    private static void AppendLookupToken(StringBuilder result, StringBuilder token)
    {
        if (token.Length == 0)
        {
            return;
        }

        var containsLetters = token.ToString().Any(char.IsLetter);
        if (result.Length > 0)
        {
            result.Append(' ');
        }

        var rawToken = token.ToString();
        for (var index = 0; index < rawToken.Length; index++)
        {
            var character = rawToken[index];
            var normalized = char.ToLowerInvariant(character);
            if (containsLetters)
            {
                normalized = normalized switch
                {
                    '0' => 'o',
                    '1' or '|' or '!' => 'i',
                    'l' when character == 'l' &&
                                  index > 0 &&
                                  index < rawToken.Length - 1 &&
                                  char.IsUpper(rawToken[index - 1]) &&
                                  char.IsUpper(rawToken[index + 1]) => 'i',
                    '5' or '$' => 's',
                    '8' => 'b',
                    '@' => 'a',
                    _ => normalized,
                };
            }

            if (char.IsLetterOrDigit(normalized))
            {
                result.Append(normalized);
            }
        }

        token.Clear();
    }
}
