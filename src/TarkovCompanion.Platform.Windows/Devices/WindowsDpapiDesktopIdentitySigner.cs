using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Platform.Windows.Devices;

/// <summary>
/// Owns the desktop's long-lived pairing identity while keeping its PKCS#8 private key protected
/// by Windows DPAPI CurrentUser whenever it is at rest.
/// </summary>
/// <remarks>
/// An unreadable identity is never silently regenerated: paired tablets pin its public key, so
/// replacing corrupt material would look like an impersonating desktop and strand every device.
/// Session traffic keys do not enter this store and remain memory-only in transport adapters.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDpapiDesktopIdentitySigner : IDesktopIdentitySigner, IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TarkovCompanion.v2.paired-desktop-identity");
    private static readonly SemaphoreSlim CreationGate = new(1, 1);
    private readonly ECDsa _key;
    private readonly object _signGate = new();
    private bool _disposed;

    private WindowsDpapiDesktopIdentitySigner(ECDsa key)
    {
        _key = key;
        var subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
        try
        {
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(subjectPublicKeyInfo))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                Base64Url.EncodeToString(subjectPublicKeyInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
        }
    }

    public DesktopIdentityKey PublicKey { get; }

    public static async ValueTask<WindowsDpapiDesktopIdentitySigner> OpenOrCreateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The paired desktop identity requires Windows DPAPI.");
        }

        var fullPath = Path.GetFullPath(path);
        await CreationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(fullPath))
            {
                return await LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The desktop identity path has no parent directory."));
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privateKey = generated.ExportPkcs8PrivateKey();
            byte[] protectedKey;
            try
            {
                protectedKey = Protect(privateKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }

            var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.writing";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedKey, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, fullPath, overwrite: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
                TryDelete(temporaryPath);
            }

            return await LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CreationGate.Release();
        }
    }

    public byte[] Sign(ReadOnlySpan<byte> signatureInput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (signatureInput.IsEmpty)
        {
            throw new ArgumentException("A desktop identity signature input is required.", nameof(signatureInput));
        }

        lock (_signGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _key.SignData(
                signatureInput,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    public void Dispose()
    {
        lock (_signGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _key.Dispose();
        }
    }

    private static async ValueTask<WindowsDpapiDesktopIdentitySigner> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var protectedKey = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (protectedKey.Length == 0)
        {
            throw new InvalidDataException("The protected desktop identity is empty.");
        }

        byte[] privateKey;
        try
        {
            privateKey = Unprotect(protectedKey);
        }
        catch (Exception exception) when (exception is CryptographicException or Win32Exception)
        {
            throw new InvalidDataException("Windows could not open the protected desktop identity.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length || key.KeySize != 256)
            {
                throw new InvalidDataException("The protected desktop identity is not one P-256 private key.");
            }

            return new WindowsDpapiDesktopIdentitySigner(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    [SupportedOSPlatform("windows")]
    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var input = Allocate(inputBytes);
        var entropy = Allocate(Entropy);
        try
        {
            const uint forbidUserInterface = 0x1;
            var succeeded = protect
                ? DpapiNative.CryptProtectData(ref input, null, ref entropy, 0, 0, forbidUserInterface, out var output)
                : DpapiNative.CryptUnprotectData(ref input, 0, ref entropy, 0, 0, forbidUserInterface, out output);
            if (succeeded == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows DPAPI could not transform the desktop identity.");
            }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                ZeroUnmanaged(output.Data, output.Length);
                _ = DpapiNative.LocalFree(output.Data);
            }
        }
        finally
        {
            ZeroAndFree(input);
            ZeroAndFree(entropy);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }

        return new DataBlob { Length = bytes.Length, Data = pointer };
    }

    private static void ZeroAndFree(DataBlob blob)
    {
        ZeroUnmanaged(blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
    }

    private static void ZeroUnmanaged(nint data, int length)
    {
        if (data == 0 || length <= 0)
        {
            return;
        }

        var zeros = new byte[length];
        try
        {
            Marshal.Copy(zeros, 0, data, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zeros);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static partial class DpapiNative
    {
        [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int CryptProtectData(
            ref DataBlob input,
            string? description,
            ref DataBlob entropy,
            nint reserved,
            nint prompt,
            uint flags,
            out DataBlob output);

        [LibraryImport("crypt32.dll", SetLastError = true)]
        internal static partial int CryptUnprotectData(
            ref DataBlob input,
            nint description,
            ref DataBlob entropy,
            nint reserved,
            nint prompt,
            uint flags,
            out DataBlob output);

        [LibraryImport("kernel32.dll")]
        internal static partial nint LocalFree(nint memory);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Length;
        internal nint Data;
    }
}
