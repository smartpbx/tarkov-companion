using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// Turns one authenticated ring decision into a fully verified binary, data and model plan.
/// </summary>
/// <remarks>
/// Nothing becomes current here. The caller activates every component, then calls
/// <see cref="CommitAsync"/> once; a cancellation or verification failure removes only the new
/// staging directory and leaves the installed release and its last-known-good state untouched.
/// </remarks>
public sealed partial class SignedReleaseFeedConsumer
{
    private const string IndexMediaType = "application/vnd.tarkov-companion.release-index.v1+json";
    private const string EnvelopeMediaType = "application/vnd.tarkov-companion.signed-release-index.v1+json";
    private const string SourceRepository = "smartpbx/tarkov-companion";
    private static readonly string[] FeedNames = ["binary", "data", "model"];

    private readonly SignedReleaseFeedOptions _options;
    private readonly IAuthenticatedReleaseFeed _feed;
    private readonly IReleaseSignatureVerifier _verifier;
    private readonly IReleaseConsumerStateStore _stateStore;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VerifiedReleasePlan? _pendingPlan;

    public SignedReleaseFeedConsumer(
        SignedReleaseFeedOptions options,
        IAuthenticatedReleaseFeed feed,
        IReleaseSignatureVerifier verifier,
        IReleaseConsumerStateStore stateStore,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(stateStore);
        if (string.IsNullOrEmpty(options.Repository) || options.Repository.Length > 200 ||
            !Repository().IsMatch(options.Repository) ||
            options.Repository.Equals(SourceRepository, StringComparison.OrdinalIgnoreCase) ||
            options.Ring is not ("canary" or "beta" or "stable") ||
            string.IsNullOrWhiteSpace(options.StagingRoot) ||
            !options.StagingRoot.Equals(options.StagingRoot.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The private release-feed configuration is invalid.", nameof(options));
        }

        _options = options;
        _feed = feed;
        _verifier = verifier;
        _stateStore = stateStore;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ReleasePreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (_pendingPlan is not null)
            {
                throw new InvalidOperationException(
                    "The prepared release must be committed, refused, or abandoned before checking again.");
            }

            var state = ValidateState(await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false));
            var names = await _feed.ListRingAsync(_options.Ring, cancellationToken).ConfigureAwait(false);
            if (names.Count > ReleaseFeedLimits.MaximumRingEntries)
            {
                throw new InvalidDataException("The release ring exceeds its entry-count limit.");
            }

            var current = SelectCurrent(names);
            if (current is null)
            {
                if (state.SeenGeneration > 0 || names.Count > 0)
                {
                    throw new InvalidDataException("The release ring is empty after previously carrying state.");
                }

                return new ReleasePreparation(ReleasePreparationStatus.UpToDate);
            }

            if (current.Value.Generation < state.SeenGeneration)
            {
                throw new InvalidDataException("The release ring moved behind this installation's authenticated generation.");
            }

            if (current.Value.Generation == state.SeenGeneration)
            {
                return new ReleasePreparation(ReleasePreparationStatus.UpToDate);
            }

            staging = CreateStagingDirectory();
            var envelopePath = Path.Combine(staging, current.Value.Name);
            await _feed.DownloadRingAsync(
                _options.Ring,
                current.Value.Name,
                envelopePath,
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            var (indexPath, indexBundlePath) = await UnpackEnvelopeAsync(envelopePath, staging, cancellationToken)
                .ConfigureAwait(false);
            await _verifier.VerifyAsync(indexPath, indexBundlePath, cancellationToken).ConfigureAwait(false);

            using var indexDocument = await ReadJsonAsync(
                indexPath,
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            var decision = ParseDecision(indexDocument.RootElement, current.Value.Generation);

            if (decision.Paused && !decision.IsRollback)
            {
                await _stateStore.SaveAsync(
                    state with
                    {
                        SeenGeneration = decision.Generation,
                        LastKnownGood = decision.LastKnownGood,
                    },
                    cancellationToken).ConfigureAwait(false);
                DeleteStaging(staging);
                staging = null;
                return new ReleasePreparation(ReleasePreparationStatus.Paused);
            }

            if (state.Current is { } installed)
            {
                var order = SemanticVersion.Parse(decision.Release.Version).CompareTo(
                    SemanticVersion.Parse(installed.Version));
                if (order < 0 && !decision.IsRollback)
                {
                    throw new InvalidDataException("The signed decision would downgrade without a rollback authorization.");
                }

                if (order == 0 && decision.Release != installed)
                {
                    throw new InvalidDataException("The signed decision reuses an installed version for different bytes.");
                }

                if (decision.Release == installed)
                {
                    await _stateStore.SaveAsync(
                        state with
                        {
                            SeenGeneration = decision.Generation,
                            LastKnownGood = decision.LastKnownGood,
                        },
                        cancellationToken).ConfigureAwait(false);
                    DeleteStaging(staging);
                    staging = null;
                    return new ReleasePreparation(ReleasePreparationStatus.UpToDate);
                }
            }

            var manifestPath = Path.Combine(staging, decision.Release.ManifestName);
            var manifestBundlePath = manifestPath + ".sigstore.json";
            await _feed.DownloadAssetAsync(
                decision.Release.BuildTag,
                decision.Release.ManifestName,
                manifestPath,
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            await _feed.DownloadAssetAsync(
                decision.Release.BuildTag,
                decision.Release.ManifestName + ".sigstore.json",
                manifestBundlePath,
                ReleaseFeedLimits.MaximumSignatureBytes,
                cancellationToken).ConfigureAwait(false);
            await _verifier.VerifyAsync(manifestPath, manifestBundlePath, cancellationToken).ConfigureAwait(false);
            var manifestSha256 = await Sha256Async(
                manifestPath,
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            if (!manifestSha256.Equals(decision.Release.ManifestSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The verified manifest does not match the signed ring decision.");
            }

            using var manifestDocument = await ReadJsonAsync(
                manifestPath,
                ReleaseFeedLimits.MaximumJsonBytes,
                cancellationToken).ConfigureAwait(false);
            var manifest = ParseManifest(manifestDocument.RootElement, decision.Release);
            if (decision.VerificationRunId is not null &&
                decision.VerificationRunId != manifest.VerificationRunId)
            {
                throw new InvalidDataException(
                    "The canary decision and verified manifest name different producer runs.");
            }

            var selected = SelectArtifacts(manifest, state.ComponentSha256);
            var stagedArtifacts = new List<VerifiedReleaseArtifact>();
            long downloaded = new FileInfo(envelopePath).Length + new FileInfo(manifestPath).Length +
                              new FileInfo(manifestBundlePath).Length;
            foreach (var artifact in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, artifact.Name);
                var bundlePath = path + ".sigstore.json";
                await _feed.DownloadAssetAsync(
                    decision.Release.BuildTag,
                    artifact.Name,
                    path,
                    artifact.Size,
                    cancellationToken).ConfigureAwait(false);
                await _feed.DownloadAssetAsync(
                    decision.Release.BuildTag,
                    artifact.Name + ".sigstore.json",
                    bundlePath,
                    ReleaseFeedLimits.MaximumSignatureBytes,
                    cancellationToken).ConfigureAwait(false);
                await _verifier.VerifyAsync(path, bundlePath, cancellationToken).ConfigureAwait(false);
                var info = RequirePlainFile(path, artifact.Size, artifact.Name);
                var artifactSha256 = await Sha256Async(path, artifact.Size, cancellationToken).ConfigureAwait(false);
                if (info.Length != artifact.Size ||
                    !artifactSha256.Equals(artifact.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Verified artifact {artifact.Name} disagrees with the manifest.");
                }

                downloaded = checked(downloaded + info.Length + new FileInfo(bundlePath).Length);
                if (downloaded > ReleaseFeedLimits.MaximumReleaseBytes)
                {
                    throw new InvalidDataException("The staged release exceeds its total byte limit.");
                }

                stagedArtifacts.Add(new VerifiedReleaseArtifact(
                    artifact.Name,
                    artifact.Component,
                    artifact.Role,
                    artifact.Sha256,
                    artifact.Size,
                    artifact.BaseSha256,
                    path));
            }

            var componentDigests = FeedNames.ToDictionary(
                name => name,
                name => ComponentDigest(name, manifest),
                StringComparer.Ordinal);
            var plan = new VerifiedReleasePlan(
                decision.Release,
                decision.LastKnownGood,
                decision.Generation,
                _options.Ring,
                staging,
                Array.AsReadOnly(stagedArtifacts.ToArray()),
                new ReadOnlyDictionary<string, string>(componentDigests),
                decision.IsRollback,
                decision.AuthorizationAction);
            _pendingPlan = plan;
            staging = null;
            return new ReleasePreparation(ReleasePreparationStatus.Ready, plan);
        }
        catch
        {
            if (staging is not null)
            {
                DeleteStaging(staging);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records a plan only after every component has been activated successfully.</summary>
    public async Task CommitAsync(VerifiedReleasePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequirePendingPlan(plan);
            var state = ValidateState(await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false));
            if (state.SeenGeneration >= plan.Generation)
            {
                throw new InvalidOperationException("This authenticated release decision is already resolved.");
            }

            await _stateStore.SaveAsync(
                new ReleaseConsumerState(
                    plan.Generation,
                    plan.Release,
                    plan.LastKnownGood,
                    new Dictionary<string, string>(plan.Components, StringComparer.Ordinal),
                    Refused: null),
                cancellationToken).ConfigureAwait(false);
            _pendingPlan = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Persists an explicit refusal so the same generation is not silently retried.</summary>
    public async Task RefuseAsync(VerifiedReleasePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequirePendingPlan(plan);
            var state = ValidateState(await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false));
            if (state.SeenGeneration >= plan.Generation)
            {
                throw new InvalidOperationException("This authenticated release decision is already resolved.");
            }

            await _stateStore.SaveAsync(
                state with
                {
                    SeenGeneration = plan.Generation,
                    LastKnownGood = plan.LastKnownGood,
                    Refused = plan.Release,
                },
                cancellationToken).ConfigureAwait(false);
            _pendingPlan = null;
            DeleteStaging(plan.Directory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Abandon(VerifiedReleasePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        _gate.Wait();
        try
        {
            RequirePendingPlan(plan);
            DeleteStaging(plan.Directory);
            _pendingPlan = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Payload, string Bundle)> UnpackEnvelopeAsync(
        string envelopePath,
        string staging,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(
            envelopePath,
            ReleaseFeedLimits.MaximumJsonBytes,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !HasExactly(root, "schemaVersion", "mediaType", "payloadBase64", "sigstoreBundle") ||
            !TryGetInt64(root, "schemaVersion", out var schema) || schema != 1 ||
            !TryGetString(root, "mediaType", out var mediaType) || mediaType != EnvelopeMediaType ||
            !TryGetString(root, "payloadBase64", out var encoded) ||
            !root.TryGetProperty("sigstoreBundle", out var bundle) || bundle.ValueKind != JsonValueKind.Object ||
            encoded.Length > checked(((ReleaseFeedLimits.MaximumJsonBytes + 2) / 3) * 4))
        {
            throw new InvalidDataException("The signed ring envelope is malformed.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The signed ring payload is not valid base64.", exception);
        }

        var bundleBytes = JsonSerializer.SerializeToUtf8Bytes(bundle);
        if (payload.Length is <= 0 or > ReleaseFeedLimits.MaximumJsonBytes ||
            bundleBytes.Length is <= 0 or > ReleaseFeedLimits.MaximumSignatureBytes)
        {
            throw new InvalidDataException("The signed ring envelope exceeds its component byte limits.");
        }

        var payloadPath = Path.Combine(staging, "release-index.json");
        var bundlePath = payloadPath + ".sigstore.json";
        await WriteNewAsync(payloadPath, payload, cancellationToken).ConfigureAwait(false);
        await WriteNewAsync(bundlePath, bundleBytes, cancellationToken).ConfigureAwait(false);
        return (payloadPath, bundlePath);
    }

    private RingDecision ParseDecision(JsonElement root, long fileGeneration)
    {
        var expected = new[]
        {
            "schemaVersion", "mediaType", "feedRepository", "ring", "generation", "updatedUtc", "paused",
            "release", "previous", "lastKnownGood", "highWaterVersion", "rollback", "authorization",
        };
        if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, expected) ||
            !TryGetInt64(root, "schemaVersion", out var schema) || schema != 1 ||
            !TryGetString(root, "mediaType", out var mediaType) || mediaType != IndexMediaType ||
            !TryGetString(root, "feedRepository", out var repository) || repository != _options.Repository ||
            !TryGetString(root, "ring", out var ring) || ring != _options.Ring ||
            !TryGetInt64(root, "generation", out var generation) ||
            generation != fileGeneration || generation is <= 0 or > ReleaseFeedLimits.MaximumGeneration ||
            !TryGetString(root, "updatedUtc", out var updatedText) ||
            !TryCanonicalTimestamp(updatedText, out var updated) ||
            updated > _clock.GetUtcNow().AddMinutes(5) ||
            !root.TryGetProperty("paused", out var pausedProperty) ||
            pausedProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("release", out var releaseProperty) ||
            !TryGetString(root, "highWaterVersion", out var highWaterText) ||
            !root.TryGetProperty("authorization", out var authorization))
        {
            throw new InvalidDataException("The verified release-index identity is malformed.");
        }

        var release = ParseIdentity(releaseProperty, "release");
        var highWater = SemanticVersion.Parse(highWaterText);
        if (highWater.CompareTo(SemanticVersion.Parse(release.Version)) < 0)
        {
            throw new InvalidDataException("The release-index high-water version is below its release.");
        }

        var previous = ParseOptionalIdentity(root.GetProperty("previous"), "previous");
        var lastKnownGood = ParseOptionalIdentity(root.GetProperty("lastKnownGood"), "lastKnownGood");
        var rollbackProperty = root.GetProperty("rollback");
        var isRollback = rollbackProperty.ValueKind != JsonValueKind.Null;
        if (isRollback)
        {
            if (rollbackProperty.ValueKind != JsonValueKind.Object ||
                !HasExactly(rollbackProperty, "generation", "from") ||
                !TryGetInt64(rollbackProperty, "generation", out var rollbackGeneration) ||
                rollbackGeneration is <= 0 || rollbackGeneration > generation ||
                !rollbackProperty.TryGetProperty("from", out var rollbackFrom))
            {
                throw new InvalidDataException("The release-index rollback authorization is malformed.");
            }

            var rollbackIdentity = ParseIdentity(rollbackFrom, "rollback.from");
            if (rollbackIdentity == release || rollbackIdentity != previous || lastKnownGood != release)
            {
                throw new InvalidDataException("The release-index rollback authorization is inconsistent.");
            }
        }

        if (!TryParseAuthorization(
                authorization,
                generation,
                _options.Ring,
                pausedProperty.GetBoolean(),
                isRollback,
                out var action,
                out var verificationRunId))
        {
            throw new InvalidDataException("The release-index authorization is malformed.");
        }

        return new RingDecision(
            generation,
            pausedProperty.GetBoolean(),
            release,
            previous,
            lastKnownGood,
            isRollback,
            action,
            verificationRunId);
    }

    private static ReleaseManifest ParseManifest(JsonElement root, ReleaseIdentity release)
    {
        var expected = new[]
        {
            "schemaVersion", "version", "commit", "builtUtc", "source", "versions", "artifacts", "binaries", "feeds",
        };
        if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, expected) ||
            !TryGetInt64(root, "schemaVersion", out var schema) || schema != 1 ||
            !TryGetString(root, "version", out var version) || version != release.Version ||
            !TryGetString(root, "commit", out var commit) || commit != release.Commit ||
            !TryGetString(root, "builtUtc", out var builtUtc) || !TryCanonicalTimestamp(builtUtc, out _) ||
            !root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !HasExactly(source, "repository", "branch", "verificationWorkflow", "verificationRunId",
                "verificationRunAttempt", "verificationArtifacts") ||
            !TryGetString(source, "repository", out var sourceRepository) || sourceRepository != SourceRepository ||
            !TryGetString(source, "branch", out var branch) || branch != "main" ||
            !TryGetString(source, "verificationWorkflow", out var workflow) ||
            workflow != ".github/workflows/windows-verify.yml" ||
            !TryGetString(source, "verificationRunId", out var verificationRunId) ||
            !IsDecimal(verificationRunId) ||
            !TryGetInt64(source, "verificationRunAttempt", out var verificationRunAttempt) ||
            verificationRunAttempt <= 0 ||
            !source.TryGetProperty("verificationArtifacts", out var verificationArtifacts) ||
            verificationArtifacts.ValueKind != JsonValueKind.Array ||
            verificationArtifacts.GetArrayLength() is <= 0 or > ReleaseFeedLimits.MaximumArtifacts ||
            !root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object ||
            !HasExactly(versions, "package", "assemblyInformational", "manifest", "commit", "databaseSchema",
                "relayProtocol", "v2Contract", "questExchange") ||
            !TryGetString(versions, "package", out var package) || package != version ||
            !TryGetString(versions, "manifest", out var manifestVersion) || manifestVersion != version ||
            !TryGetString(versions, "commit", out var versionCommit) || versionCommit != commit ||
            !TryGetString(versions, "assemblyInformational", out var informational) ||
            informational != $"{version}+{commit}" ||
            !root.TryGetProperty("binaries", out var binaries) || binaries.ValueKind != JsonValueKind.Array ||
            binaries.GetArrayLength() is <= 0 or > ReleaseFeedLimits.MaximumArtifacts ||
            !root.TryGetProperty("artifacts", out var artifactArray) ||
            artifactArray.ValueKind != JsonValueKind.Array ||
            artifactArray.GetArrayLength() is <= 0 or > ReleaseFeedLimits.MaximumArtifacts ||
            !root.TryGetProperty("feeds", out var feeds) || feeds.ValueKind != JsonValueKind.Object ||
            !HasExactly(feeds, FeedNames))
        {
            throw new InvalidDataException("The verified release manifest has an inconsistent identity.");
        }

        _ = SemanticVersion.Parse(version);
        ValidateVersionInventory(versions);
        ValidateVerificationArtifacts(verificationArtifacts);
        var artifacts = new Dictionary<string, ReleaseArtifact>(StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var item in artifactArray.EnumerateArray())
        {
            var artifact = ParseArtifact(item);
            if (!artifacts.TryAdd(artifact.Name, artifact))
            {
                throw new InvalidDataException($"The release manifest repeats artifact {artifact.Name}.");
            }

            totalSize = checked(totalSize + artifact.Size);
            if (totalSize > ReleaseFeedLimits.MaximumReleaseBytes)
            {
                throw new InvalidDataException("The release manifest exceeds its total byte limit.");
            }
        }

        ValidateBinaryInventory(binaries, artifacts);

        var feedArtifacts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var allFeedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feedName in FeedNames)
        {
            var value = feeds.GetProperty(feedName);
            if (value.ValueKind != JsonValueKind.Object || !HasExactly(value, "authentication", "artifacts") ||
                !TryGetString(value, "authentication", out var authentication) || authentication != "required" ||
                !value.TryGetProperty("artifacts", out var names) || names.ValueKind != JsonValueKind.Array ||
                names.GetArrayLength() is <= 0 or > ReleaseFeedLimits.MaximumArtifacts)
            {
                throw new InvalidDataException($"The {feedName} feed is not private or is empty.");
            }

            var values = new List<string>();
            foreach (var nameValue in names.EnumerateArray())
            {
                var name = nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
                if (name is null || !artifacts.TryGetValue(name, out var artifact) ||
                    !BelongsToFeed(artifact.Component, feedName) || !allFeedNames.Add(name))
                {
                    throw new InvalidDataException($"The {feedName} feed names an invalid or repeated artifact.");
                }

                values.Add(name);
            }

            feedArtifacts.Add(feedName, values);
        }

        foreach (var artifact in artifacts.Values.Where(value => value.Component is "desktop" or "relay" or "data" or "model"))
        {
            if (!allFeedNames.Contains(artifact.Name))
            {
                throw new InvalidDataException($"Installable artifact {artifact.Name} is absent from its feed.");
            }
        }

        RequireRole(artifacts.Values, "desktop", "full-package");
        RequireNamedRole(artifacts.Values, "releases.win.json", "desktop", "velopack-feed");
        RequireRole(artifacts.Values, "relay", "archive");
        RequireRole(artifacts.Values, "data", "full");
        RequireRole(artifacts.Values, "model", "full");
        return new ReleaseManifest(artifacts, feedArtifacts, verificationRunId);
    }

    private static ReleaseArtifact ParseArtifact(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !HasOnlyOrExactly(value, ["name", "component", "role", "sha256", "size"], "baseSha256") ||
            !TryGetString(value, "name", out var name) || !IsSafeName(name) ||
            !TryGetString(value, "component", out var component) ||
            component is not ("desktop" or "relay" or "data" or "model" or "release") ||
            !TryGetString(value, "role", out var role) || !Role().IsMatch(role) ||
            !TryGetString(value, "sha256", out var sha256) || !LowerHex64().IsMatch(sha256) ||
            !TryGetInt64(value, "size", out var size) || size is <= 0 or > ReleaseFeedLimits.MaximumArtifactBytes)
        {
            throw new InvalidDataException("The release manifest contains a malformed artifact.");
        }

        string? baseSha256 = null;
        if (value.TryGetProperty("baseSha256", out var baseProperty))
        {
            baseSha256 = baseProperty.ValueKind == JsonValueKind.String ? baseProperty.GetString() : null;
            if (baseSha256 is null || !LowerHex64().IsMatch(baseSha256) || !IsDeltaRole(role))
            {
                throw new InvalidDataException($"Artifact {name} has an invalid delta base.");
            }
        }
        else if (component is "data" or "model" && role == "delta")
        {
            throw new InvalidDataException($"Delta artifact {name} has no authenticated base digest.");
        }

        return new ReleaseArtifact(name, component, role, sha256, size, baseSha256);
    }

    private static void ValidateVersionInventory(JsonElement versions)
    {
        if (!TryGetString(versions, "databaseSchema", out var databaseSchema) ||
            !FourDigits().IsMatch(databaseSchema) ||
            !TryGetInt64(versions, "relayProtocol", out var relayProtocol) ||
            relayProtocol is < 0 or > int.MaxValue ||
            !TryGetString(versions, "v2Contract", out var v2Contract) ||
            !BoundedContractVersion().IsMatch(v2Contract) ||
            !TryGetInt64(versions, "questExchange", out var questExchange) ||
            questExchange is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException("The release manifest's contract versions are malformed.");
        }
    }

    private static void ValidateVerificationArtifacts(JsonElement values)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !HasExactly(value, "name", "sha256", "size") ||
                !TryGetString(value, "name", out var name) || !IsSafeName(name) || !names.Add(name) ||
                !TryGetString(value, "sha256", out var sha256) || !LowerHex64().IsMatch(sha256) ||
                !TryGetInt64(value, "size", out var size) ||
                size is <= 0 or > ReleaseFeedLimits.MaximumArtifactBytes)
            {
                throw new InvalidDataException("The release manifest's verification-artifact record is malformed.");
            }
        }

        if (!names.SetEquals(["windows-release-payload", "group-server-release"]))
        {
            throw new InvalidDataException("The release manifest does not name both producer artifacts.");
        }
    }

