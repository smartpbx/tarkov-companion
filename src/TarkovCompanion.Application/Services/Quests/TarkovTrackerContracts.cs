using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public sealed record TarkovTrackerOptions
{
    public bool Enabled { get; init; }

    public bool NetworkAccessEnabled { get; init; } = true;

    /// <summary>[#292] Asked before every connect: false under Local only or with TarkovTracker switched off.</summary>
    public Func<bool>? NetworkProbe { get; init; }

    /// <summary>Whether TarkovTracker may be reached right now.</summary>
    public bool NetworkAllowedNow => NetworkAccessEnabled && (NetworkProbe?.Invoke() ?? true);

    public string UserAgent { get; init; } = "TarkovCompanion/1.0 (read-only progress import)";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan MinimumForegroundRefreshInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan TransientFailureBackoff { get; init; } = TimeSpan.FromMinutes(1);

    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;

    public void Validate()
    {
        if (UserAgent.Length is < 5 or > 200 ||
            !UserAgent.Equals(UserAgent.Trim(), StringComparison.Ordinal) ||
            !UserAgent.Contains('/') ||
            UserAgent.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The TarkovTracker User-Agent must be a descriptive 5-200 character application identifier.",
                nameof(UserAgent));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MinimumForegroundRefreshInterval < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumForegroundRefreshInterval),
                "Foreground refreshes must be at least 60 seconds apart.");
        }

        if (TransientFailureBackoff < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(TransientFailureBackoff));
        }

        if (MaximumResponseBytes is < 1024 or > 2 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }
    }
}

public enum TarkovTrackerApiFailure
{
    RedirectRejected,
    Unauthorized,
    Forbidden,
    RateLimited,
    ServerError,
    InvalidResponse,
    Timeout,
    Transport,
}

public sealed class TarkovTrackerApiException : Exception
{
    public TarkovTrackerApiException(
        TarkovTrackerApiFailure failure,
        string message,
        int? statusCode = null,
        DateTimeOffset? retryAfterUtc = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        RetryAfterUtc = retryAfterUtc;
    }

    public TarkovTrackerApiFailure Failure { get; }

    public int? StatusCode { get; }

    public DateTimeOffset? RetryAfterUtc { get; }
}

public sealed record TarkovTrackerQuota(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetUtc);

public sealed record TarkovTrackerTokenValidation(
    GameMode GameMode,
    TarkovTrackerQuota Quota);

public sealed record TarkovTrackerTaskProgress(
    string Id,
    bool Complete,
    bool Failed,
    bool Invalid);

public sealed record TarkovTrackerObjectiveProgress(
    string Id,
    bool Complete,
    decimal? Count,
    bool Invalid);

public sealed record TarkovTrackerProgressSnapshot(
    GameMode GameMode,
    string PayloadSha256,
    DateTimeOffset FetchedUtc,
    IReadOnlyList<TarkovTrackerTaskProgress> Tasks,
    IReadOnlyList<TarkovTrackerObjectiveProgress> Objectives);

public sealed record TarkovTrackerProgressFetch(
    bool NotModified,
    string? ETag,
    TarkovTrackerProgressSnapshot? Snapshot,
    TarkovTrackerQuota Quota);

public interface ITarkovTrackerApiClient
{
    Task<TarkovTrackerTokenValidation> ValidateTokenAsync(
        string token,
        GameMode expectedMode,
        CancellationToken cancellationToken);

    Task<TarkovTrackerProgressFetch> GetProgressAsync(
        string token,
        GameMode expectedMode,
        string? etag,
        CancellationToken cancellationToken);
}

public enum TarkovTrackerRefreshKind
{
    Manual,
    Foreground,
}

public sealed record TarkovTrackerIntegrationStatus(
    bool FeatureEnabled,
    bool NetworkAccessEnabled,
    bool SecureStorageAvailable,
    bool Connected,
    bool RequiresReconnect,
    GameMode GameMode,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? SnapshotFetchedUtc,
    DateTimeOffset? NextEligibleRefreshUtc,
    TarkovTrackerQuota Quota)
{
    public bool CanConnect => FeatureEnabled && NetworkAccessEnabled && SecureStorageAvailable;

    public bool CanRefresh => CanConnect && Connected && !RequiresReconnect;
}

public sealed record TarkovTrackerRefreshResult(
    QuestProgressImportPreview Preview,
    bool NotModified,
    TarkovTrackerIntegrationStatus Status);

public interface ITarkovTrackerIntegrationService
{
    Task<TarkovTrackerIntegrationStatus> GetStatusAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken);

    Task<TarkovTrackerIntegrationStatus> ConnectAsync(
        QuestProfileScope scope,
        string token,
        CancellationToken cancellationToken);

    Task<TarkovTrackerIntegrationStatus> DisconnectAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken);

    Task<TarkovTrackerRefreshResult> RefreshPreviewAsync(
        QuestProfileScope scope,
        TarkovTrackerRefreshKind kind,
        CancellationToken cancellationToken);
}

public static class TarkovTrackerTokenPolicy
{
    public static void ValidateForMode(string token, GameMode expectedMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 4096 || !token.Equals(token.Trim(), StringComparison.Ordinal) ||
            token.Any(value => char.IsWhiteSpace(value) || char.IsControl(value)))
        {
            throw new ArgumentException("The TarkovTracker token format is invalid.", nameof(token));
        }

        var prefix = expectedMode switch
        {
            GameMode.Regular => "PVP_",
            GameMode.Pve => "PVE_",
            GameMode.PvpSeason => "SZN_",
            _ => throw new ArgumentOutOfRangeException(nameof(expectedMode)),
        };
        if (!token.StartsWith(prefix, StringComparison.Ordinal) || token.Length == prefix.Length)
        {
            throw new ArgumentException(
                $"The TarkovTracker token prefix does not match the {expectedMode} profile mode.",
                nameof(token));
        }
    }

    public static string ApiMode(GameMode mode) => mode switch
    {
        GameMode.Regular => "pvp",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "seasonal",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static GameMode ParseApiMode(string value) => value switch
    {
        "pvp" => GameMode.Regular,
        "pve" => GameMode.Pve,
        "seasonal" => GameMode.PvpSeason,
        _ => throw new TarkovTrackerApiException(
            TarkovTrackerApiFailure.InvalidResponse,
            "TarkovTracker returned an unsupported game mode."),
    };
}
