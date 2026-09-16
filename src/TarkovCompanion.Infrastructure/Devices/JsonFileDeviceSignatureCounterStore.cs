using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.Infrastructure.Devices;

/// <summary>
/// Atomically persists the small monotonic WebAuthn counter registry used by paired devices.
/// </summary>
/// <remarks>
/// Counter corruption is an authorization failure, not an empty registry. The lock covers the
/// read/compare/write transaction so two simultaneous resume proofs cannot both advance from the
/// same stored value.
/// </remarks>
public sealed class JsonFileDeviceSignatureCounterStore : IDeviceSignatureCounterStore, IDisposable
{
    public const int FormatVersion = 1;
    public const int MaximumCounters = ProtocolBounds.MaxDevices * 4;
    public const int MaximumDocumentBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly string _path;
    private readonly string _leasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private int _disposeStarted;

    public JsonFileDeviceSignatureCounterStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _leasePath = _path + ".lock";
    }

    public async ValueTask<bool> TryAcceptAsync(
        DeviceSignatureCounter candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposeStarted) != 0, this);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The WebAuthn counter path has no parent directory."));
            using var lease = AcquireLease();
            var counters = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existingIndex = counters.FindIndex(item => item.DeviceKeyId == candidate.DeviceKeyId);
            if (existingIndex >= 0)
            {
                var existing = counters[existingIndex];
                if (existing.ChallengeId == candidate.ChallengeId)
                {
                    return existing.SignatureCounter == candidate.SignatureCounter &&
                           existing.ChallengeIssuedUtc == candidate.ChallengeIssuedUtc;
                }

                if (existing.SignatureCounter > 0 && candidate.SignatureCounter <= existing.SignatureCounter)
                {
                    return false;
                }

                counters[existingIndex] = candidate;
            }
            else
            {
                if (counters.Count >= MaximumCounters)
                {
                    throw new InvalidDataException("The WebAuthn counter registry is full and fails closed.");
                }

                counters.Add(candidate);
            }

            await WriteAsync(counters, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }

        // A proof can pass the pre-wait disposal guard immediately before disposal begins.
        // Leaving this managed semaphore alive lets every such waiter drain and fail closed.
    }

    private FileStream AcquireLease()
    {
        try
        {
            return new FileStream(
                _leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
        }
        catch (IOException exception)
        {
            throw new IOException("The WebAuthn counter registry is being updated by another desktop process.", exception);
        }
    }

    private async ValueTask<List<DeviceSignatureCounter>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The WebAuthn counter registry is empty or oversized.");
            }

            var bytes = new byte[checked((int)stream.Length)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read != bytes.Length || stream.Length != bytes.Length)
            {
                throw new InvalidDataException("The WebAuthn counter registry changed while it was read.");
            }

            RejectDuplicateProperties(bytes);
            var document = JsonSerializer.Deserialize<CounterDocument>(bytes, Options)
                           ?? throw new InvalidDataException("The WebAuthn counter registry was null.");
            if (document.Counters is null ||
                document.FormatVersion != FormatVersion || document.Counters.Count > MaximumCounters ||
                document.Counters.Select(item => item.DeviceKeyId).Distinct().Count() != document.Counters.Count)
            {
                throw new InvalidDataException("The WebAuthn counter registry has an unsupported or ambiguous shape.");
            }

            return document.Counters.ToList();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
                                              InvalidOperationException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("The WebAuthn counter registry failed strict validation.", exception);
        }
    }

    private async ValueTask WriteAsync(
        IReadOnlyList<DeviceSignatureCounter> counters,
        CancellationToken cancellationToken)
    {
        var ordered = counters.OrderBy(item => item.DeviceKeyId.Value, StringComparer.Ordinal).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CounterDocument(FormatVersion, ordered), Options);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The WebAuthn counter registry exceeds its byte bound.");
        }

        await AtomicJsonFile.WriteAsync(
            _path,
            System.Text.Encoding.UTF8.GetString(bytes),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(CompanionProtocolJson.Options)
        {
            MaxDepth = 8,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
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
                        throw new InvalidDataException("The WebAuthn counter registry has an invalid container boundary.");
                    }

                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (containers.Count == 0 || containers.Peek() is not { } properties || !properties.Add(name))
                    {
                        throw new InvalidDataException("The WebAuthn counter registry contains a duplicate property.");
                    }

                    break;
            }
        }

        if (containers.Count != 0)
        {
            throw new InvalidDataException("The WebAuthn counter registry has an unterminated container.");
        }
    }

    private sealed record CounterDocument(
        int FormatVersion,
        IReadOnlyList<DeviceSignatureCounter> Counters);
}