    private static void ValidateBinaryInventory(
        JsonElement values,
        IReadOnlyDictionary<string, ReleaseArtifact> artifacts)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !HasExactly(value, "component", "archive", "path", "sha256", "size") ||
                !TryGetString(value, "component", out var component) || component is not ("desktop" or "relay") ||
                !TryGetString(value, "archive", out var archive) || !IsSafeName(archive) ||
                !artifacts.TryGetValue(archive, out var archiveArtifact) ||
                archiveArtifact.Component != component ||
                archiveArtifact.Role is not ("portable-archive" or "full-package" or "archive") ||
                !TryGetString(value, "path", out var path) || !IsSafeArchivePath(path) ||
                !identities.Add($"{archive}\0{path}") ||
                !TryGetString(value, "sha256", out var sha256) || !LowerHex64().IsMatch(sha256) ||
                !TryGetInt64(value, "size", out var size) ||
                size is <= 0 or > ReleaseFeedLimits.MaximumArtifactBytes)
            {
                throw new InvalidDataException("The release manifest's binary inventory is malformed.");
            }
        }
    }

    private static IReadOnlyList<ReleaseArtifact> SelectArtifacts(
        ReleaseManifest manifest,
        IReadOnlyDictionary<string, string> installedComponents)
    {
        var selected = new List<ReleaseArtifact>();
        foreach (var feedName in FeedNames)
        {
            foreach (var name in manifest.Feeds[feedName])
            {
                var artifact = manifest.Artifacts[name];
                if (IsDeltaRole(artifact.Role) &&
                    artifact.BaseSha256 is { } baseSha &&
                    (!installedComponents.TryGetValue(feedName, out var installed) || installed != baseSha))
                {
                    continue;
                }

                selected.Add(artifact);
            }
        }

        return selected;
    }

    private static string ComponentDigest(string feedName, ReleaseManifest manifest)
    {
        var canonical = manifest.Feeds[feedName]
            .Select(name => manifest.Artifacts[name])
            .Where(artifact => feedName switch
            {
                "binary" => artifact.Component == "desktop" && artifact.Role == "full-package",
                "data" or "model" => artifact.Role == "full",
                _ => false,
            })
            .ToArray();
        if (canonical.Length != 1)
        {
            throw new InvalidDataException($"The {feedName} feed has no unique full component artifact.");
        }

        return canonical[0].Sha256;
    }

    private static ReleaseIdentity ParseIdentity(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !HasExactly(value, "version", "commit", "buildTag", "manifestName", "manifestSha256") ||
            !TryGetString(value, "version", out var version) || version.Length > 128 ||
            !TryGetString(value, "commit", out var commit) || !LowerHex40().IsMatch(commit) ||
            !TryGetString(value, "buildTag", out var buildTag) || buildTag != $"v2-build-{version}" ||
            !TryGetString(value, "manifestName", out var manifestName) || manifestName != "release-manifest.json" ||
            !TryGetString(value, "manifestSha256", out var manifestSha256) || !LowerHex64().IsMatch(manifestSha256))
        {
            throw new InvalidDataException($"The release-index {label} identity is malformed.");
        }

        _ = SemanticVersion.Parse(version);
        return new ReleaseIdentity(version, commit, buildTag, manifestName, manifestSha256);
    }

    private static ReleaseIdentity? ParseOptionalIdentity(JsonElement value, string label) =>
        value.ValueKind == JsonValueKind.Null ? null : ParseIdentity(value, label);

    private static ReleaseConsumerState ValidateState(ReleaseConsumerState? state)
    {
        if (state is null || state.SeenGeneration is < 0 or > ReleaseFeedLimits.MaximumGeneration ||
            state.ComponentSha256 is null ||
            state.ComponentSha256.Any(item => !FeedNames.Contains(item.Key, StringComparer.Ordinal) ||
                                               !LowerHex64().IsMatch(item.Value ?? string.Empty)))
        {
            throw new InvalidDataException("The persisted release state is malformed.");
        }

        foreach (var identity in new[] { state.Current, state.LastKnownGood, state.Refused })
        {
            if (identity is not null)
            {
                ValidateIdentityRecord(identity);
            }
        }

        return state;
    }

    private void ValidatePlan(VerifiedReleasePlan plan)
    {
        if (plan.Release is null)
        {
            throw new ArgumentException("The verified release plan has no release identity.", nameof(plan));
        }

        ValidateIdentityRecord(plan.Release);
        if (plan.LastKnownGood is not null)
        {
            ValidateIdentityRecord(plan.LastKnownGood);
        }

        if (plan.Generation is <= 0 or > ReleaseFeedLimits.MaximumGeneration || plan.Ring != _options.Ring ||
            plan.Artifacts is null || plan.Artifacts.Count is <= 0 or > ReleaseFeedLimits.MaximumArtifacts ||
            plan.Artifacts.Any(artifact => artifact is null) ||
            plan.Artifacts.Select(artifact => artifact.Name).Distinct(StringComparer.Ordinal).Count() !=
            plan.Artifacts.Count ||
            plan.Artifacts.Any(artifact =>
                !IsSafeName(artifact.Name) ||
                artifact.Component is not ("desktop" or "relay" or "data" or "model") ||
                !Role().IsMatch(artifact.Role ?? string.Empty) ||
                !LowerHex64().IsMatch(artifact.Sha256 ?? string.Empty) ||
                artifact.Size is <= 0 or > ReleaseFeedLimits.MaximumArtifactBytes ||
                artifact.BaseSha256 is not null &&
                (!LowerHex64().IsMatch(artifact.BaseSha256) || !IsDeltaRole(artifact.Role)) ||
                artifact.Component is "data" or "model" && artifact.Role == "delta" &&
                artifact.BaseSha256 is null ||
                !IsDirectChild(artifact.Path, plan.Directory)) ||
            plan.Components is null ||
            plan.Components.Count != FeedNames.Length ||
            FeedNames.Any(name => !plan.Components.TryGetValue(name, out var digest) || !LowerHex64().IsMatch(digest)) ||
            !HasPlanComponent(plan, "binary", "desktop", "full-package") ||
            !HasPlanComponent(plan, "data", "data", "full") ||
            !HasPlanComponent(plan, "model", "model", "full") ||
            plan.AuthorizationAction is not ("publish" or "promote" or "pause" or "resume" or "mark-lkg" or "rollback") ||
            plan.AuthorizationAction == "rollback" && !plan.IsRollback ||
            plan.IsRollback && plan.AuthorizationAction is not ("rollback" or "pause" or "resume") ||
            plan.IsRollback && plan.LastKnownGood != plan.Release ||
            !IsOwnedStagingDirectory(plan.Directory))
        {
            throw new ArgumentException("The verified release plan is invalid.", nameof(plan));
        }
    }

    private void RequirePendingPlan(VerifiedReleasePlan plan)
    {
        if (!ReferenceEquals(plan, _pendingPlan))
        {
            throw new InvalidOperationException("The release plan was not prepared by this consumer or is no longer pending.");
        }
    }

    private static bool HasPlanComponent(
        VerifiedReleasePlan plan,
        string feed,
        string component,
        string role)
    {
        if (!plan.Components.TryGetValue(feed, out var expected))
        {
            return false;
        }

        var values = plan.Artifacts
            .Where(artifact => artifact is not null && artifact.Component == component && artifact.Role == role)
            .ToArray();
        return values.Length == 1 && values[0].Sha256 == expected;
    }

    private static bool IsDirectChild(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return parent?.Equals(expected, comparison) == true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateIdentityRecord(ReleaseIdentity identity)
    {
        _ = SemanticVersion.Parse(identity.Version);
        if (!LowerHex40().IsMatch(identity.Commit) || identity.BuildTag != $"v2-build-{identity.Version}" ||
            identity.ManifestName != "release-manifest.json" || !LowerHex64().IsMatch(identity.ManifestSha256))
        {
            throw new InvalidDataException("A persisted release identity is malformed.");
        }
    }

    private string CreateStagingDirectory()
    {
        var root = Path.GetFullPath(_options.StagingRoot);
        Directory.CreateDirectory(root);
        RequireUnredirectedDirectoryTree(root, "release staging root");

        var staging = Path.Combine(root, $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        RequireUnredirectedDirectoryTree(staging, "release staging directory");
        return staging;
    }

    private void DeleteStaging(string path)
    {
        if (!IsOwnedStagingDirectory(path))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the release staging root.");
        }

        var info = new DirectoryInfo(Path.GetFullPath(path));
        info.Refresh();
        if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
        {
            throw new InvalidOperationException("Refusing to follow a redirected release staging directory.");
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private bool IsOwnedStagingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.StagingRoot));
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(candidate);
        return Path.GetDirectoryName(candidate)?.Equals(root, comparison) == true &&
               name.StartsWith("release-", StringComparison.Ordinal) &&
               Guid.TryParseExact(name["release-".Length..], "N", out _);
    }

    private static void RequireUnredirectedDirectoryTree(string path, string label)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            current.Refresh();
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint) || current.LinkTarget is not null)
            {
                throw new InvalidDataException($"The {label} is missing or redirected.");
            }
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = RequirePlainFile(path, maximumBytes, "release JSON");
        var bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(
            info.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"{path} changed while it was being read.");
            }

            offset += read;
        }

        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ReleaseFeedLimits.MaximumJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{path} is not bounded valid JSON.", exception);
        }
    }

    private static FileInfo RequirePlainFile(string path, long maximumBytes, string label)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"{label} is missing, redirected, empty, or outside its byte limit.");
        }

        return info;
    }

    private static async Task<string> Sha256Async(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rented = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("A release file grew beyond its authenticated byte limit.");
                }

                hash.AppendData(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static (long Generation, string Name)? SelectCurrent(IReadOnlyList<string> names)
    {
        (long Generation, string Name)? current = null;
        foreach (var name in names)
        {
            var match = RingFile().Match(name);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var generation) || generation is <= 0 or > ReleaseFeedLimits.MaximumGeneration)
            {
                continue;
            }

            if (current is null || generation > current.Value.Generation)
            {
                current = (generation, name);
            }
        }

        return current;
    }

    private static bool TryCanonicalTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);

    private static bool TryGetString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString()!;
        return true;
    }

    private static bool TryGetInt64(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out result);
    }

    private static bool TryParseAuthorization(
        JsonElement authorization,
        long generation,
        string ring,
        bool paused,
        bool isRollback,
        out string action,
        out string? verificationRunId)
    {
        action = string.Empty;
        verificationRunId = null;
        var expected = new[]
        {
            "action", "actor", "reason", "workflowRunId", "verificationRunId", "sourceRing",
            "sourceGeneration", "previousGeneration",
        };
        if (authorization.ValueKind != JsonValueKind.Object || !HasExactly(authorization, expected) ||
            !TryGetString(authorization, "action", out action) ||
            action is not ("publish" or "promote" or "pause" or "resume" or "mark-lkg" or "rollback") ||
            !TryGetString(authorization, "actor", out var actor) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor != actor.Trim() ||
            !TryGetString(authorization, "reason", out var reason) || reason.Length > 2048 ||
            !TryGetString(authorization, "workflowRunId", out var workflowRun) || !IsDecimal(workflowRun) ||
            !TryGetInt64(authorization, "previousGeneration", out var previousGeneration) ||
            previousGeneration != generation - 1 ||
            !TryGetOptionalString(authorization, "verificationRunId", out verificationRunId) ||
            verificationRunId is not null && !IsDecimal(verificationRunId) ||
            !TryGetOptionalString(authorization, "sourceRing", out var sourceRing) ||
            sourceRing is not null && sourceRing is not ("canary" or "beta") ||
            !TryGetOptionalInt64(authorization, "sourceGeneration", out var sourceGeneration) ||
            sourceGeneration is long sourceGenerationValue &&
            (sourceGenerationValue <= 0 || sourceGenerationValue > ReleaseFeedLimits.MaximumGeneration) ||
            (sourceRing is null) != (sourceGeneration is null) ||
            (action == "promote") != (sourceRing is not null) ||
            action == "promote" && sourceRing != (ring == "beta" ? "canary" : ring == "stable" ? "beta" : null) ||
            action == "publish" && (ring != "canary" || verificationRunId is null) ||
            action != "publish" && verificationRunId is not null ||
            (action is "publish" or "promote") && paused ||
            action == "pause" && !paused || action == "resume" && paused ||
            action == "rollback" && !isRollback ||
            isRollback && action is not ("rollback" or "pause" or "resume"))
        {
            action = string.Empty;
            verificationRunId = null;
            return false;
        }

        return true;
    }

    private static bool TryGetOptionalString(JsonElement value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return result is not null;
    }

    private static bool TryGetOptionalInt64(JsonElement value, string name, out long? result)
    {
        result = null;
        if (!value.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number))
        {
            return false;
        }

        result = number;
        return true;
    }

    private static bool IsDecimal(string value) =>
        value.Length is > 0 and <= 20 && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private static bool HasExactly(JsonElement value, params string[] expected)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool HasOnlyOrExactly(JsonElement value, string[] required, string optional)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == names.Distinct(StringComparer.Ordinal).Count() &&
               required.All(name => names.Contains(name, StringComparer.Ordinal)) &&
               names.All(name => required.Contains(name, StringComparer.Ordinal) || name == optional);
    }

    private static bool BelongsToFeed(string component, string feed) => feed switch
    {
        "binary" => component is "desktop" or "relay",
        "data" => component == "data",
        "model" => component == "model",
        _ => false,
    };

    private static void RequireRole(IEnumerable<ReleaseArtifact> artifacts, string component, string role)
    {
        if (artifacts.Count(value => value.Component == component && value.Role == role) != 1)
        {
            throw new InvalidDataException($"The release manifest does not have exactly one {component} {role} artifact.");
        }
    }

    private static void RequireNamedRole(
        IEnumerable<ReleaseArtifact> artifacts,
        string name,
        string component,
        string role)
    {
        if (!artifacts.Any(value => value.Name == name && value.Component == component && value.Role == role))
        {
            throw new InvalidDataException($"The release manifest has no authenticated {name} artifact.");
        }
    }

    private static bool IsSafeName(string value) => value.Length <= 200 && SafeName().IsMatch(value);

    private static bool IsDeltaRole(string role) => role is "delta" or "delta-package";

    private static bool IsSafeArchivePath(string value) =>
        value.Length is > 0 and <= 1024 &&
        value[0] != '/' &&
        !value.Contains('\\') &&
        value.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private sealed record RingDecision(
        long Generation,
        bool Paused,
        ReleaseIdentity Release,
        ReleaseIdentity? Previous,
        ReleaseIdentity? LastKnownGood,
        bool IsRollback,
        string AuthorizationAction,
        string? VerificationRunId);

    private sealed record ReleaseArtifact(
        string Name,
        string Component,
        string Role,
        string Sha256,
        long Size,
        string? BaseSha256);

    private sealed record ReleaseManifest(
        IReadOnlyDictionary<string, ReleaseArtifact> Artifacts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Feeds,
        string VerificationRunId);

    private readonly record struct SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<string>? Prerelease) : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                throw new InvalidDataException($"{value} is not a supported bounded semantic version.");
            }

            var match = SemVer().Match(value ?? string.Empty);
            if (!match.Success || !TryNumber(match.Groups[1].Value, out var major) ||
                !TryNumber(match.Groups[2].Value, out var minor) ||
                !TryNumber(match.Groups[3].Value, out var patch))
            {
                throw new InvalidDataException($"{value} is not a supported bounded semantic version.");
            }

            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : null;
            if (prerelease is not null && prerelease.Any(part =>
                    part.All(char.IsAsciiDigit) && !TryNumber(part, out _)))
            {
                throw new InvalidDataException($"{value} has an out-of-range prerelease identifier.");
            }

            return new SemanticVersion(major, minor, patch, prerelease);
        }

        public int CompareTo(SemanticVersion other)
        {
            var numeric = Major.CompareTo(other.Major);
            if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
            if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
            if (numeric != 0) return numeric;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
            {
                var left = Prerelease[index];
                var right = other.Prerelease[index];
                var leftNumeric = left.All(char.IsAsciiDigit);
                var rightNumeric = right.All(char.IsAsciiDigit);
                int order;
                if (leftNumeric && rightNumeric)
                {
                    order = int.Parse(left, CultureInfo.InvariantCulture)
                        .CompareTo(int.Parse(right, CultureInfo.InvariantCulture));
                }
                else if (leftNumeric != rightNumeric)
                {
                    order = leftNumeric ? -1 : 1;
                }
                else
                {
                    order = string.CompareOrdinal(left, right);
                }

                if (order != 0) return order;
            }

            return Prerelease.Count.CompareTo(other.Prerelease.Count);
        }

        private static bool TryNumber(string value, out int number) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Repository();

    [GeneratedRegex("^release-index-g([0-9]{10})\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RingFile();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Role();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex40();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();

    [GeneratedRegex("^[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FourDigits();

    [GeneratedRegex("^(?:0|[1-9][0-9]{0,9})\\.(?:0|[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex BoundedContractVersion();

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();
}
