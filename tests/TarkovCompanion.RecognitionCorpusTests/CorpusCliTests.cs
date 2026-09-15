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
