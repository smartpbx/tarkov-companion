using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.Quests;

/// <summary>The one line Setup › Progress shows about TarkovTracker.</summary>
/// <remarks>
/// [#863] The parts were glued with a bare space, and each ended in its own full stop, so the line
/// read "…no protected storage for the token Read quota is unknown." Every part is now a phrase
/// and the line joins them with " · ", the separator the rest of the status lines use.
/// </remarks>
internal static class TarkovTrackerStatusLine
{
    public static string Compose(TarkovTrackerIntegrationStatus status, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var availability = !status.SecureStorageAvailable
            ? SetupText.QuestsTrackerNoStorage
            : !status.FeatureEnabled
                ? SetupText.QuestsTrackerOff
                : !status.NetworkAccessEnabled
                    ? SetupText.QuestsTrackerOffline
                    : status.RequiresReconnect
                        ? SetupText.QuestsTrackerRejected
                        : status.Connected
                            ? SetupText.QuestsTrackerConnected(GameModeLabel.Of(status.GameMode))
                            : SetupText.QuestsTrackerNotConnected(GameModeLabel.Of(status.GameMode));
        var quota = status.Quota.Remaining is { } remaining
            ? SetupText.QuestsTrackerQuota(
                remaining.ToString(CultureInfo.CurrentCulture),
                status.Quota.Limit?.ToString(CultureInfo.CurrentCulture) ?? "?")
            : SetupText.QuestsTrackerQuotaUnknown;
        var backoff = status.NextEligibleRefreshUtc is { } next
            ? SetupText.QuestsTrackerNextRefresh(LocalTime.Moment(next))
            : null;
        return string.Join(
            " · ",
            new[] { operation, availability, quota, backoff }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
