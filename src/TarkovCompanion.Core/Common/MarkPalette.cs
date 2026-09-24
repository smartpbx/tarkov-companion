namespace TarkovCompanion.Core.Common;

/// <summary>One colour a player can give a mark, with the name a screen reader says for it.</summary>
public sealed record MarkColour(string Hex, string Name);

/// <summary>
/// The six colours a mark can be given (#290), shared by the tablet, the desktop and the group relay.
/// </summary>
/// <remarks>
/// Okabe and Ito's colour-blind-safe set, less its black and its dark blue, which vanish on a dark
/// map. A mark with no chosen colour keeps its kind's own colour, so a colour is only ever one of
/// these six or nothing: anything else a client sends is dropped rather than drawn, which is also
/// what keeps a squadmate from painting marks in a colour nobody chose from this list.
/// </remarks>
public static class MarkPalette
{
    public static IReadOnlyList<MarkColour> Colours { get; } =
    [
        new("#56B4E9", "Sky blue"),
        new("#E69F00", "Orange"),
        new("#009E73", "Green"),
        new("#F0E442", "Yellow"),
        new("#D55E00", "Red orange"),
        new("#CC79A7", "Pink"),
    ];

    /// <summary>The palette entry <paramref name="value"/> names, as upper-case #RRGGBB, or null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16)
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var colour in Colours)
        {
            if (string.Equals(colour.Hex, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return colour.Hex;
            }
        }

        return null;
    }

    /// <summary>The screen-reader name of a palette colour, or null for anything else.</summary>
    public static string? NameOf(string? value) =>
        Normalize(value) is { } hex ? Colours.First(colour => colour.Hex == hex).Name : null;
}
