using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using Velopack;
using Velopack.Locators;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>Going back to the previous build, and what Setup shows about the running one.</summary>
public interface IUpdateRollback
{
    /// <summary>Whether this copy was installed, which is the only kind that can go back.</summary>
    bool IsInstalled { get; }

    /// <summary>The running build and the one "Go back" would install.</summary>
    Task<RollbackOffer> FindPreviousAsync(CancellationToken cancellationToken);

    /// <summary>Fetches the older build and checks it against its hash. Nothing installed is touched.</summary>
    Task<UpdateProgress> DownloadPreviousAsync(RollbackOffer offer, Action<int>? progress, CancellationToken cancellationToken);

    /// <summary>Sets the pin, records the apply, and hands over to the updater. Does not return if it works.</summary>
    void ApplyAndRestart();

    /// <summary>The pin holding back newer builds, while it applies to the running build.</summary>
    UpdatePin? Pin { get; }

    /// <summary>Ends the pin, so the next check offers the newest build again.</summary>
    void ResumeUpdates();

    /// <summary>Version, channel, feed host, package SHA256 and when it was applied.</summary>
    IReadOnlyList<UpdateProvenanceRow> Provenance();
}

/// <remarks>
/// #292. Going back used to be a sentence: "install its Setup.exe over this one", with no Setup.exe
/// of the previous build anywhere a player could find it. Now it is the updater doing the same
/// job it does forward, with the downgrade allowed, over a feed of exactly one package.
///
/// One package, because the updater picks the newest build of whatever feed it is given; with
/// the real feed it would pick the build the player is leaving. The package is still checked
/// against a SHA256 it did not choose: the feed's own, or the one taken when the copy was kept.
///
/// Where the older build comes from: the rough feed lists one build (the relay deletes the
/// rest when it publishes), so the usual source is the kept copy: before an update downloads,
/// the package of the build that is running is copied into the data folder, because the updater
/// deletes it from <c>packages\</c> as soon as the new one arrives.
/// </remarks>
public sealed partial class VelopackUpdateGateway : IUpdateRollback
{
    private readonly IVelopackLocator? _locator;
    private readonly Lazy<IUpdateFeedTransport> _transport;
    private UpdateManager? _applyManager;
    private UpdatePin? _pendingPin;
    private string? _pendingHost;
    private string? _installedFeedSha256;

    /// <summary>The pin, the last apply and the kept package; null where nothing is remembered (tools, most tests).</summary>
    public IUpdateStateStore? State { get; init; }

    public UpdatePin? Pin => State?.Read().Pin is { } pin
        && InstalledVersion is { } installed
        && UpdateRollbackRules.Holds(pin, installed, pin.HoldThrough)
            ? pin
            : null;

    private string? InstalledVersion => _manager.Value is { IsInstalled: true } manager
        ? manager.CurrentVersion?.ToString()
        : null;

