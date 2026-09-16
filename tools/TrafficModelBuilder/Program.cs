using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        var options = ParseArguments(arguments);
        var requestBytes = await ReadBoundedAsync(options["--request"], 16 * 1024 * 1024).ConfigureAwait(false);
        var artifact = await ReadBoundedAsync(options["--artifact"], 64 * 1024 * 1024).ConfigureAwait(false);
        var privateKeyPem = await ReadBoundedAsync(options["--private-key"], 64 * 1024).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<TrafficModelBuildRequest>(requestBytes, TrafficDataJson.Options)
                      ?? throw new InvalidDataException("The build request was null.");
        var signedUtc = DateTimeOffset.ParseExact(
            options["--signed-utc"],
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var build = TrafficModelBuilder.Build(request, artifact);

        using var key = ECDsa.Create();
        key.ImportFromPem(System.Text.Encoding.UTF8.GetString(privateKeyPem));
        if (key.KeySize != 256)
        {
            throw new InvalidDataException("Traffic manifests require an ECDSA P-256 private key.");
        }

        var manifestHash = SHA256.HashData(build.ManifestJson);
        var signature = new TrafficArtifactSignature(
            TrafficSignatureAlgorithm.EcdsaP256Sha256,
            options["--key-id"],
            Convert.ToHexStringLower(manifestHash),
            Convert.ToBase64String(key.SignHash(manifestHash, DSASignatureFormat.Rfc3279DerSequence)),
            signedUtc);
        if (signature.SignedUtc < build.Manifest.GeneratedUtc)
        {
            throw new InvalidDataException("The explicit signing time cannot predate model generation.");
        }

        var output = Path.GetFullPath(options["--output"]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException("The output directory must be absent or empty.");
        }

        Directory.CreateDirectory(output);
        await WriteNewAsync(Path.Combine(output, "dataset.json"), build.DatasetJson).ConfigureAwait(false);
        await WriteNewAsync(Path.Combine(output, "build-report.json"), build.ReportJson).ConfigureAwait(false);
        await WriteNewAsync(Path.Combine(output, "manifest.json"), build.ManifestJson).ConfigureAwait(false);
        await WriteNewAsync(
            Path.Combine(output, "manifest.signature.json"),
            JsonSerializer.SerializeToUtf8Bytes(signature, TrafficDataJson.Options)).ConfigureAwait(false);
        await WriteNewAsync(Path.Combine(output, "model.artifact"), build.Artifact).ConfigureAwait(false);
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException or
                                      CryptographicException or FormatException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var required = new HashSet<string>(StringComparer.Ordinal)
    {
        "--request",
        "--artifact",
        "--private-key",
        "--key-id",
        "--signed-utc",
        "--output",
    };
    if (arguments.Length != required.Count * 2)
    {
        throw new ArgumentException(
            "Usage: --request FILE --artifact FILE --private-key PEM --key-id ID --signed-utc ISO_UTC --output DIRECTORY");
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (!required.Contains(arguments[index]) || !result.TryAdd(arguments[index], arguments[index + 1]))
        {
            throw new ArgumentException("Traffic model builder arguments contain an unknown or repeated option.");
        }
    }

    if (result.Count != required.Count || result.Values.Any(string.IsNullOrWhiteSpace))
    {
        throw new ArgumentException("Every traffic model builder option is required.");
    }

    return result;
}

static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes)
{
    var fullPath = Path.GetFullPath(path);
    var length = new FileInfo(fullPath).Length;
    if (length is <= 0 || length > maximumBytes)
    {
        throw new InvalidDataException($"Input is outside its {maximumBytes}-byte bound.");
    }

    return await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
}

static async Task WriteNewAsync(string path, byte[] bytes)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 16 * 1024,
        FileOptions.Asynchronous | FileOptions.WriteThrough);
    await stream.WriteAsync(bytes).ConfigureAwait(false);
    await stream.FlushAsync().ConfigureAwait(false);
}
