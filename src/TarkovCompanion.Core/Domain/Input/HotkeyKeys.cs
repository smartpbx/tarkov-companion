using System.Diagnostics.CodeAnalysis;

namespace TarkovCompanion.Core.Domain.Input;

/// <summary>
/// The keys a global shortcut may use, and their platform key codes.
/// </summary>
/// <remarks>
/// The codes are Windows virtual-key values, but this is a lookup table rather than a
/// platform integration: no operating-system call is made here, so the domain stays testable
/// and buildable everywhere.
/// </remarks>
public static class HotkeyKeys
{
    private static readonly Dictionary<string, uint> Codes = Build();

    /// <summary>Canonical key names in presentation order.</summary>
    public static IReadOnlyList<string> Names { get; } = Codes.Keys.ToArray();

    public static bool TryGetVirtualKey(string? key, out uint virtualKey)
    {
        virtualKey = 0;
        return !string.IsNullOrWhiteSpace(key) && Codes.TryGetValue(key.Trim(), out virtualKey);
    }

    public static bool TryNormalize(string? key, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var candidate = key.Trim();
        foreach (var name in Codes.Keys)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                normalized = name;
                return true;
            }
        }

        return false;
    }

    public static bool IsFunctionKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        key.Length is 2 or 3 &&
        (key[0] is 'F' or 'f') &&
        int.TryParse(key.AsSpan(1), out var number) &&
        number is >= 1 and <= 24;

    private static Dictionary<string, uint> Build()
    {
        var codes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        for (var number = 1; number <= 24; number++)
        {
            codes[$"F{number}"] = (uint)(0x70 + number - 1);
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            codes[letter.ToString()] = letter;
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            codes[digit.ToString()] = (uint)(0x30 + digit);
            codes[$"NumPad{digit}"] = (uint)(0x60 + digit);
        }

        codes["Space"] = 0x20;
        codes["Tab"] = 0x09;
        codes["Insert"] = 0x2D;
        codes["Delete"] = 0x2E;
        codes["Home"] = 0x24;
        codes["End"] = 0x23;
        codes["PageUp"] = 0x21;
        codes["PageDown"] = 0x22;
        codes["Left"] = 0x25;
        codes["Up"] = 0x26;
        codes["Right"] = 0x27;
        codes["Down"] = 0x28;
        codes["Multiply"] = 0x6A;
        codes["Add"] = 0x6B;
        codes["Subtract"] = 0x6D;
        codes["Decimal"] = 0x6E;
        codes["Divide"] = 0x6F;
        codes["Semicolon"] = 0xBA;
        codes["Equals"] = 0xBB;
        codes["Comma"] = 0xBC;
        codes["Minus"] = 0xBD;
        codes["Period"] = 0xBE;
        codes["Slash"] = 0xBF;
        codes["Backtick"] = 0xC0;
        codes["LeftBracket"] = 0xDB;
        codes["Backslash"] = 0xDC;
        codes["RightBracket"] = 0xDD;
        codes["Quote"] = 0xDE;
        return codes;
    }
}
