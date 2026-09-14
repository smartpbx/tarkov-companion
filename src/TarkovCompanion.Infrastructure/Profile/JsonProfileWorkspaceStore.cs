using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Profile;

/// <summary>
/// Profile-context's small local adapter. It atomically replaces one complete revision and never
/// writes a partially switched active profile. The v2 persistence work can replace this adapter
/// behind <see cref="IProfileWorkspaceStore"/> without changing lifecycle semantics.
/// </summary>
public sealed class JsonProfileWorkspaceStore : IProfileWorkspaceStore, IDisposable
{
    private const int MaximumBytes = 2_097_152;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonProfileWorkspaceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A profile workspace path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public async Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryReplaceAsync(long expectedRevision, ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Revision != checked(expectedRevision + 1))
            throw new ArgumentException("A replacement must advance exactly one revision.", nameof(replacement));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision) return false;
            await WriteUnsafeAsync(replacement, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ProfileWorkspaceSnapshot> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new ProfileWorkspaceSnapshot(0, null, []);
        if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException("Profile workspace exceeds the 2 MiB format limit.");
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var workspace = JsonSerializer.Deserialize<ProfileWorkspaceSnapshot>(json, Options)
                ?? throw new InvalidDataException("Profile workspace is empty.");
            return workspace;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Profile workspace is invalid; it was left unchanged.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("Profile workspace contains an unsupported value; it was left unchanged.", exception);
        }
    }

    private async Task WriteUnsafeAsync(ProfileWorkspaceSnapshot replacement, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(replacement, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidOperationException("Profile workspace exceeds the 2 MiB format limit.");
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
