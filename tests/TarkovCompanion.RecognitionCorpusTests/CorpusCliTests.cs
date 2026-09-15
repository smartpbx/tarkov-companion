using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TarkovCompanion.RecognitionCorpus;
using Xunit;

namespace TarkovCompanion.RecognitionCorpusTests;

/// <summary>
/// Drives the real command surface against files in a private scratch root outside the
/// repository, with the clock fixed so emitted and scored bytes can be compared to the goldens.
/// </summary>
public sealed class CorpusCliTests
{
    private static readonly TimeProvider Clock = new CorpusFixtures.FixedTime(CorpusFixtures.GoldenScoredUtc);

    [Fact]
    public async Task EmitRunPlanWritesTheGoldenBytesAndOnlyOutsideRepositories()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var manifest = await CopyGolden(root, "synthetic-manifest.v1.json", "manifest.json");
            var output = Path.Join(root.FullName, "run-plan.json");
            var emitted = await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, output);
            Assert.Empty(emitted.Errors);
            Assert.Equal(0, emitted.Code);

            var bytes = await File.ReadAllBytesAsync(output);
            Assert.Equal(CorpusFixtures.Golden("synthetic-run-plan.v1.json"), Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.NotEqual(Encoding.UTF8.Preamble.ToArray(), bytes.Take(Encoding.UTF8.Preamble.Length).ToArray());

            // Emitting again replaces the file with identical bytes, and the emitted plan validates.
            Assert.Equal(0, (await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, output)).Code);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
            Assert.Equal(0, (await Run("validate-run-plan", output, manifest)).Code);

            // A destination inside the checkout is refused before anything is written.
            var repositoryOutput = CorpusFixtures.FixturePath("golden", $"leak-{Guid.NewGuid():N}.json");
            var refused = await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, repositoryOutput);
            Assert.Equal(1, refused.Code);
            Assert.Contains("outside every repository", refused.Errors, StringComparison.Ordinal);
            Assert.False(File.Exists(repositoryOutput));

            var fromCheckout = Path.Join(root.FullName, "from-checkout.json");
            var checkoutManifest = await Run(
                "emit-run-plan",
                CorpusFixtures.FixturePath("golden", "synthetic-manifest.v1.json"),
                CorpusGoldenTests.RunId,
                CorpusGoldenTests.ProducerId,
                CorpusGoldenTests.ProducerVersion,
                fromCheckout);
            Assert.Equal(1, checkoutManifest.Code);
            Assert.False(File.Exists(fromCheckout));

            var linkedCheckout = Path.Join(root.FullName, "linked-golden");
            if (CorpusFixtures.TryCreateDirectoryLink(linkedCheckout, CorpusFixtures.FixturePath("golden")))
            {
                var linked = await Run("validate-manifest", Path.Join(linkedCheckout, "synthetic-manifest.v1.json"));
                Assert.Equal(1, linked.Code);
                Assert.Contains("outside every repository", linked.Errors, StringComparison.Ordinal);
            }

            Assert.Equal(1, (await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, manifest)).Code);
            Assert.Equal(CorpusFixtures.Golden("synthetic-manifest.v1.json"), await File.ReadAllTextAsync(manifest));

            // Invalid ids, and a version the interchange privacy rules reject, never reach disk.
            var shortId = Path.Join(root.FullName, "short-id.json");
            Assert.Equal(1, (await Run("emit-run-plan", manifest, "short", CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, shortId)).Code);
            Assert.False(File.Exists(shortId));
            var traversal = Path.Join(root.FullName, "traversal.json");
            var traversalRun = await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, "1.0/../../capture", traversal);
            Assert.Equal(1, traversalRun.Code);
            Assert.Contains("relative traversal", traversalRun.Errors, StringComparison.Ordinal);
            Assert.False(File.Exists(traversal));

            Assert.Equal(2, (await Run("emit-run-plan", manifest)).Code);
            Assert.DoesNotContain(Directory.EnumerateFiles(root.FullName), file => file.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public async Task ScoreValidateAndPublishReproduceTheGoldenAggregateAndRefuseUnboundPredictions()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var manifest = await CopyGolden(root, "synthetic-manifest.v1.json", "manifest.json");
            var predictions = await CopyGolden(root, "synthetic-predictions.v1.json", "predictions.json");
            var plan = Path.Join(root.FullName, "run-plan.json");
            Assert.Equal(0, (await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, plan)).Code);
            Assert.Equal(0, (await Run("validate-predictions", predictions, plan)).Code);

            // The frozen policy is read from the checked-in file, exactly as an operator would pass it.
            var thresholds = CorpusFixtures.ThresholdsPath();
            var aggregate = Path.Join(root.FullName, "aggregate.json");
            var scored = await Run("score-and-publish", manifest, plan, predictions, thresholds, "synthetic-raster", aggregate);
            Assert.Empty(scored.Errors);
            Assert.Equal(0, scored.Code);
            Assert.Equal(CorpusFixtures.Golden("synthetic-aggregate-results.v1.json"), await File.ReadAllTextAsync(aggregate));
            Assert.Equal(0, (await Run("validate-aggregate", aggregate, thresholds)).Code);

            var published = Path.Join(root.FullName, "published.json");
            Assert.Equal(0, (await Run("publish-aggregate", aggregate, thresholds, published)).Code);
            Assert.Equal(await File.ReadAllBytesAsync(aggregate), await File.ReadAllBytesAsync(published));

            // Output produced for a superseded plan carries a different lock and cannot be scored.
            var stale = JsonNode.Parse(await File.ReadAllTextAsync(predictions))!.AsObject();
            stale["planLock"] = new string('b', 64);
            var stalePredictions = Path.Join(root.FullName, "stale-predictions.json");
            await File.WriteAllTextAsync(stalePredictions, stale.ToJsonString());
            var staleValidation = await Run("validate-predictions", stalePredictions, plan);
            Assert.Equal(1, staleValidation.Code);
            Assert.Contains("exact run-plan lock", staleValidation.Errors, StringComparison.Ordinal);
            var staleAggregate = Path.Join(root.FullName, "stale-aggregate.json");
            Assert.Equal(1, (await Run("score-and-publish", manifest, plan, stalePredictions, thresholds, "synthetic-raster", staleAggregate)).Code);
            Assert.False(File.Exists(staleAggregate));

            stale.Remove("planLock");
            await File.WriteAllTextAsync(stalePredictions, stale.ToJsonString());
            Assert.Equal(1, (await Run("validate-predictions", stalePredictions, plan)).Code);

            var wrongClass = Path.Join(root.FullName, "wrong-class.json");
            Assert.Equal(1, (await Run("score-and-publish", manifest, plan, predictions, thresholds, "real-raster", wrongClass)).Code);
            Assert.False(File.Exists(wrongClass));
            Assert.Equal(1, (await Run("score-and-publish", manifest, plan, predictions, thresholds, "pooled", wrongClass)).Code);
            Assert.Equal(1, (await Run("score-and-publish", manifest, plan, predictions, thresholds, "synthetic-raster", predictions)).Code);
            Assert.Equal(CorpusFixtures.Golden("synthetic-predictions.v1.json"), await File.ReadAllTextAsync(predictions));

            // A tuned policy copy is not the frozen policy, wherever it is placed.
            var tuned = JsonNode.Parse(CorpusFixtures.ThresholdsJson())!.AsObject();
            tuned["candidateThresholds"]!["minimumF1"] = 0.5m;
            var tunedThresholds = Path.Join(root.FullName, "tuned-thresholds.json");
            await File.WriteAllTextAsync(tunedThresholds, tuned.ToJsonString());
            Assert.Equal(1, (await Run("validate-aggregate", aggregate, tunedThresholds)).Code);
            Assert.Equal(1, (await Run("score-and-publish", manifest, plan, predictions, tunedThresholds, "synthetic-raster", Path.Join(root.FullName, "tuned.json"))).Code);
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    /// <summary>
    /// A runtime I/O exception names the file it failed on, and the CLI used to print it: a
    /// manifest locked by another process put its private location on stderr. The same held for
    /// a document that placed a private path in an id, an enum value, or a property name.
    /// </summary>
    [Fact]
    public async Task FilesystemAndDocumentFailuresNeverEchoPrivatePaths()
    {
        var root = CorpusFixtures.PrivateRoot();
        try
        {
            var owner = Directory.CreateDirectory(Path.Join(root.FullName, "private-owner-capture-folder"));
            var manifest = Path.Join(owner.FullName, "owner-private-manifest.json");
            await File.WriteAllTextAsync(manifest, CorpusFixtures.Golden("synthetic-manifest.v1.json"));
            string[] secrets = [root.FullName, owner.Name, "owner-private-manifest"];

            var plan = Path.Join(owner.FullName, "owner-plan.json");
            using (new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // The lock is taken inside the runtime's own open, so the exception and its message
                // come from the runtime, not from this tool.
                var locked = await Run("validate-manifest", manifest);
                Assert.Equal(1, locked.Code);
                Assert.Equal(CorpusDiagnostics.InputOutputMessage, locked.Errors.TrimEnd());
                AssertOmits(locked, secrets);

                var lockedEmit = await Run("emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, plan);
                Assert.Equal(1, lockedEmit.Code);
                Assert.Equal(CorpusDiagnostics.InputOutputMessage, lockedEmit.Errors.TrimEnd());
                AssertOmits(lockedEmit, secrets);
                Assert.False(File.Exists(plan));
            }

            // Refusals this tool writes are fixed text as well.
            foreach (var args in new[]
                     {
                         new[] { "validate-manifest", Path.Join(owner.FullName, "owner-absent-manifest.json") },
                         new[] { "validate-manifest", owner.FullName },
                         new[] { "emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, Path.Join(owner.FullName, "absent", "owner-plan.json") },
                         new[] { "emit-run-plan", manifest, CorpusGoldenTests.RunId, CorpusGoldenTests.ProducerId, CorpusGoldenTests.ProducerVersion, owner.FullName },
                         new[] { "validate-predictions", Path.Join(owner.FullName, "owner-predictions.json"), plan },
                     })
            {
                var refused = await Run(args);
                Assert.Equal(1, refused.Code);
                Assert.NotEmpty(refused.Errors);
                AssertOmits(refused, secrets);
            }

            // Document content that looks like a private location is never quoted back.
            var hostile = JsonNode.Parse(CorpusFixtures.Golden("synthetic-manifest.v1.json"))!.AsObject();
            hostile["samples"]![0]!["sampleId"] = "/home/private-owner-capture-folder/owner-private-manifest.png";
            hostile["samples"]![1]!["evidenceClass"] = @"C:\private-owner-capture-folder\owner-private-manifest.png";
            hostile["samples"]![2]!["truth"]!["claims"]![0]!["truthId"] = "private-owner-capture-folder owner-private-manifest";
            hostile["samples"]![3]!["lineage"]!["sequenceId"] = "private-owner-capture-folder";
            hostile["futureExtension"] = new JsonObject { ["/home/private-owner-capture-folder/owner-private-manifest.png"] = true };
            var hostileManifest = Path.Join(root.FullName, "hostile.json");
            await File.WriteAllTextAsync(hostileManifest, hostile.ToJsonString());
            var content = await Run("validate-manifest", hostileManifest);
            Assert.Equal(1, content.Code);
            Assert.Contains("filesystem path", content.Errors, StringComparison.Ordinal);
            Assert.Contains("<non-opaque id>", content.Errors, StringComparison.Ordinal);
            Assert.Contains("<unrecognized value>", content.Errors, StringComparison.Ordinal);
            AssertOmits(content, secrets);

            foreach (var malformed in new[]
                     {
                         """{"schemaVersion": "manifest.v1", "/home/private-owner-capture-folder/owner-private-manifest.png": """,
                         """{"schemaVersion": "manifest.v1", "C:\\private-owner-capture-folder": 1, "C:\\private-owner-capture-folder": 2}""",
                     })
            {
                await File.WriteAllTextAsync(hostileManifest, malformed);
                var result = await Run("validate-manifest", hostileManifest);
                Assert.Equal(1, result.Code);
                Assert.NotEmpty(result.Errors);
                AssertOmits(result, secrets);
            }
        }
        finally
        {
            CorpusFixtures.DeletePrivateRoot(root);
        }
    }

    [Fact]
    public void RuntimeExceptionMessagesAreReplacedByFixedText()
    {
        const string secret = "/home/private-owner/corpus/manifest.json";
        foreach (var (exception, expected) in new (Exception, string)[]
                 {
                     (new FileNotFoundException($"Could not find file '{secret}'.", secret), CorpusDiagnostics.NotFoundMessage),
                     (new DirectoryNotFoundException($"Could not find a part of the path '{secret}'."), CorpusDiagnostics.NotFoundMessage),
                     (new UnauthorizedAccessException($"Access to the path '{secret}' is denied."), CorpusDiagnostics.AccessDeniedMessage),
                     (new PathTooLongException($"The path '{secret}' is too long."), CorpusDiagnostics.PathTooLongMessage),
                     (new IOException($"The process cannot access the file '{secret}'."), CorpusDiagnostics.InputOutputMessage),
                     (new ArgumentException($"Illegal characters in path '{secret}'.", "path"), CorpusDiagnostics.InvalidArgumentMessage),
                     (new NotSupportedException($"The given path's format is not supported: '{secret}'."), CorpusDiagnostics.InvalidArgumentMessage),
                     (new InvalidOperationException($"Unexpected state at '{secret}'."), CorpusDiagnostics.UnexpectedMessage),
                     (new InvalidDataException(secret), CorpusDiagnostics.UnexpectedMessage),
                 })
        {
            Assert.Equal(expected, CorpusDiagnostics.DescribeFailure(exception));
        }
    }

    private static void AssertOmits((int Code, string Errors) result, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, result.Errors, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> CopyGolden(DirectoryInfo root, string fileName, string destinationName)
    {
        var destination = Path.Join(root.FullName, destinationName);
        await File.WriteAllTextAsync(destination, CorpusFixtures.Golden(fileName));
        return destination;
    }

    private static async Task<(int Code, string Errors)> Run(params string[] args)
    {
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await CorpusCli.RunAsync(args, error, Clock);
        return (code, error.ToString());
    }
}
