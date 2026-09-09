using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Platform.Windows.Security;

/// <summary>Stores only DPAPI CurrentUser-protected bytes on disk.</summary>
public sealed partial class WindowsDpapiSecretStore : ISecretStore
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

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(secret));
        var path = PathFor(key);
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

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(Unprotect(protectedBytes));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI secret storage is available only on Windows.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string key) =>
        Path.Combine(_root, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.secret");

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "Secret key must not exceed 256 characters.");
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
                _ = DpapiNative.LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
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
