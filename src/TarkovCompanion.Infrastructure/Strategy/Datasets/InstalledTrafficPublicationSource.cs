using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Infrastructure.Strategy.Datasets;

/// <summary>
/// Gives the runtime the snapshot <see cref="TrafficSnapshotStore"/> holds, after installing any
/// package that has been left in the inbox folder.
/// </summary>
/// <remarks>
/// The store was merged complete and tested and then never constructed, so no snapshot could be
/// installed or read and the Raid plan said "no installed model yet" for every player. This is the
/// caller it was missing. A package is the five files <c>tools/TrafficModelBuilder</c> writes into
/// one directory; the inbox holds one such directory per package, and nothing is deleted from it:
/// installing the same signed manifest again is a no-op the store reports as
/// <c>already-current-manifest</c>, and a package the store refuses leaves only a quarantine
/// receipt, never a change to the installed snapshot.
///
/// The result is remembered for the run because loading verifies the signature again and reads the
/// whole artifact, which the cockpit must not do on every rebuild. <see cref="InvalidateAsync"/>
/// drops it, for a caller that has installed something.
/// </remarks>
public sealed class InstalledTrafficPublicationSource : ITrafficPublicationSource
{
    /// <summary>The file names <c>tools/TrafficModelBuilder</c> writes, in package order.</summary>
    public static readonly IReadOnlyList<string> PackageFileNames =
        ["dataset.json", "build-report.json", "manifest.json", "manifest.signature.json", "model.artifact"];

    private const int MaximumInboxPackages = 8;

    private readonly TrafficSnapshotStore _store;
    private readonly string? _inboxDirectory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private TrafficModelPublication? _publication;

    public InstalledTrafficPublicationSource(
        TrafficSnapshotStore store,
        string? inboxDirectory,
        ILogger<InstalledTrafficPublicationSource>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inboxDirectory = string.IsNullOrWhiteSpace(inboxDirectory) ? null : Path.GetFullPath(inboxDirectory);
        _logger = logger ?? NullLogger<InstalledTrafficPublicationSource>.Instance;
    }

    public async Task<TrafficModelPublication?> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return _publication;
            }

            await InstallInboxAsync(cancellationToken).ConfigureAwait(false);
            _publication = await LoadAsync(cancellationToken).ConfigureAwait(false);
            _loaded = true;
            return _publication;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the remembered snapshot so the next read installs and loads again.</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loaded = false;
            _publication = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TrafficModelPublication?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await _store.LoadAsync(requiredScope: null, cancellationToken).ConfigureAwait(false);
            return loaded.Disposition == TrafficModelImportDisposition.Accepted ? loaded.Publication : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A snapshot directory that cannot be read is the same to the player as none: the
            // plan and the manual tools carry on, and the traffic line says nothing is installed.
            _logger.LogWarning(exception, "The installed traffic snapshot could not be read.");
            return null;
        }
    }

    private async Task InstallInboxAsync(CancellationToken cancellationToken)
    {
        if (_inboxDirectory is null || !Directory.Exists(_inboxDirectory))
        {
            return;
        }

        string[] packages;
        try
        {
            packages = [.. Directory.EnumerateDirectories(_inboxDirectory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(MaximumInboxPackages)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The traffic inbox could not be listed.");
            return;
        }

        foreach (var package in packages)
        {
            var files = PackageFileNames.Select(name => Path.Combine(package, name)).ToArray();
            if (!files.All(File.Exists))
            {
                continue;
            }

            var streams = new List<FileStream>();
            try
            {
                foreach (var file in files)
                {
                    streams.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                }

                var result = await _store.InstallAsync(
                    new TrafficModelPackageStreams(streams[0], streams[1], streams[2], streams[3], streams[4]),
                    requiredScope: null,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Traffic package {Package}: {Disposition} ({Reason}).",
                    Path.GetFileName(package),
                    result.Disposition,
                    result.ReasonCode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Traffic package {Package} could not be installed.", Path.GetFileName(package));
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}

/// <summary>A verifier that trusts nothing, for a machine that has no signing key configured.</summary>
/// <remarks>
/// <see cref="EcdsaTrafficArtifactSignatureVerifier"/> refuses to exist without a key, and the
/// store needs a verifier to exist. With none configured no package can be installed and any
/// snapshot already on disk fails verification, which is the safe answer.
/// </remarks>
public sealed class UntrustedTrafficSignatureVerifier : ITrafficArtifactSignatureVerifier
{
    public bool Verify(ReadOnlySpan<byte> manifestSha256, TrafficArtifactSignature signature) => false;
}

/// <summary>Reads the signing keys this machine trusts for traffic packages.</summary>
/// <remarks>
/// A JSON object of key id to base64 SubjectPublicKeyInfo, kept beside the other settings. No key
/// ships with the app: whoever builds packages decides which public key to trust, and the private
/// half never comes near this repository.
/// </remarks>
public static class TrafficTrustedKeys
{
    private const int MaximumFileBytes = 64 * 1024;

    public static ITrafficArtifactSignatureVerifier Load(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes)
            {
                return new UntrustedTrafficSignatureVerifier();
            }

            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (keys is not { Count: > 0 })
            {
                return new UntrustedTrafficSignatureVerifier();
            }

            return new EcdsaTrafficArtifactSignatureVerifier(
                keys.ToDictionary(pair => pair.Key, pair => Convert.FromBase64String(pair.Value), StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            logger?.LogWarning(exception, "The traffic trusted-keys file could not be read; no package will be trusted.");
            return new UntrustedTrafficSignatureVerifier();
        }
    }
}
