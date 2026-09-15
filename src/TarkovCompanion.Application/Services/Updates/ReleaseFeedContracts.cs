using System.Collections.ObjectModel;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Updates;

/// <summary>The bounded inputs accepted by every desktop release-feed stage.</summary>
public static class ReleaseFeedLimits
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;
    public const int MaximumSignatureBytes = 2 * 1024 * 1024;
    public const long MaximumArtifactBytes = 512L * 1024 * 1024;
    public const long MaximumReleaseBytes = 1024L * 1024 * 1024;
    public const int MaximumArtifacts = 4096;
    public const int MaximumRingEntries = 8192;
    public const int MaximumJsonDepth = 32;
    public const int MaximumCommandOutputCharacters = 64 * 1024;
    public const int MaximumVersionNumber = int.MaxValue;
    public const long MaximumGeneration = 9_999_999_999;
}

/// <summary>Configuration whose absence leaves the desktop update path closed.</summary>
public sealed record SignedReleaseFeedOptions
{
    public required string Repository { get; init; }

    public string Ring { get; init; } = "stable";

    public required string StagingRoot { get; init; }

    public required string CosignPath { get; init; }

    public required string CosignSha256 { get; init; }

    public required string TrustRootPath { get; init; }

    public string SignerIdentity { get; init; } =
        "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main";

    public string SignerIssuer { get; init; } = "https://token.actions.githubusercontent.com";

    public string SignerRepository { get; init; } = "smartpbx/tarkov-companion";

    public string SignerRef { get; init; } = "refs/heads/main";

    public IntegrationSecretReference CredentialReference { get; init; } = new(
        IntegrationSecretKind.ReleaseFeedReadToken,
        new Guid("d1709329-5bc6-4dc6-bc07-c1ddd9f44cb9"),
        GameMode.Regular,
        "v2-private-release-feed");
}

/// <summary>One immutable identity named by a signed ring decision.</summary>
public sealed record ReleaseIdentity(
    string Version,
    string Commit,
    string BuildTag,
    string ManifestName,
    string ManifestSha256);

/// <summary>The locally remembered, authenticated release state owned by #270's persistence layer.</summary>
public sealed record ReleaseConsumerState(
    long SeenGeneration,
    ReleaseIdentity? Current,
    ReleaseIdentity? LastKnownGood,
    IReadOnlyDictionary<string, string> ComponentSha256,
    ReleaseIdentity? Refused = null)
{
    public static ReleaseConsumerState Empty { get; } = new(
        0,
        null,
        null,
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)));
}

/// <summary>
/// Persists the authenticated generation, installed release, refusal and recovery point.
/// </summary>
/// <remarks>
/// Issue #270 owns the implementation. Keeping the interface here lets its store be wired by
/// #294 without introducing a second settings file whose transaction could disagree with the
/// application's schema.
/// </remarks>
public interface IReleaseConsumerStateStore
{
    Task<ReleaseConsumerState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ReleaseConsumerState state, CancellationToken cancellationToken);
}

/// <summary>A release-feed transport that can only read with a protected credential.</summary>
public interface IAuthenticatedReleaseFeed
{
    Task<IReadOnlyList<string>> ListRingAsync(string ring, CancellationToken cancellationToken);

    Task DownloadRingAsync(
        string ring,
        string name,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken);

    Task DownloadAssetAsync(
        string buildTag,
        string name,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken);
}

/// <summary>Verifies one immutable file with its separately downloaded Sigstore bundle.</summary>
public interface IReleaseSignatureVerifier
{
    Task VerifyAsync(string filePath, string bundlePath, CancellationToken cancellationToken);
}

/// <summary>Metadata for a bounded, unredirected file in release staging.</summary>
public sealed record ReleaseStagedFile(string FullPath, long Length);

/// <summary>
/// Owns filesystem I/O for the application-layer release transaction.
/// </summary>
public interface IReleaseStagingStore
{
    string Create(string stagingRoot);

    void Delete(string stagingRoot, string stagingDirectory);

    ReleaseStagedFile RequirePlainFile(string path, long maximumBytes, string label);

    Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken);

    Task<JsonDocument> ReadJsonAsync(string path, int maximumBytes, CancellationToken cancellationToken);

    Task<string> Sha256Async(string path, long maximumBytes, CancellationToken cancellationToken);
}

public enum ReleasePreparationStatus
{
    UpToDate,
    Paused,
    Ready,
}

/// <summary>One downloaded artifact, with the signed activation metadata that selected it.</summary>
public sealed record VerifiedReleaseArtifact(
    string Name,
    string Component,
    string Role,
    string Sha256,
    long Size,
    string? BaseSha256,
    string Path);

/// <summary>Files verified for one binary, data and model release transaction.</summary>
public sealed record VerifiedReleasePlan(
    ReleaseIdentity Release,
    ReleaseIdentity? LastKnownGood,
    long Generation,
    string Ring,
    string Directory,
    IReadOnlyList<VerifiedReleaseArtifact> Artifacts,
    IReadOnlyDictionary<string, string> Components,
    bool IsRollback,
    string AuthorizationAction);

public sealed record ReleasePreparation(ReleasePreparationStatus Status, VerifiedReleasePlan? Plan = null);

/// <summary>Obtains the release-feed credential without exposing it to callers.</summary>
public sealed class ReleaseFeedCredentialProvider(
    IIntegrationSecretStore secrets,
    IntegrationSecretReference reference)
{
    public async Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        if (!secrets.IsAvailable)
        {
            throw new InvalidOperationException("Protected release-feed credential storage is unavailable.");
        }

        var token = await secrets.LoadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || !token.Equals(token.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The private release-feed credential is missing or invalid.");
        }

        return token;
    }
}
