using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Updates;

namespace TarkovCompanion.Infrastructure.Updates;

internal sealed class CosignCleanupCanceledException(
    OperationCanceledException cancellation)
    : OperationCanceledException(
        "Cosign cancellation could not prove that the verifier process exited and drained; release staging was quarantined.",
        cancellation,
        cancellation.CancellationToken),
      IReleaseStagingQuarantineRequired
{
}

internal sealed class CosignCleanupException(Exception failure)
    : InvalidOperationException(
        "Cosign cleanup could not prove that the verifier process exited and drained; release staging was quarantined.",
        failure),
      IReleaseStagingQuarantineRequired
{
}

/// <summary>Verifies only the project's standardized v0.3 bundles with a content-pinned cosign.</summary>
public sealed partial class CosignReleaseSignatureVerifier : IReleaseSignatureVerifier
{
    private const string BundleMediaType = "application/vnd.dev.sigstore.bundle.v0.3+json";
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> PinnedCosignDigests = new(StringComparer.Ordinal)
    {
        // cosign v3.1.3, from its Sigstore-verified checksums.
        "9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be",
        "4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71",
        "c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a",
    };

    private readonly SignedReleaseFeedOptions _options;
    private readonly ICosignProcessFactory _processFactory;
    private readonly Func<string, CancellationToken, Task<string>> _cosignDigest;
    private readonly TimeSpan _processCleanupTimeout;

