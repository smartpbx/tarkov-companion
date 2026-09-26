using System.Globalization;
using System.Text;

namespace TarkovCompanion.Core.Domain.Recognition.Learning;

/// <summary>
/// One icon crop the player confirmed as an item (epic #712, 1-12): only the item's own squares,
/// PNG-encoded, never the screen it was cut from.
/// </summary>
public sealed record LearnedIconReference(
    Guid ReferenceId,
    string ItemId,
    int WidthCells,
    int HeightCells,
    ReadOnlyMemory<byte> Png,
    DateTimeOffset CreatedUtc);

/// <summary>An OCR reading the player said was a different item, and how often they said so.</summary>
public sealed record LearnedTextAlias(
    string Kind,
    string NormalizedText,
    string ItemId,
    string ItemName,
    int Picks,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>The epic's rule: an alternate picked twice becomes an alias; once is a correction.</summary>
    public const int PicksToBecomeAlias = 2;

    /// <summary>The reading of an item's name on a flea, task or item-detail screen.</summary>
    public const string ItemNameKind = "item-name";

    public bool IsActive => Picks >= PicksToBecomeAlias;

    /// <summary>Lower case, single spaces, trimmed: the same reading keys the same row.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(char.ToLower(character, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>How much has been learned, for Setup's row.</summary>
public sealed record CorrectionMemoryCounts(int Icons, int Names, int FrameCorrections)
{
    public static CorrectionMemoryCounts None { get; } = new(0, 0, 0);
}

/// <summary>What kind of learned data a clear removes.</summary>
public enum CorrectionMemoryKind
{
    Icons = 1,
    Names,
    FrameCorrections,
}

/// <summary>
/// The player's corrections, kept on this PC. Never exported, never in a diagnostic report.
/// </summary>
public interface ICorrectionMemoryStore
{
    Task AddIconAsync(LearnedIconReference reference, CancellationToken cancellationToken);

    Task<IReadOnlyList<LearnedIconReference>> ListIconsAsync(CancellationToken cancellationToken);

    /// <summary>Counts one more pick of <paramref name="itemId"/> for the reading, and returns the row.</summary>
    Task<LearnedTextAlias> RecordAliasPickAsync(
        string kind,
        string text,
        string itemId,
        string itemName,
        DateTimeOffset utc,
        CancellationToken cancellationToken);

    /// <summary>Every alias picked often enough to be used.</summary>
    Task<IReadOnlyList<LearnedTextAlias>> ListActiveAliasesAsync(string kind, CancellationToken cancellationToken);

    Task SetFrameCorrectionAsync(
        string frameSha256,
        string targetKey,
        string itemId,
        DateTimeOffset utc,
        CancellationToken cancellationToken);

    /// <summary>Target key to corrected item id, for one frame.</summary>
    Task<IReadOnlyDictionary<string, string>> ListFrameCorrectionsAsync(string frameSha256, CancellationToken cancellationToken);

    Task<CorrectionMemoryCounts> CountAsync(CancellationToken cancellationToken);

    Task ClearAsync(CorrectionMemoryKind kind, CancellationToken cancellationToken);
}
