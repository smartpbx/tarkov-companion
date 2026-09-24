using TarkovCompanion.App.Localization;
using System.Globalization;
using TarkovCompanion.Core.Domain.Ammo;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// How many rounds the player owns, from the profile's owned counts: loose rounds plus the rounds
/// in every sealed pack of them.
/// </summary>
/// <remarks>
/// The counts come from stash scans, a full one or an Ammo case sub-scan (#283), and from hand
/// corrections. A round nobody has recorded is unknown, not none: "?" is what a page must show
/// until a scan has looked, the same rule the hideout and quest needs follow.
/// </remarks>
public sealed class OwnedAmmo
{
    private readonly IReadOnlyDictionary<string, int> _owned;
    private readonly ILookup<string, AmmoPackContents> _packsByRound;

    public OwnedAmmo(IReadOnlyDictionary<string, int> owned, IReadOnlyList<AmmoPackContents> packs)
    {
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _packsByRound = (packs ?? throw new ArgumentNullException(nameof(packs)))
            .ToLookup(pack => pack.AmmoItemId, StringComparer.Ordinal);
    }

    public static OwnedAmmo None { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal), []);

    /// <summary>Rounds owned of one round, or null when neither it nor a pack of it was ever counted.</summary>
    public int? RoundsOf(string roundId)
    {
        ArgumentNullException.ThrowIfNull(roundId);
        var known = _owned.TryGetValue(roundId, out var loose);
        long total = known ? Math.Max(0, loose) : 0;
        foreach (var pack in _packsByRound[roundId])
        {
            if (_owned.TryGetValue(pack.PackItemId, out var packs))
            {
                known = true;
                total += (long)Math.Max(0, packs) * Math.Max(0, pack.Quantity);
            }
        }

        return known ? (int)Math.Min(int.MaxValue, total) : null;
    }

    /// <summary>Rounds owned across a caliber's rounds; null when none of them was ever counted.</summary>
    public int? RoundsOf(IEnumerable<string> roundIds)
    {
        ArgumentNullException.ThrowIfNull(roundIds);
        int? total = null;
        foreach (var id in roundIds)
        {
            if (RoundsOf(id) is { } rounds)
            {
                total = (int)Math.Min(int.MaxValue, (long)(total ?? 0) + rounds);
            }
        }

        return total;
    }

    /// <summary>"240 owned", "None owned", or empty when unknown.</summary>
    public static string Short(int? rounds) => rounds switch
    {
        null => string.Empty,
        0 => IntelText.AmmoNoneOwned,
        int count => IntelText.AmmoOwnedShort(count),
    };

    /// <summary>The context panel's line, which also says how to find out.</summary>
    public static string Long(int? rounds) => rounds switch
    {
        null => IntelText.AmmoOwnedNotScanned,
        0 => IntelText.AmmoYouOwnNone,
        int count => IntelText.AmmoYouOwnRounds(count),
    };
}
