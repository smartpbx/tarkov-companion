using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    byte[]? privateKeyPem = null;
    try
    {
        var options = ParseArguments(arguments);
        var requestBytes = await ReadBoundedAsync(options["--request"], 16 * 1024 * 1024).ConfigureAwait(false);
        var artifact = await ReadBoundedAsync(options["--artifact"], 64 * 1024 * 1024).ConfigureAwait(false);
        privateKeyPem = await ReadBoundedAsync(options["--private-key"], 64 * 1024).ConfigureAwait(false);
        RejectDuplicateProperties(requestBytes, "build request");
        var request = JsonSerializer.Deserialize<TrafficModelBuildRequest>(requestBytes, TrafficDataJson.Options)
                      ?? throw new InvalidDataException("The build request was null.");
        var signedUtc = DateTimeOffset.ParseExact(
            options["--signed-utc"],
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var build = TrafficModelBuilder.Build(request, artifact);

        var manifestHash = SHA256.HashData(build.ManifestJson);
        var signature = new TrafficArtifactSignature(
            TrafficSignatureAlgorithm.EcdsaP256Sha256,
            options["--key-id"],
            Convert.ToHexStringLower(manifestHash),
            Convert.ToBase64String(SignManifestHash(manifestHash, privateKeyPem!)),
            signedUtc);

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
    finally
    {
        if (privateKeyPem is not null)
        {
            CryptographicOperations.ZeroMemory(privateKeyPem);
        }
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
    await using var stream = new FileStream(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 16 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    try
    {
        using var destination = new MemoryStream(capacity: Math.Min(maximumBytes, 64 * 1024));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maximumBytes)
                {
                    throw new InvalidDataException($"Input exceeds its {maximumBytes}-byte bound.");
                }

                destination.Write(buffer, 0, read);
            }

            if (destination.Length == 0)
            {
                throw new InvalidDataException("Input cannot be empty.");
            }

            return destination.ToArray();
        }
        finally
        {
            if (destination.TryGetBuffer(out var contents))
            {
                CryptographicOperations.ZeroMemory(contents.AsSpan());
            }
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

static byte[] SignManifestHash(ReadOnlySpan<byte> manifestHash, ReadOnlySpan<byte> privateKeyPem)
{
    var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(privateKeyPem.Length));
    try
    {
        var count = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetChars(privateKeyPem, characters);
        using var key = ECDsa.Create();
        key.ImportFromPem(characters.AsSpan(0, count));
        if (key.KeySize != 256)
        {
            throw new InvalidDataException("Traffic manifests require an ECDSA P-256 private key.");
        }

        return key.SignHash(manifestHash, DSASignatureFormat.Rfc3279DerSequence);
    }
    finally
    {
        Array.Clear(characters);
        ArrayPool<char>.Shared.Return(characters);
    }
}

static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes, string description)
{
    var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = TrafficDataJson.MaxDepth,
    });
    var containers = new Stack<HashSet<string>?>();
    while (reader.Read())
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                containers.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                break;
            case JsonTokenType.StartArray:
                containers.Push(null);
                break;
            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
                if (containers.Count == 0)
                {
                    throw new InvalidDataException($"The {description} has an invalid container boundary.");
                }

                containers.Pop();
                break;
            case JsonTokenType.PropertyName:
                var name = reader.GetString()!;
                if (containers.Count == 0 || containers.Peek() is not { } properties || !properties.Add(name))
                {
                    throw new InvalidDataException($"The {description} contains a duplicate property.");
                }

                break;
        }
    }

    if (containers.Count != 0)
    {
        throw new InvalidDataException($"The {description} has an unterminated container.");
    }
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
