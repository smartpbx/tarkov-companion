using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Application.Services.Updates;

namespace TarkovCompanion.Infrastructure.Updates;

/// <summary>Performs bounded release-staging I/O outside the application orchestration layer.</summary>
public sealed class FileSystemReleaseStagingStore : IReleaseStagingStore
{
    public string Create(string stagingRoot)
    {
        var root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root);
        RequireUnredirectedDirectoryTree(root, "release staging root");

        var staging = Path.Combine(root, $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        RequireUnredirectedDirectoryTree(staging, "release staging directory");
        return staging;
    }

    public void Delete(string stagingRoot, string stagingDirectory)
    {
        if (!IsOwnedStagingDirectory(stagingRoot, stagingDirectory))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the release staging root.");
        }

        var root = Path.GetFullPath(stagingRoot);
        RequireUnredirectedDirectoryTree(root, "release staging root");
        var info = new DirectoryInfo(Path.GetFullPath(stagingDirectory));
        info.Refresh();
        if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
        {
            throw new InvalidOperationException("Refusing to follow a redirected release staging directory.");
        }

        try
        {
            Directory.Delete(info.FullName, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public ReleaseStagedFile RequirePlainFile(string path, long maximumBytes, string label)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"{label} is missing, redirected, empty, or outside its byte limit.");
        }

        return new ReleaseStagedFile(info.FullName, info.Length);
    }

    public async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonDocument> ReadJsonAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = RequirePlainFile(path, maximumBytes, "release JSON");
        var bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(
            info.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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

    public async Task<string> Sha256Async(
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
                    throw new InvalidDataException("A release file grew beyond its authenticated byte limit.");
                }

                hash.AppendData(rented, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsOwnedStagingDirectory(string stagingRoot, string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot) || string.IsNullOrWhiteSpace(stagingDirectory))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var candidate = Path.GetFullPath(stagingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(candidate);
        return Path.GetDirectoryName(candidate)?.Equals(root, comparison) == true &&
               name.StartsWith("release-", StringComparison.Ordinal) &&
               Guid.TryParseExact(name["release-".Length..], "N", out _);
    }

    private static void RequireUnredirectedDirectoryTree(string path, string label)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            current.Refresh();
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint) || current.LinkTarget is not null)
            {
                throw new InvalidDataException($"The {label} is missing or redirected.");
            }
        }
    }
}