    public CosignReleaseSignatureVerifier(SignedReleaseFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var cosignSha256 = options.CosignSha256;
        if (cosignSha256 is null || !LowerHex64().IsMatch(cosignSha256) ||
            !PinnedCosignDigests.Contains(cosignSha256))
        {
            throw new ArgumentException("Cosign must be one of the reviewed v3.1.3 builds.", nameof(options));
        }

        foreach (var value in new[]
                 {
                     options.CosignPath,
                     options.TrustRootPath,
                     options.SignerIdentity,
                     options.SignerIssuer,
                     options.SignerRepository,
                     options.SignerRef,
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Equals(value.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Release signature configuration is incomplete.", nameof(options));
            }
        }

        _options = options;
        _processFactory = SystemCosignProcessFactory.Instance;
        _cosignDigest = static (path, cancellationToken) => Sha256Async(
            path,
            ReleaseFeedLimits.MaximumArtifactBytes,
            cancellationToken);
        _processCleanupTimeout = ProcessCleanupTimeout;
    }

    internal CosignReleaseSignatureVerifier(
        SignedReleaseFeedOptions options,
        ICosignProcessFactory processFactory,
        Func<string, CancellationToken, Task<string>> cosignDigest,
        TimeSpan processCleanupTimeout)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(cosignDigest);
        if (processCleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processCleanupTimeout),
                "Process cleanup must have a positive bounded duration.");
        }

        _processFactory = processFactory;
        _cosignDigest = cosignDigest;
        _processCleanupTimeout = processCleanupTimeout;
    }

    public async Task VerifyAsync(string filePath, string bundlePath, CancellationToken cancellationToken)
    {
        var signedFile = RequirePlainFile(filePath, ReleaseFeedLimits.MaximumArtifactBytes, "signed release file");
        var bundle = RequirePlainFile(bundlePath, ReleaseFeedLimits.MaximumSignatureBytes, "Sigstore bundle");
        var cosign = RequirePlainFile(_options.CosignPath, ReleaseFeedLimits.MaximumArtifactBytes, "cosign verifier");
        var trustRoot = RequirePlainFile(_options.TrustRootPath, ReleaseFeedLimits.MaximumJsonBytes, "Sigstore trust root");

        var cosignDigest = await _cosignDigest(cosign.FullName, cancellationToken).ConfigureAwait(false);
        if (!cosignDigest.Equals(_options.CosignSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The configured cosign executable does not match its reviewed digest.");
        }

        using var trustRootDocument = await ParseJsonAsync(
            trustRoot.FullName,
            ReleaseFeedLimits.MaximumJsonBytes,
            cancellationToken).ConfigureAwait(false);
        using var bundleDocument = await ParseJsonAsync(
            bundle.FullName,
            ReleaseFeedLimits.MaximumSignatureBytes,
            cancellationToken).ConfigureAwait(false);
        await ValidateBundleAsync(bundleDocument.RootElement, signedFile.FullName, cancellationToken).ConfigureAwait(false);

        using var process = _processFactory.Create(
            CreateStartInfo(cosign.FullName, signedFile.FullName, bundle.FullName, trustRoot.FullName));
        var started = false;
        Task<(string Text, bool Truncated)>? outputTask = null;
        Task<(string Text, bool Truncated)>? errorTask = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The pinned cosign verifier did not start.");
            }

            started = true;
            process.CloseStandardInput();

            // Caller cancellation stops verification, not pipe drainage. Once cosign starts, its
            // redirected pipes must keep being consumed until the process tree is gone so a full
            // buffer cannot strand either the verifier or a child process during cleanup.
            outputTask = ReadBoundedOutputAsync(process.StandardOutput, CancellationToken.None);
            errorTask = ReadBoundedOutputAsync(process.StandardError, CancellationToken.None);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var outputs = await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var output = outputs[0];
            var error = outputs[1];
            if (output.Truncated || error.Truncated)
            {
                throw new InvalidDataException("Cosign exceeded its diagnostic-output limit.");
            }

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error.Text) ? output.Text : error.Text;
                throw new InvalidDataException($"Release signature verification failed: {Sanitize(detail)}");
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            _ = await TerminateAndDrainAsync(process, started, outputTask, errorTask).ConfigureAwait(false);
            // Cancellation means verification never reached its normal completed boundary. Even
            // if the root races to a natural exit before cleanup observes it, detached descendants
            // are not waitable through Process, so retain the release staging directory.
            throw new CosignCleanupCanceledException(exception);
        }
        catch (Exception exception)
        {
            if (!await TerminateAndDrainAsync(process, started, outputTask, errorTask).ConfigureAwait(false))
            {
                throw new CosignCleanupException(exception);
            }

            throw;
        }
    }

    private async Task<bool> TerminateAndDrainAsync(
        ICosignProcess process,
        bool started,
        Task<(string Text, bool Truncated)>? outputTask,
        Task<(string Text, bool Truncated)>? errorTask)
    {
        if (!started)
        {
            return true;
        }

        // Cleanup deliberately has its own deadline. Reusing the caller's cancelled token would
        // abandon cosign and its redirected streams at exactly the point cleanup is required.
        using var cleanup = new CancellationTokenSource(_processCleanupTimeout);
        var forcedTreeTermination = false;
        try
        {
            if (!process.HasExited)
            {
                forcedTreeTermination = true;
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // A racing natural exit or an OS-level kill failure must not replace the original
            // verification exception. The independent wait below still gets a chance to settle.
        }

        var exited = false;
        try
        {
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            exited = process.HasExited;
        }
        catch
        {
            // The final HasExited probe below distinguishes a timeout from a racing natural exit.
        }

        if (!exited)
        {
            try
            {
                exited = process.HasExited;
            }
            catch
            {
                // A process whose state cannot be observed is not proven quiescent.
            }
        }

        var outputTasks = new List<Task>(2);
        if (outputTask is not null)
        {
            outputTasks.Add(outputTask);
        }

        if (errorTask is not null)
        {
            outputTasks.Add(errorTask);
        }

        if (outputTasks.Count == 0)
        {
            return exited;
        }

        var drained = false;
        try
        {
            await Task.WhenAll(outputTasks).WaitAsync(cleanup.Token).ConfigureAwait(false);
            drained = true;
        }
        catch
        {
            // A completed fault is quiescent even though its diagnostic read failed. Pending
            // reads mean a process or pipe can still be live, so staging must be retained.
            drained = outputTasks.All(static task => task.IsCompleted);
        }
        finally
        {
            foreach (var task in outputTasks)
            {
                ObserveEventually(task);
            }
        }

        // Process.Kill(true) requests termination recursively, but the Process handle and
        // WaitForExitAsync represent only the root. Pipe EOF does not prove that a detached child
        // which closed its inherited handles is gone. Preserve staging after every forced kill;
        // the cancellation caller also quarantines an exit that raced its cancelled wait.
        return exited && drained && !forcedTreeTermination;
    }

    private static void ObserveEventually(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private ProcessStartInfo CreateStartInfo(
        string cosignPath,
        string filePath,
        string bundlePath,
        string trustRootPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = cosignPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // Beside the file being checked, which is a download folder and never the install folder (#599).
            WorkingDirectory = Path.GetDirectoryName(filePath)!,
        };
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "TARKOV_RELEASE_FEED_TOKEN" })
        {
            start.Environment.Remove(name);
        }

        foreach (var argument in new[]
                 {
                     "verify-blob",
                     "--bundle", bundlePath,
                     "--trusted-root", trustRootPath,
                     "--certificate-identity", _options.SignerIdentity,
                     "--certificate-oidc-issuer", _options.SignerIssuer,
                     "--certificate-github-workflow-repository", _options.SignerRepository,
                     "--certificate-github-workflow-ref", _options.SignerRef,
                     filePath,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static async Task ValidateBundleAsync(
        JsonElement bundle,
        string signedFile,
        CancellationToken cancellationToken)
    {
        if (bundle.ValueKind != JsonValueKind.Object ||
            !HasExactly(bundle, "mediaType", "verificationMaterial", "messageSignature") ||
            !bundle.TryGetProperty("mediaType", out var mediaType) || mediaType.GetString() != BundleMediaType ||
            !bundle.TryGetProperty("verificationMaterial", out var material) ||
            material.ValueKind != JsonValueKind.Object ||
            !HasOnly(material, "certificate", "tlogEntries", "timestampVerificationData") ||
            !material.TryGetProperty("certificate", out var certificate) ||
            certificate.ValueKind != JsonValueKind.Object ||
            !certificate.TryGetProperty("rawBytes", out var rawCertificate) ||
            rawCertificate.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(rawCertificate.GetString()) ||
            !material.TryGetProperty("tlogEntries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1 ||
            !bundle.TryGetProperty("messageSignature", out var signature) ||
            signature.ValueKind != JsonValueKind.Object ||
            !HasExactly(signature, "messageDigest", "signature") ||
            !signature.TryGetProperty("signature", out var signatureBytes) ||
            signatureBytes.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(signatureBytes.GetString()) ||
            !signature.TryGetProperty("messageDigest", out var digest) ||
            digest.ValueKind != JsonValueKind.Object ||
            !HasExactly(digest, "algorithm", "digest") ||
            !digest.TryGetProperty("algorithm", out var algorithm) || algorithm.GetString() != "SHA2_256" ||
            !digest.TryGetProperty("digest", out var encoded) || encoded.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The signature is not a standardized v0.3 Sigstore message bundle.");
        }

        byte[] claimed;
        try
        {
            claimed = Convert.FromBase64String(encoded.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Sigstore bundle carries an invalid digest.", exception);
        }

        var actualText = await Sha256Async(
            signedFile,
            ReleaseFeedLimits.MaximumArtifactBytes,
            cancellationToken).ConfigureAwait(false);
        var actual = Convert.FromHexString(actualText);
        try
        {
            if (claimed.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(claimed, actual))
            {
                throw new InvalidDataException("The Sigstore bundle signs different bytes from the release file.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claimed);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static async Task<JsonDocument> ParseJsonAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"{path} is outside its JSON byte limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"The {label} is missing, redirected, empty, or outside its byte limit.");
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
                    throw new InvalidDataException("A verification input grew beyond its byte limit.");
                }

                hash.AppendData(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedOutputAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (builder.ToString(), truncated);
            }

            var remaining = ReleaseFeedLimits.MaximumCommandOutputCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }

            truncated |= read > remaining;
        }
    }

    private static bool HasExactly(JsonElement value, params string[] expected)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool HasOnly(JsonElement value, params string[] allowed)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == names.Distinct(StringComparer.Ordinal).Count() &&
               names.All(name => allowed.Contains(name, StringComparer.Ordinal));
    }

    private static string Sanitize(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 512 ? oneLine : oneLine[..512] + "…";
    }

    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();
}

internal interface ICosignProcessFactory
{
    ICosignProcess Create(ProcessStartInfo startInfo);
}

internal interface ICosignProcess : IDisposable
{
    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    bool Start();

    void CloseStandardInput();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

internal sealed class SystemCosignProcessFactory : ICosignProcessFactory
{
    public static SystemCosignProcessFactory Instance { get; } = new();

    private SystemCosignProcessFactory()
    {
    }

    public ICosignProcess Create(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return new SystemCosignProcess(startInfo);
    }

    private sealed class SystemCosignProcess : ICosignProcess
    {
        private readonly Process _process;

        public SystemCosignProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo };
        }

        public TextReader StandardOutput => _process.StandardOutput;

        public TextReader StandardError => _process.StandardError;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public bool Start() => _process.Start();

        public void CloseStandardInput() => _process.StandardInput.Close();

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}