    private IVelopackLocator? Locator
    {
        get
        {
            if (_locator is not null)
            {
                return _locator;
            }

            try
            {
                return VelopackLocator.Current;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        }
    }

    public async Task<RollbackOffer> FindPreviousAsync(CancellationToken cancellationToken)
    {
        if (InstalledVersion is not { } installed || _manager.Value is not { } manager)
        {
            return new(AppBuildIdentity.Current.Version, null, null, "Run from a folder, so it cannot go back");
        }

        IReadOnlyList<UpdateFeedPackage> packages = [];
        string? feedError = null;
        try
        {
            var channel = Locator?.Channel ?? "win";
            var json = await _transport.Value.ReadTextAsync($"releases.{channel}.json", cancellationToken).ConfigureAwait(true);
            packages = UpdateFeedDocument.Parse(json).Packages;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not read {Feed} to find an older build", Channel.Feed);
            feedError = exception.Message;
        }

        var packageId = manager.AppId;
        _installedFeedSha256 = packages
            .FirstOrDefault(package => package.IsFull && SameVersion(package.Version, installed))?.Sha256;
        var previous = UpdateRollbackRules.Choose(installed, packages, packageId, KeptCandidate());
        var latest = UpdateRollbackRules.Latest(packages, packageId);
        var reason = previous is not null
            ? null
            : feedError is not null
                ? $"No older build kept on this PC, and the feed could not be read · {feedError}"
                : "No older build in the feed or kept on this PC";
        return new(installed, previous, latest, reason);
    }

    public async Task<UpdateProgress> DownloadPreviousAsync(
        RollbackOffer offer,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.Previous is not { } target || _manager.Value is not { IsInstalled: true } manager)
        {
            return new("There is no older build to go back to.");
        }

        _verified = null;
        _applyManager = null;
        _pendingPin = null;
        try
        {
            IUpdateFeedTransport inner = target.Source == RollbackSource.Feed
                ? _transport.Value
                : new DirectoryUpdateFeedTransport(State?.KeptFolder
                    ?? throw new InvalidOperationException("Nothing is kept on this PC."));
            var single = new SingleReleaseFeedTransport(inner, target, manager.AppId ?? RollbackPackageId);
            var downgrade = new UpdateManager(
                new HashVerifiedUpdateSource(single, _logger),
                new UpdateOptions { AllowVersionDowngrade = true },
                Locator);
            var info = await downgrade.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (info is null || !SameVersion(info.TargetFullRelease.Version.ToString(), target.Version))
            {
                return new($"The updater would not go back to {target.Version}.", Failed: true);
            }

            await downgrade.DownloadUpdatesAsync(info, progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _verified = info.TargetFullRelease;
            _applyManager = downgrade;
            _pendingHost = target.Source == RollbackSource.KeptCopy ? "kept copy on this PC" : null;
            _pendingPin = UpdateRollbackRules.PinFor(target.Version, offer.Installed, offer.LatestInFeed, DateTimeOffset.UtcNow);
            _logger?.LogInformation("Going back from {Installed} to {Target}, from the {Source}", offer.Installed, target.Version, target.Source);
            return new($"{target.Version} is ready · it installs when this restarts", CanApply: true, Available: target.Version);
        }
        catch (UpdateHashMismatchException exception)
        {
            _logger?.LogError(exception, "Refused {Target}: the package did not match its hash", target.Version);
            return new("Refused · the package did not match its hash, so nothing was installed", Failed: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not fetch {Target} to go back to", target.Version);
            return new($"Could not fetch {target.Version} · {exception.Message}", Failed: true);
        }
    }

    public void ResumeUpdates()
    {
        if (State is { } store)
        {
            store.Write(store.Read() with { Pin = null });
        }
    }

    public IReadOnlyList<UpdateProvenanceRow> Provenance() => UpdateProvenanceText.Rows(
        InstalledVersion ?? AppBuildIdentity.Current.Version,
        Channel.Name,
        Channel.Feed,
        State?.Read().Applied,
        _installedFeedSha256);

    private const string RollbackPackageId = "TarkovCompanionDesktop";

    /// <summary>What <see cref="CheckAsync"/> says instead of offering a build the pin holds back.</summary>
    private UpdateProgress? HeldByPin(UpdateManager manager, string available)
    {
        if (State is not { } store || store.Read() is not { Pin: { } pin } state || manager.CurrentVersion is not { } current)
        {
            return null;
        }

        if (UpdateRollbackRules.Holds(pin, current.ToString(), available))
        {
            _logger?.LogInformation("Holding {Available} back: staying on {Pinned} until a newer build", available, pin.Version);
            return new($"Staying on {pin.Version} · {available} is held until a newer build", Available: available, Held: true);
        }

        _logger?.LogInformation("{Available} is newer than {HoldThrough}; no longer staying on {Pinned}", available, pin.HoldThrough, pin.Version);
        store.Write(state with { Pin = null });
        return null;
    }

    private RollbackCandidate? KeptCandidate()
    {
        if (State?.Read().Kept is not { } kept)
        {
            return null;
        }

        var path = Path.Combine(State.KeptFolder, kept.FileName);
        return File.Exists(path) ? new(kept.Version, kept.FileName, kept.Sha256, kept.Size, RollbackSource.KeptCopy) : null;
    }

    /// <summary>
    /// Copies the running build's package out of <c>packages\</c> before an update deletes it.
    /// </summary>
    /// <remarks>
    /// Never in the way of the update: anything that goes wrong here is logged and the update
    /// goes ahead without a kept copy. Only one copy is kept (a full build is about 100 MB).
    /// </remarks>
    private void KeepInstalledPackage(UpdateManager manager)
    {
        if (State is not { } store || Locator is not { PackagesDir: { } packagesDir } || manager.CurrentVersion is not { } current)
        {
            return;
        }

        try
        {
            var version = current.ToString();
            var fileName = $"{manager.AppId ?? RollbackPackageId}-{version}-full.nupkg";
            var source = Path.Combine(packagesDir, fileName);
            var state = store.Read();
            var destination = Path.Combine(store.KeptFolder, fileName);
            if (!File.Exists(source) || (state.Kept?.FileName == fileName && File.Exists(destination)))
            {
                return;
            }

            Directory.CreateDirectory(store.KeptFolder);
            var temporary = destination + ".tmp";
            File.Copy(source, temporary, overwrite: true);
            string sha256;
            using (var stream = File.OpenRead(temporary))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            // The record of what was applied, when it names this build, is what the bytes should be.
            if (state.Applied is { } applied
                && SameVersion(applied.Version, version)
                && !applied.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                _logger?.LogWarning("Not keeping {File}: it is not the package that was applied", fileName);
                return;
            }

            File.Move(temporary, destination, overwrite: true);
            foreach (var other in Directory.EnumerateFiles(store.KeptFolder).Where(path => !path.Equals(destination, StringComparison.Ordinal)))
            {
                File.Delete(other);
            }

            store.Write(state with { Kept = new(version, fileName, sha256, new FileInfo(destination).Length) });
            _logger?.LogInformation("Kept {File} (SHA256 {Sha256}) so this PC can go back to {Version}", fileName, sha256, version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "Could not keep the running build's package; going back will need the feed");
        }
    }

    /// <summary>Remembers what is being handed over, and the pin when it is a step back.</summary>
    private void RecordApply(VelopackAsset update)
    {
        if (State is not { } store)
        {
            return;
        }

        try
        {
            var wentBack = _applyManager is not null;
            var host = (wentBack ? _pendingHost : null) ?? UpdateProvenanceText.HostOf(Channel.Feed);
            store.Write(store.Read() with
            {
                Applied = new(update.Version.ToString(), Channel.Name, host, update.SHA256 ?? string.Empty, DateTimeOffset.UtcNow, wentBack),
                // Forward ends any pin; back sets one.
                Pin = wentBack ? _pendingPin : null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "Could not record the update being applied");
        }
    }

    private static bool SameVersion(string left, string right) =>
        SemanticVersion.TryParse(left, out var a) && SemanticVersion.TryParse(right, out var b)
            ? a == b
            : left.Equals(right, StringComparison.Ordinal);
}

/// <summary>A feed of exactly one package, read through another transport.</summary>
/// <remarks>
/// The updater installs the newest build of the feed it is given, so to go back it is given a
/// feed in which the older build is the newest. The package itself still comes from where it
/// really is, and is still refused if its bytes do not match the hash listed here.
/// </remarks>
internal sealed class SingleReleaseFeedTransport(IUpdateFeedTransport inner, RollbackCandidate target, string packageId)
    : IUpdateFeedTransport
{
    public string Description => $"{inner.Description} ({target.FileName} only)";

    public Task<string> ReadTextAsync(string fileName, CancellationToken cancellationToken) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            Assets = new[]
            {
                new
                {
                    PackageId = packageId,
                    target.Version,
                    Type = "Full",
                    target.FileName,
                    SHA256 = target.Sha256,
                    target.Size,
                },
            },
        }));

    public Task DownloadAsync(string fileName, string destination, long expectedSize, Action<int> progress, CancellationToken cancellationToken) =>
        inner.DownloadAsync(fileName, destination, expectedSize, progress, cancellationToken);
}
