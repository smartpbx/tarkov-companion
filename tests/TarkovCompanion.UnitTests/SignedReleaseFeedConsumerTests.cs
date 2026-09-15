using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Application.Services.Updates;
using TarkovCompanion.Infrastructure.Updates;

namespace TarkovCompanion.UnitTests;

public sealed class SignedReleaseFeedConsumerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-release-consumer-{Guid.NewGuid():N}");

    [Fact]
    public async Task OneAuthenticatedDecisionStagesBinaryDataAndModelTogether()
    {
        var fixture = CreateFixture("2.0.0");

        var prepared = await fixture.Consumer.PrepareAsync(default);

        Assert.Equal(ReleasePreparationStatus.Ready, prepared.Status);
        var plan = Assert.IsType<VerifiedReleasePlan>(prepared.Plan);
        Assert.Equal(5, plan.Artifacts.Count);
        Assert.Equal(new[] { "binary", "data", "model" }, plan.Components.Keys.Order(StringComparer.Ordinal));
        Assert.All(plan.Artifacts, artifact => Assert.True(File.Exists(artifact.Path)));
        Assert.Equal(7, fixture.Verifier.Verified.Count); // index, manifest, and five feed artifacts
        Assert.Equal(0, fixture.State.Value.SeenGeneration);

        await fixture.Consumer.CommitAsync(plan, default);

        Assert.Equal(1, fixture.State.Value.SeenGeneration);
        Assert.Equal("2.0.0", fixture.State.Value.Current?.Version);
        Assert.Equal(
            plan.Components.OrderBy(item => item.Key, StringComparer.Ordinal),
            fixture.State.Value.ComponentSha256.OrderBy(item => item.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OnlyTheExactPendingPlanCanAdvanceAuthenticatedState()
    {
        var fixture = CreateFixture("2.0.0");
        var prepared = await fixture.Consumer.PrepareAsync(default);
        var plan = Assert.IsType<VerifiedReleasePlan>(prepared.Plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Consumer.CommitAsync(plan with { }, default));
        Assert.Equal(0, fixture.State.Value.SeenGeneration);

        await fixture.Consumer.CommitAsync(plan, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Consumer.CommitAsync(plan, default));
        Assert.Equal(1, fixture.State.Value.SeenGeneration);
    }

    [Fact]
    public async Task APreparedPlanMustBeResolvedBeforeAnotherCheck()
    {
        var fixture = CreateFixture("2.0.0");
        var prepared = await fixture.Consumer.PrepareAsync(default);
        var plan = Assert.IsType<VerifiedReleasePlan>(prepared.Plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Consumer.PrepareAsync(default));

        fixture.Consumer.Abandon(plan);
        var replacement = await fixture.Consumer.PrepareAsync(default);
        Assert.Equal(ReleasePreparationStatus.Ready, replacement.Status);
    }

    [Fact]
    public async Task APlainPauseAdvancesOnlyAuthenticatedRingStateAndDownloadsNoBuild()
    {
        var fixture = CreateFixture("2.0.0", paused: true);

        var prepared = await fixture.Consumer.PrepareAsync(default);

        Assert.Equal(ReleasePreparationStatus.Paused, prepared.Status);
        Assert.Null(prepared.Plan);
        Assert.Equal(1, fixture.State.Value.SeenGeneration);
        Assert.Empty(fixture.Feed.AssetDownloads);
        Assert.Single(fixture.Verifier.Verified);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public async Task AnUnsignedDowngradeIsRefusedWithoutChangingState()
    {
        var installed = Identity("2.0.0", '2');
        var initial = ReleaseConsumerState.Empty with { Current = installed };
        var fixture = CreateFixture("1.0.0", state: initial);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Consumer.PrepareAsync(default));

        Assert.Equal(initial, fixture.State.Value);
        Assert.Empty(fixture.Feed.AssetDownloads);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public async Task AVerifiedRollbackCanMoveToTheSignedLastKnownGoodRelease()
    {
        var installed = Identity("2.0.0", '2');
        var target = Identity("1.0.0", '1');
        var fixture = CreateFixture(
            "1.0.0",
            state: ReleaseConsumerState.Empty with { Current = installed },
            release: target,
            lastKnownGood: target,
            rollbackFrom: installed,
            action: "rollback");

        var prepared = await fixture.Consumer.PrepareAsync(default);

        var plan = Assert.IsType<VerifiedReleasePlan>(prepared.Plan);
        Assert.True(plan.IsRollback);
        Assert.Equal("1.0.0", plan.Release.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AComponentDeltaIsFetchedOnlyForItsAuthenticatedBase(bool baseMatches)
    {
        const string baseDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var initial = ReleaseConsumerState.Empty with
        {
            Current = Identity("1.0.0", '1'),
            ComponentSha256 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["data"] = baseMatches
                    ? baseDigest
                    : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            },
        };
        var fixture = CreateFixture("2.0.0", state: initial, dataDeltaBase: baseDigest);

        var prepared = await fixture.Consumer.PrepareAsync(default);

        Assert.Equal(ReleasePreparationStatus.Ready, prepared.Status);
        Assert.Equal(
            baseMatches,
            fixture.Feed.AssetDownloads.Contains("TarkovCompanion-data-2.0.0.delta"));
        Assert.Contains("TarkovCompanion-data-2.0.0.json", fixture.Feed.AssetDownloads);
        var plan = Assert.IsType<VerifiedReleasePlan>(prepared.Plan);
        Assert.Equal(Sha256("{\"data\":true}"u8.ToArray()), plan.Components["data"]);
        if (baseMatches)
        {
            var delta = Assert.Single(plan.Artifacts, artifact => artifact.Role == "delta");
            Assert.Equal("data", delta.Component);
            Assert.Equal(baseDigest, delta.BaseSha256);
        }
        else
        {
            Assert.DoesNotContain(plan.Artifacts, artifact => artifact.Role == "delta");
        }
    }

    [Fact]
    public async Task ADecisionDatedImplausiblyInTheFutureIsRefusedBeforeBuildAccess()
    {
        var fixture = CreateFixture("2.0.0", updatedUtc: Now.AddMinutes(6));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Consumer.PrepareAsync(default));

        Assert.Empty(fixture.Feed.AssetDownloads);
        Assert.Equal(0, fixture.State.Value.SeenGeneration);
    }

    [Fact]
    public async Task AFeedReplayBelowThePersistedGenerationIsRefused()
    {
        var initial = ReleaseConsumerState.Empty with { SeenGeneration = 2 };
        var fixture = CreateFixture("2.0.0", state: initial);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Consumer.PrepareAsync(default));

        Assert.Empty(fixture.Verifier.Verified);
        Assert.Equal(initial, fixture.State.Value);
    }

    private Fixture CreateFixture(
        string version,
        bool paused = false,
        ReleaseConsumerState? state = null,
        ReleaseIdentity? release = null,
        ReleaseIdentity? lastKnownGood = null,
        ReleaseIdentity? rollbackFrom = null,
        string? action = null,
        string? dataDeltaBase = null,
        DateTimeOffset? updatedUtc = null)
    {
        Directory.CreateDirectory(_root);
        var options = new SignedReleaseFeedOptions
        {
            Repository = "smartpbx/tarkov-companion-private-feed",
            Ring = "stable",
            StagingRoot = _root,
            CosignPath = Path.Combine(_root, "cosign.exe"),
            CosignSha256 = "9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be",
            TrustRootPath = Path.Combine(_root, "trusted-root.json"),
        };
        var feed = new FakeFeed();
        var verifier = new FakeVerifier();
        var store = new FakeStateStore(state ?? ReleaseConsumerState.Empty);

        var artifactBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"TarkovCompanionDesktop-{version}-full.nupkg"] = "desktop"u8.ToArray(),
            ["releases.win.json"] = "{}"u8.ToArray(),
            [$"TarkovCompanion.GroupServer-{version}-linux-x64.tar.gz"] = "relay"u8.ToArray(),
            [$"TarkovCompanion-data-{version}.json"] = "{\"data\":true}"u8.ToArray(),
            [$"TarkovCompanion-model-eng-{version}.traineddata"] = "model"u8.ToArray(),
        };
        if (dataDeltaBase is not null)
        {
            artifactBytes[$"TarkovCompanion-data-{version}.delta"] = "delta"u8.ToArray();
        }

        var artifacts = new List<Dictionary<string, object?>>
        {
            Artifact($"TarkovCompanionDesktop-{version}-full.nupkg", "desktop", "full-package", artifactBytes),
            Artifact("releases.win.json", "desktop", "velopack-feed", artifactBytes),
            Artifact($"TarkovCompanion.GroupServer-{version}-linux-x64.tar.gz", "relay", "archive", artifactBytes),
            Artifact($"TarkovCompanion-data-{version}.json", "data", "full", artifactBytes),
            Artifact($"TarkovCompanion-model-eng-{version}.traineddata", "model", "full", artifactBytes),
        };
        if (dataDeltaBase is not null)
        {
            var delta = Artifact($"TarkovCompanion-data-{version}.delta", "data", "delta", artifactBytes);
            delta["baseSha256"] = dataDeltaBase;
            artifacts.Add(delta);
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version,
            commit = new string(version[0], 40),
            builtUtc = "2026-09-15T00:00:00Z",
            source = new
            {
                repository = "smartpbx/tarkov-companion",
                branch = "main",
                verificationWorkflow = ".github/workflows/windows-verify.yml",
                verificationRunId = "42",
                verificationRunAttempt = 1,
                verificationArtifacts = new[]
                {
                    new
                    {
                        name = "windows-release-payload",
                        sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        size = 1,
                    },
                    new
                    {
                        name = "group-server-release",
                        sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        size = 1,
                    },
                },
            },
            versions = new
            {
                package = version,
                assemblyInformational = $"{version}+{new string(version[0], 40)}",
                manifest = version,
                commit = new string(version[0], 40),
                databaseSchema = "0001",
                relayProtocol = 1,
                v2Contract = "2.0",
                questExchange = 2,
            },
            artifacts,
            binaries = new[]
            {
                new
                {
                    component = "desktop",
                    archive = $"TarkovCompanionDesktop-{version}-full.nupkg",
                    path = "TarkovCompanion.dll",
                    sha256 = Sha256("desktop binary"u8.ToArray()),
                    size = 14,
                },
            },
            feeds = new
            {
                binary = new
                {
                    authentication = "required",
                    artifacts = new[]
                    {
                        $"TarkovCompanionDesktop-{version}-full.nupkg",
                        "releases.win.json",
                        $"TarkovCompanion.GroupServer-{version}-linux-x64.tar.gz",
                    },
                },
                data = new
                {
                    authentication = "required",
                    artifacts = dataDeltaBase is null
                        ? [$"TarkovCompanion-data-{version}.json"]
                        : new[] { $"TarkovCompanion-data-{version}.json", $"TarkovCompanion-data-{version}.delta" },
                },
                model = new
                {
                    authentication = "required",
                    artifacts = new[] { $"TarkovCompanion-model-eng-{version}.traineddata" },
                },
            },
        }, Json);
        var identityTemplate = release ?? new ReleaseIdentity(
            version,
            new string(version[0], 40),
            $"v2-build-{version}",
            "release-manifest.json",
            string.Empty);
        var identity = identityTemplate with { ManifestSha256 = Sha256(manifestBytes) };
        var effectiveLastKnownGood = release is not null && lastKnownGood == release ? identity : lastKnownGood;
        feed.Assets["release-manifest.json"] = manifestBytes;
        feed.Assets["release-manifest.json.sigstore.json"] = "{}"u8.ToArray();
        foreach (var (name, bytes) in artifactBytes)
        {
            feed.Assets[name] = bytes;
            feed.Assets[name + ".sigstore.json"] = "{}"u8.ToArray();
        }

        var effectiveAction = action ?? (rollbackFrom is not null ? "rollback" : paused ? "pause" : "promote");
        var decisionBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            mediaType = "application/vnd.tarkov-companion.release-index.v1+json",
            feedRepository = options.Repository,
            ring = options.Ring,
            generation = 1,
            updatedUtc = (updatedUtc ?? Now).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            paused,
            release = identity,
            previous = rollbackFrom,
            lastKnownGood = effectiveLastKnownGood,
            highWaterVersion = rollbackFrom?.Version ?? identity.Version,
            rollback = rollbackFrom is null ? null : new { generation = 1, from = rollbackFrom },
            authorization = new
            {
                action = effectiveAction,
                actor = "release-operator",
                reason = "fixture",
                workflowRunId = "43",
                verificationRunId = effectiveAction == "publish" ? "42" : null,
                sourceRing = effectiveAction == "promote" ? "beta" : null,
                sourceGeneration = effectiveAction == "promote" ? 1L : (long?)null,
                previousGeneration = 0,
            },
        }, Json);
        feed.RingFiles["release-index-g0000000001.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            mediaType = "application/vnd.tarkov-companion.signed-release-index.v1+json",
            payloadBase64 = Convert.ToBase64String(decisionBytes),
            sigstoreBundle = new { fixture = true },
        }, Json);

        return new Fixture(
            new SignedReleaseFeedConsumer(
                options,
                feed,
                verifier,
                store,
                new FileSystemReleaseStagingStore(),
                new FixedClock(Now)),
            feed,
            verifier,
            store);
    }

    private static Dictionary<string, object?> Artifact(
        string name,
        string component,
        string role,
        IReadOnlyDictionary<string, byte[]> bytes) => new(StringComparer.Ordinal)
    {
        ["name"] = name,
        ["component"] = component,
        ["role"] = role,
        ["sha256"] = Sha256(bytes[name]),
        ["size"] = bytes[name].LongLength,
    };

    private static ReleaseIdentity Identity(string version, char commit) => new(
        version,
        new string(commit, 40),
        $"v2-build-{version}",
        "release-manifest.json",
        new string(commit, 64));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Fixture(
        SignedReleaseFeedConsumer Consumer,
        FakeFeed Feed,
        FakeVerifier Verifier,
        FakeStateStore State);

    private sealed class FakeFeed : IAuthenticatedReleaseFeed
    {
        public Dictionary<string, byte[]> RingFiles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);

        public HashSet<string> AssetDownloads { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<string>> ListRingAsync(string ring, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(RingFiles.Keys.ToArray());
        }

        public Task DownloadRingAsync(
            string ring,
            string name,
            string destination,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            WriteAsync(RingFiles[name], destination, maximumBytes, cancellationToken);

        public Task DownloadAssetAsync(
            string buildTag,
            string name,
            string destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            AssetDownloads.Add(name);
            return WriteAsync(Assets[name], destination, maximumBytes, cancellationToken);
        }

        private static async Task WriteAsync(
            byte[] value,
            string destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (value.LongLength > maximumBytes)
            {
                throw new InvalidDataException("fixture exceeds requested bound");
            }

            await File.WriteAllBytesAsync(destination, value, cancellationToken);
        }
    }

    private sealed class FakeVerifier : IReleaseSignatureVerifier
    {
        public List<string> Verified { get; } = [];

        public Task VerifyAsync(string filePath, string bundlePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(filePath));
            Assert.True(File.Exists(bundlePath));
            Verified.Add(Path.GetFileName(filePath));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStateStore(ReleaseConsumerState value) : IReleaseConsumerStateStore
    {
        public ReleaseConsumerState Value { get; private set; } = value;

        public Task<ReleaseConsumerState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }

        public Task SaveAsync(ReleaseConsumerState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
