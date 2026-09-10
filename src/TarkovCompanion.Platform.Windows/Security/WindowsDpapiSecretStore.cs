using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Platform.Windows.Security;

/// <summary>Stores only DPAPI CurrentUser-protected integration secrets on disk.</summary>
public sealed partial class WindowsDpapiSecretStore : IIntegrationSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TarkovCompanion.v1.local-secrets");
    private readonly string _root;

    public WindowsDpapiSecretStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovCompanion",
            "Secrets");
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task SaveAsync(
        IntegrationSecretReference reference,
        string secret,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        var plaintextBytes = Encoding.UTF8.GetBytes(secret);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Protect(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        var path = PathFor(reference);
        var temporaryPath = Path.Combine(_root, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string?> LoadAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        var path = PathFor(reference);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plaintextBytes = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public Task<bool> ExistsAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsAvailable && File.Exists(PathFor(reference)));
    }

    public Task DeleteAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(reference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(IntegrationSecretReference reference)
    {
        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{reference.Kind}:{reference.ProfileId:D}:{reference.GameMode}:{reference.ProfileGeneration}");
        return Path.Combine(_root, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.secret");
    }

    private static void ValidateReference(IntegrationSecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Enum.IsDefined(reference.Kind) || reference.ProfileId == Guid.Empty ||
            !Enum.IsDefined(reference.GameMode) ||
            string.IsNullOrWhiteSpace(reference.ProfileGeneration) ||
            reference.ProfileGeneration.Length > 128 ||
            !reference.ProfileGeneration.Equals(reference.ProfileGeneration.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The integration secret reference is invalid.", nameof(reference));
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
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows DPAPI could not transform the secret.");
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

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        if (bytes.Length > 0)
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }

        return new DataBlob { Length = bytes.Length, Data = pointer };
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
