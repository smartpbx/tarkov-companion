namespace TarkovCompanion.Application.Services.Intel;

/// <summary>The item quantity Intel's headline and comparison table both call needed.</summary>
public static class V2IntelNeed
{
    /// <summary>
    /// Remaining named quest requirements plus the profile's remaining hideout quantity.
    /// </summary>
    /// <remarks>
    /// The aggregate quest snapshot can include every future quest in the catalog. The named
    /// Keep rows are the active profile's actual board, so using them here prevents a key from
    /// reading "225 wanted" beside a headline that correctly says it is not needed.
    /// </remarks>
    public static int Remaining(V2ItemIntelResult? result) =>
        (result?.Keep?.Quests.Sum(row => row.Remaining ?? 0) ?? 0) +
        (result?.Value?.HideoutCount ?? 0);
}

/// <summary>Turns the catalog facts behind map locks into labels a player can read.</summary>
public static class V2IntelLockNames
{
    public const string Unknown = "Unknown lock";

    /// <summary>
    /// Keeps names already supplied by the catalog and replaces its opaque map-lock ids with the
    /// key item's catalog name. The suffix names the object, not the place it opens, so it is
    /// removed from labels such as "Dorm room 114 key".
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string> locks,
        string catalogItemName,
        string catalogItemId)
    {
        ArgumentNullException.ThrowIfNull(locks);
        var catalogName = LockName(catalogItemName, catalogItemId);
        return
        [
            .. locks.Select(value => LooksLikeIdentifier(value)
                ? catalogName
                : string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim()),
        ];
    }

    internal static bool LooksLikeIdentifier(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(part => part.Length >= 20 && part.All(char.IsAsciiHexDigit));
    }

    private static string LockName(string name, string itemId)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 ||
            string.Equals(trimmed, itemId, StringComparison.OrdinalIgnoreCase) ||
            LooksLikeIdentifier(trimmed))
        {
            return Unknown;
        }

        foreach (var suffix in new[] { " keycard", " key" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var withoutSuffix = trimmed[..^suffix.Length].TrimEnd();
                return withoutSuffix.Length == 0 ? Unknown : withoutSuffix;
            }
        }

        return trimmed;
    }
}
