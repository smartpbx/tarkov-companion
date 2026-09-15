using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Application.Services.Updates;
using TarkovCompanion.Infrastructure.Updates;

namespace TarkovCompanion.UnitTests;

public sealed class CosignReleaseSignatureVerifierTests : IDisposable
{
    private const string ReviewedCosignDigest =
        "9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-cosign-verifier-{Guid.NewGuid():N}");

    [Fact]
    public async Task AValidBundleAcceptedByThePinnedVerifierSucceeds()
    {
        var fixture = CreateFixture();
        var process = FakeCosignProcess.Completed(exitCode: 0, output: "Verified OK\n");
        var factory = new FakeCosignProcessFactory(process);
        var verifier = CreateVerifier(fixture, factory);

        await verifier.VerifyAsync(fixture.PayloadPath, fixture.BundlePath, default);

        Assert.True(process.Started);
        Assert.True(process.StandardInputClosed);
        Assert.False(process.Killed);
        Assert.True(process.StandardOutputReader.EndObserved);
        Assert.True(process.StandardErrorReader.EndObserved);
        Assert.True(process.Disposed);
        Assert.Single(process.WaitTokens);

        var startInfo = Assert.IsType<ProcessStartInfo>(factory.StartInfo);
        Assert.Equal(Path.GetFullPath(fixture.CosignPath), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(_root), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal("verify-blob", startInfo.ArgumentList[0]);
        Assert.Equal(Path.GetFullPath(fixture.PayloadPath), startInfo.ArgumentList[^1]);
    }

    [Fact]
    public async Task AVerifierRejectionReturnsItsBoundedSanitizedDiagnostic()
    {
        var fixture = CreateFixture();
        var process = FakeCosignProcess.Completed(
            exitCode: 23,
            output: "unused output",
            error: "certificate rejected\r\nidentity differs");
        var verifier = CreateVerifier(fixture, new FakeCosignProcessFactory(process));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => verifier.VerifyAsync(fixture.PayloadPath, fixture.BundlePath, default));

        Assert.Contains("certificate rejected  identity differs", exception.Message, StringComparison.Ordinal);
        Assert.True(process.StandardOutputReader.EndObserved);
        Assert.True(process.StandardErrorReader.EndObserved);
        Assert.False(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AChangedVerifierBinaryIsRejectedBeforeAProcessIsCreated()
    {
        var fixture = CreateFixture();
        var process = FakeCosignProcess.Completed(exitCode: 0);
        var factory = new FakeCosignProcessFactory(process);
        var verifier = CreateVerifier(
            fixture,
            factory,
            static (_, _) => Task.FromResult(new string('0', 64)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => verifier.VerifyAsync(fixture.PayloadPath, fixture.BundlePath, default));

        Assert.Contains("does not match its reviewed digest", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.CreateCount);
        Assert.False(process.Started);
    }

    [Fact]
    public async Task CancellationKillsTheProcessTreeThenWaitsAndDrainsWithAnIndependentToken()
    {
        var fixture = CreateFixture();
        var process = FakeCosignProcess.BlockedUntilKilled();
        var verifier = CreateVerifier(fixture, new FakeCosignProcessFactory(process));
        using var cancellation = new CancellationTokenSource();

        var verification = verifier.VerifyAsync(
            fixture.PayloadPath,
            fixture.BundlePath,
            cancellation.Token);
        await process.FirstWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verification.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(process.Killed);
        Assert.True(process.KilledEntireTree);
        Assert.True(process.StandardInputClosed);
        Assert.True(process.StandardOutputReader.EndObserved);
        Assert.True(process.StandardErrorReader.EndObserved);
        Assert.True(process.Disposed);

        Assert.Collection(
            process.WaitTokens,
            token => Assert.Equal(cancellation.Token, token),
            token =>
            {
                Assert.NotEqual(cancellation.Token, token);
                Assert.True(token.CanBeCanceled);
                Assert.False(token.IsCancellationRequested);
            });
    }

    [Fact]
    public async Task ARealV03BundleIsAcceptedAndAlteredBytesAreRefusedByTheProductionProcessBoundary()
    {
        // The release gate supplies pinned public Sigstore material and the reviewed cosign
        // binary. Ordinary unit-test runs retain the deterministic process fixtures above.
        if (Environment.GetEnvironmentVariable("TARKOV_REAL_SIGSTORE_CSHARP") != "1")
        {
            return;
        }

        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"The release gate did not supply {name}.");

        var subject = Required("TARKOV_REAL_SIGSTORE_SUBJECT");
        var bundle = Required("TARKOV_REAL_SIGSTORE_BUNDLE");
        var verifier = new CosignReleaseSignatureVerifier(new SignedReleaseFeedOptions
        {
            Repository = "smartpbx/tarkov-companion-private-feed",
            StagingRoot = _root,
            CosignPath = Required("TARKOV_REAL_SIGSTORE_COSIGN"),
            CosignSha256 = Required("TARKOV_REAL_SIGSTORE_COSIGN_SHA256"),
            TrustRootPath = Required("TARKOV_REAL_SIGSTORE_TRUST_ROOT"),
            SignerIdentity = Required("TARKOV_REAL_SIGSTORE_IDENTITY"),
            SignerIssuer = Required("TARKOV_REAL_SIGSTORE_ISSUER"),
            SignerRepository = Required("TARKOV_REAL_SIGSTORE_REPOSITORY"),
            SignerRef = Required("TARKOV_REAL_SIGSTORE_REF"),
        });

        await verifier.VerifyAsync(subject, bundle, default);

        Directory.CreateDirectory(_root);
        var altered = Path.Combine(_root, "altered-subject");
        File.Copy(subject, altered);
        await File.AppendAllTextAsync(altered, "altered");
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.VerifyAsync(altered, bundle, default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture CreateFixture()
    {
        Directory.CreateDirectory(_root);
        var payloadPath = Path.Combine(_root, "release.json");
        var bundlePath = Path.Combine(_root, "release.json.sigstore.json");
        var cosignPath = Path.Combine(_root, "cosign-test-double");
        var trustRootPath = Path.Combine(_root, "trusted-root.json");
        var payload = "signed release payload"u8.ToArray();

        File.WriteAllBytes(payloadPath, payload);
        File.WriteAllText(cosignPath, "deterministic fake cosign executable");
        File.WriteAllText(trustRootPath, "{}");
        File.WriteAllText(
            bundlePath,
            JsonSerializer.Serialize(new
            {
                mediaType = "application/vnd.dev.sigstore.bundle.v0.3+json",
                verificationMaterial = new
                {
                    certificate = new { rawBytes = "Y2VydGlmaWNhdGU=" },
                    tlogEntries = new[] { new { logIndex = 1 } },
                },
                messageSignature = new
                {
                    messageDigest = new
                    {
                        algorithm = "SHA2_256",
                        digest = Convert.ToBase64String(SHA256.HashData(payload)),
                    },
                    signature = "c2lnbmF0dXJl",
                },
            }));

        return new Fixture(
            payloadPath,
            bundlePath,
            cosignPath,
            new SignedReleaseFeedOptions
            {
                Repository = "smartpbx/tarkov-companion-private-feed",
                StagingRoot = _root,
                CosignPath = cosignPath,
                CosignSha256 = ReviewedCosignDigest,
                TrustRootPath = trustRootPath,
            });
    }

    private static CosignReleaseSignatureVerifier CreateVerifier(
        Fixture fixture,
        ICosignProcessFactory processFactory,
        Func<string, CancellationToken, Task<string>>? cosignDigest = null)
    {
        return new CosignReleaseSignatureVerifier(
            fixture.Options,
            processFactory,
            cosignDigest ?? (static (_, _) => Task.FromResult(ReviewedCosignDigest)),
            TimeSpan.FromSeconds(2));
    }

    private sealed record Fixture(
        string PayloadPath,
        string BundlePath,
        string CosignPath,
        SignedReleaseFeedOptions Options);

    private sealed class FakeCosignProcessFactory(FakeCosignProcess process) : ICosignProcessFactory
    {
        public int CreateCount { get; private set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public ICosignProcess Create(ProcessStartInfo startInfo)
        {
            CreateCount++;
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeCosignProcess : ICosignProcess
    {
        private readonly int _exitCode;
        private readonly bool _completeOnStart;
        private readonly string _output;
        private readonly string _error;
        private readonly TaskCompletionSource<bool> _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeCosignProcess(int exitCode, bool completeOnStart, string output, string error)
        {
            _exitCode = exitCode;
            _completeOnStart = completeOnStart;
            _output = output;
            _error = error;
        }

        public ControlledTextReader StandardOutputReader { get; } = new();

        public ControlledTextReader StandardErrorReader { get; } = new();

        public TextReader StandardOutput => StandardOutputReader;

        public TextReader StandardError => StandardErrorReader;

        public bool HasExited => _exited.Task.IsCompleted;

        public int ExitCode => _exitCode;

        public bool Started { get; private set; }

        public bool StandardInputClosed { get; private set; }

        public bool Killed { get; private set; }

        public bool KilledEntireTree { get; private set; }

        public bool Disposed { get; private set; }

        public ConcurrentQueue<CancellationToken> WaitTokens { get; } = new();

        public TaskCompletionSource<bool> FirstWaitEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeCosignProcess Completed(
            int exitCode,
            string output = "",
            string error = "") => new(exitCode, completeOnStart: true, output, error);

        public static FakeCosignProcess BlockedUntilKilled() =>
            new(exitCode: 137, completeOnStart: false, output: "", error: "");

        public bool Start()
        {
            Started = true;
            if (_completeOnStart)
            {
                Complete();
            }

            return true;
        }

        public void CloseStandardInput()
        {
            StandardInputClosed = true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Enqueue(cancellationToken);
            FirstWaitEntered.TrySetResult(true);
            return _exited.Task.WaitAsync(cancellationToken);
        }

        public void Kill(bool entireProcessTree)
        {
            Killed = true;
            KilledEntireTree = entireProcessTree;
            Complete();
        }

        public void Dispose()
        {
            Disposed = true;
            Complete();
        }

        private void Complete()
        {
            StandardOutputReader.Complete(_output);
            StandardErrorReader.Complete(_error);
            _exited.TrySetResult(true);
        }
    }

    private sealed class ControlledTextReader : TextReader
    {
        private readonly TaskCompletionSource<string> _content = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;

        public bool EndObserved { get; private set; }

        public void Complete(string content)
        {
            _content.TrySetResult(content);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            var content = await _content.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_offset >= content.Length)
            {
                EndObserved = true;
                return 0;
            }

            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }
}
