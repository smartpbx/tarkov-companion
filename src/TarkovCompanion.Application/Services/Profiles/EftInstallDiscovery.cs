using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Profiles;

public enum EftInstallDiscoveryStatus
{
    Uninitialized,
    Ready,
    Missing,
    InvalidConfiguration,
    Invalidated,
    Unavailable,
}

/// <summary>
/// A revision changes only when the discovery conclusion changes. <see cref="CheckedUtc"/> still
/// advances on a confirming probe, so diagnostics can distinguish a stale conclusion from one
/// that was just revalidated without making every polling tick redraw consumers.
/// </summary>
public sealed record EftInstallDiscoverySnapshot
{
    public EftInstallDiscoverySnapshot(
        long revision,
        EftInstallDiscoveryStatus status,
        EftPaths paths,
        DateTimeOffset checkedUtc,
        string code,
        string detail)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code != code.Trim() || code.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded discovery code is required.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(detail) || detail.Length > 2048 || detail != detail.Trim() || detail.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded actionable discovery detail is required.", nameof(detail));
        }
        if (status == EftInstallDiscoveryStatus.Ready && paths.InstallRoot is null)
        {
            throw new ArgumentException("Ready discovery requires an install root.", nameof(paths));
        }

        Revision = revision;
        Status = status;
        Paths = paths;
        CheckedUtc = checkedUtc.ToUniversalTime();
        Code = code;
        Detail = detail;
    }

    public long Revision { get; }

    public EftInstallDiscoveryStatus Status { get; }

    public EftPaths Paths { get; }

    public DateTimeOffset CheckedUtc { get; }

    public string Code { get; }

    public string Detail { get; }

    public bool IsContextValid => Status == EftInstallDiscoveryStatus.Ready;

    public static EftInstallDiscoverySnapshot Uninitialized(DateTimeOffset checkedUtc) => new(
        0,
        EftInstallDiscoveryStatus.Uninitialized,
        new EftPaths(null, null, null, Confidence.Unknown),
        checkedUtc,
        "eft-discovery-uninitialized",
        "Escape from Tarkov installation discovery has not run yet.");
}

public sealed record EftInstallDiscoveryChanged(EftInstallDiscoverySnapshot Snapshot);

public interface IEftInstallDiscoverySource
{
    EftInstallDiscoverySnapshot Current { get; }

    event Action<EftInstallDiscoveryChanged>? StateChanged;

    Task<EftInstallDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<EftInstallDiscoverySnapshot> WatchAsync(
        TimeSpan interval,
        CancellationToken cancellationToken);
}
