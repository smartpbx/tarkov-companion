using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>
/// This is deliberately a projection, not a merge: an old active profile gets one stable scope
/// and every supported local fact moves with it. Context that v1 never recorded is supplied by
/// the caller, so migration cannot quietly claim Regular mode or English.
/// </summary>
public static class LegacyProfileMigration
{
    public static ProfileRecord Migrate(
        PlayerProfile legacy,
        ProfileContext context,
        IEnumerable<ProfilePin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(context);
        if (legacy.Id != context.Identity.ProfileId)
        {
            throw new ArgumentException("The migrated context must keep the legacy stable profile id.", nameof(context));
        }

        var expectedGeneration = string.IsNullOrWhiteSpace(legacy.ProfileGeneration)
            ? $"legacy-{legacy.Id:N}"
            : legacy.ProfileGeneration.Trim();
        if (!string.Equals(expectedGeneration, context.Identity.Generation, StringComparison.Ordinal))
        {
            throw new ArgumentException("The migrated context generation must be deterministic from the legacy profile.", nameof(context));
        }

        return new ProfileRecord(
            context,
            legacy.Name,
            new ProfileProgress(
                legacy.Level,
                legacy.TraderLevels,
                legacy.CompletedTaskIds,
                legacy.ObjectiveProgress,
                legacy.HideoutStationLevels,
                legacy.WishlistItemIds,
                legacy.OwnedItemCounts,
                legacy.EventItemStates.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
                legacy.ItemOverrides,
                pins?.ToArray()),
            ProfileLifecycle.Active,
            legacy.UpdatedUtc);
    }
}
