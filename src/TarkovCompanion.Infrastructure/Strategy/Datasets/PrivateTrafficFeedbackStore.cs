using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Infrastructure.Strategy.Datasets;

/// <summary>
/// A bounded, private-local feedback journal with optimistic append and actual record deletion.
/// </summary>
/// <remarks>
/// This store deliberately has no enumeration or export API. Corrections and revocations append to
/// one validated history; deletion removes that complete history from the next atomically replaced
/// document. The exclusive lease prevents two desktop processes from publishing divergent journals.
/// </remarks>
public sealed class PrivateTrafficFeedbackStore : IDisposable
{
    public const int FormatVersion = 1;
    public const int MaximumHistories = 4_096;
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;

    private readonly string _path;
    private readonly IDisposable _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<Guid, TrafficFeedbackHistory> _histories;
    private bool _disposed;
    private int _disposeStarted;

    private PrivateTrafficFeedbackStore(
        string path,
        IDisposable lease,
        IReadOnlyDictionary<Guid, TrafficFeedbackHistory> histories)
    {
        _path = path;
        _lease = lease;
        _histories = histories;
    }

    public static async ValueTask<PrivateTrafficFeedbackStore> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The private feedback path has no parent directory."));
        var lease = AcquireLease(fullPath + ".lock");
        try
        {
            var histories = await LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return new PrivateTrafficFeedbackStore(fullPath, lease, histories);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async ValueTask<TrafficFeedbackHistory?> ReadAsync(
        Guid feedbackId,
        CancellationToken cancellationToken = default)
    {
        RequireFeedbackId(feedbackId);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _histories.GetValueOrDefault(feedbackId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<TrafficFeedbackHistory> SubmitAsync(
        HistoricalTrafficFeedback feedback,
        TrafficFeedbackActor actor,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        return MutateAsync(
            feedback.FeedbackId,
            expectedRevision: 0,
            current =>
            {
                if (current is not null)
                {
                    throw new InvalidOperationException("That private feedback id already exists.");
                }

                var submitted = new TrafficFeedbackEvent(
                    Guid.NewGuid(),
                    feedback.FeedbackId,
                    1,
                    TrafficFeedbackEventKind.Submitted,
                    occurredUtc,
                    actor,
                    feedback);
                return new TrafficFeedbackHistory(feedback.FeedbackId, [submitted]);
            },
            cancellationToken);
    }

    public ValueTask<TrafficFeedbackHistory> CorrectAsync(
        HistoricalTrafficFeedback correctedFeedback,
        long expectedRevision,
        TrafficFeedbackActor actor,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correctedFeedback);
        return MutateAsync(
            correctedFeedback.FeedbackId,
            expectedRevision,
            current =>
            {
                var existing = current ?? throw new KeyNotFoundException("Private feedback was not found.");
                var prior = existing.Events[^1];
                var corrected = new TrafficFeedbackEvent(
                    Guid.NewGuid(),
                    correctedFeedback.FeedbackId,
                    checked(prior.Revision + 1),
                    TrafficFeedbackEventKind.Corrected,
                    occurredUtc,
                    actor,
                    correctedFeedback,
                    prior.EventId);
                return new TrafficFeedbackHistory(
                    correctedFeedback.FeedbackId,
                    existing.Events.Append(corrected).ToArray());
            },
            cancellationToken);
    }

    public ValueTask<TrafficFeedbackHistory> RevokeAsync(
        Guid feedbackId,
        long expectedRevision,
        TrafficFeedbackActor actor,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            feedbackId,
            expectedRevision,
            current =>
            {
                var existing = current ?? throw new KeyNotFoundException("Private feedback was not found.");
                var prior = existing.Events[^1];
                var revoked = new TrafficFeedbackEvent(
                    Guid.NewGuid(),
                    feedbackId,
                    checked(prior.Revision + 1),
                    TrafficFeedbackEventKind.Revoked,
                    occurredUtc,
                    actor,
                    value: null,
                    prior.EventId);
                return new TrafficFeedbackHistory(
                    feedbackId,
                    existing.Events.Append(revoked).ToArray());
            },
            cancellationToken);

    public async ValueTask<bool> DeleteAsync(
        Guid feedbackId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireFeedbackId(feedbackId);
        RequireExpectedRevision(expectedRevision, allowZero: false, appendsEvent: false);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_histories.TryGetValue(feedbackId, out var existing))
            {
                return false;
            }

            RequireRevision(existing, expectedRevision);
            var next = new Dictionary<Guid, TrafficFeedbackHistory>(_histories);
            _ = next.Remove(feedbackId);
            await SaveAsync(_path, next.Values, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _histories, next);
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
            _lease.Dispose();
            _histories = new Dictionary<Guid, TrafficFeedbackHistory>();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async ValueTask<TrafficFeedbackHistory> MutateAsync(
        Guid feedbackId,
        long expectedRevision,
        Func<TrafficFeedbackHistory?, TrafficFeedbackHistory> mutation,
        CancellationToken cancellationToken)
    {
        RequireFeedbackId(feedbackId);
        RequireExpectedRevision(expectedRevision, allowZero: true, appendsEvent: true);
        ArgumentNullException.ThrowIfNull(mutation);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _histories.TryGetValue(feedbackId, out var current);
            if (current is not null)
            {
                RequireRevision(current, expectedRevision);
            }
            else if (expectedRevision != 0)
            {
                throw new InvalidOperationException("Private feedback revision does not match the durable journal.");
            }

            var updated = mutation(current);
            var next = new Dictionary<Guid, TrafficFeedbackHistory>(_histories)
            {
                [feedbackId] = updated,
            };
            if (next.Count > MaximumHistories)
            {
                throw new InvalidOperationException($"The private feedback journal is limited to {MaximumHistories} histories.");
            }

            await SaveAsync(_path, next.Values, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _histories, next);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private static IDisposable AcquireLease(string leasePath)
    {
        try
        {
            return new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
        }
        catch (IOException exception)
        {
            throw new IOException("The private feedback journal is already open in another desktop instance.", exception);
        }
    }

    private static async ValueTask<IReadOnlyDictionary<Guid, TrafficFeedbackHistory>> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<Guid, TrafficFeedbackHistory>();
        }

        byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var initialLength = stream.Length;
            if (initialLength is <= 0 or > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The private feedback journal is empty or exceeds its byte bound.");
            }

            bytes = ArrayPool<byte>.Shared.Rent(checked((int)initialLength));
            var read = 0;
            while (read < initialLength)
            {
                var count = await stream.ReadAsync(
                    bytes.AsMemory(read, checked((int)initialLength) - read),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read != initialLength || stream.Length != initialLength)
            {
                throw new InvalidDataException("The private feedback journal changed while it was read.");
            }

            var payload = bytes.AsSpan(0, read);
            RejectDuplicateProperties(payload);
            var document = JsonSerializer.Deserialize<PersistedFeedbackDocument>(payload, TrafficDataJson.Options)
                           ?? throw new InvalidDataException("The private feedback journal was null.");
            if (document.FormatVersion != FormatVersion || document.Histories.Count > MaximumHistories)
            {
                throw new InvalidDataException("The private feedback journal format or count is unsupported.");
            }

            var histories = document.Histories.ToDictionary(history => history.FeedbackId);
            if (histories.Count != document.Histories.Count)
            {
                throw new InvalidDataException("Private feedback ids must be unique.");
            }

            return histories;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The private feedback journal failed strict validation.", exception);
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }

    private static async ValueTask SaveAsync(
        string path,
        IEnumerable<TrafficFeedbackHistory> histories,
        CancellationToken cancellationToken)
    {
        var ordered = histories.OrderBy(history => history.FeedbackId).ToArray();
        var document = new PersistedFeedbackDocument(FormatVersion, ordered);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, TrafficDataJson.Options);
        var temporary = path + $".{Guid.NewGuid():N}.writing";
        try
        {
            if (bytes.Length > MaximumDocumentBytes)
            {
                throw new InvalidOperationException("The private feedback journal exceeds its byte bound.");
            }

            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
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
                        throw new InvalidDataException("The private feedback journal has an invalid container boundary.");
                    }

                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (containers.Count == 0 || containers.Peek() is not { } properties || !properties.Add(name))
                    {
                        throw new InvalidDataException("The private feedback journal contains a duplicate property.");
                    }

                    break;
            }
        }

        if (containers.Count != 0)
        {
            throw new InvalidDataException("The private feedback journal has an unterminated container.");
        }
    }

    private static void RequireFeedbackId(Guid feedbackId)
    {
        if (feedbackId == Guid.Empty)
        {
            throw new ArgumentException("A private feedback id is required.", nameof(feedbackId));
        }
    }

    private static void RequireExpectedRevision(long expectedRevision, bool allowZero, bool appendsEvent)
    {
        var maximum = appendsEvent
            ? TrafficDataBounds.MaximumFeedbackEvents - 1L
            : TrafficDataBounds.MaximumFeedbackEvents;
        if (expectedRevision < (allowZero ? 0 : 1) || expectedRevision > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
    }

    private static void RequireRevision(TrafficFeedbackHistory history, long expectedRevision)
    {
        if (history.Events.Count != expectedRevision)
        {
            throw new InvalidOperationException("Private feedback revision does not match the durable journal.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PersistedFeedbackDocument(
        int FormatVersion,
        IReadOnlyList<TrafficFeedbackHistory> Histories);
}
