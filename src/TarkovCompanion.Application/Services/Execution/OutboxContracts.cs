using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.Application.Services.Execution;

public enum OutboxCommandKind
{
    RaidStarted = 1,
    RaidEventRecorded,
    RaidEnded,
}

public enum OutboxDeliveryState
{
    Pending = 1,
    Processing,
    Retrying,
    DeadLetter,
    Completed,
}

public readonly record struct OutboxContractVersion
{
    public OutboxContractVersion(int major, int minor)
    {
        if (major is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (minor is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }

        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public bool IsDefined => Major is >= 1 and <= 99 && Minor is >= 0 and <= 999;

    public static OutboxContractVersion Current { get; } = new(1, 0);
}

public sealed record OutboxAttemptPolicy
{
    public OutboxAttemptPolicy(
        int maxAttempts,
        TimeSpan attemptTimeout,
        TimeSpan initialRetryDelay,
        TimeSpan maxRetryDelay,
        double backoffFactor)
    {
        if (maxAttempts is < 1 or > OperationPolicy.MaxAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (attemptTimeout <= TimeSpan.Zero || attemptTimeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        }

        if (initialRetryDelay < TimeSpan.Zero || initialRetryDelay > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        }

        if (maxRetryDelay < initialRetryDelay || maxRetryDelay > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryDelay));
        }

        if (!double.IsFinite(backoffFactor) || backoffFactor is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffFactor));
        }

        MaxAttempts = maxAttempts;
        AttemptTimeout = attemptTimeout;
        InitialRetryDelay = initialRetryDelay;
        MaxRetryDelay = maxRetryDelay;
        BackoffFactor = backoffFactor;
    }

    public int MaxAttempts { get; }

    public TimeSpan AttemptTimeout { get; }

    public TimeSpan InitialRetryDelay { get; }

    public TimeSpan MaxRetryDelay { get; }

    public double BackoffFactor { get; }

    public static OutboxAttemptPolicy Default { get; } = new(
        5,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromSeconds(1),
        2);
}

/// <summary>A bounded immutable payload; only closed command codecs can create trusted payloads.</summary>
public sealed class OutboxPayload
{
    public const int MaxBytes = 64 * 1024;
    private static readonly string[] SensitiveSegments =
    [
        "capture", "screenshot", "pixel", "ocr", "secret", "token", "credential", "password",
        "path", "uri", "url", "name", "coordinate", "position", "latitude", "longitude",
    ];
    private static readonly string[] GenericFields =
    [
        "state", "code", "recovery", "diagnosticReference", "failureCount", "attemptCount",
        "pendingCount", "processingCount", "retryingCount", "deadLetterCount", "completedCount",
    ];
    private static readonly string[] GenericCountFields =
    [
        "failureCount", "attemptCount", "pendingCount", "processingCount", "retryingCount",
        "deadLetterCount", "completedCount",
    ];

    private OutboxPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        Bytes = ImmutableArray.Create(bytes.ToArray());
    }

    public ImmutableArray<byte> Bytes { get; }

    /// <summary>
    /// Builds an untyped diagnostic payload under the strict privacy boundary. Closed typed
    /// command adapters use their own reviewed codec and queue.
    /// </summary>
    public static OutboxPayload CreateGenericJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(json));
        }

        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A generic outbox payload must be one diagnostic object.", nameof(json));
        }

        ValidateGenericObject(document.RootElement);
        return new(bytes);
    }

    internal static OutboxPayload FromTypedJson<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    internal T ReadTypedJson<T>() where T : class =>
        JsonSerializer.Deserialize<T>(Bytes.AsSpan())
        ?? throw new InvalidDataException("The typed outbox payload was empty.");

    private static void ValidateGenericObject(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!GenericFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Generic outbox payload fields must come from the safe diagnostic vocabulary.", "json");
            }

            var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);
            if (SensitiveSegments.Any(segment => normalized.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Generic outbox payloads cannot carry sensitive fields.", "json");
            }

            if (GenericCountFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!property.Value.TryGetInt32(out var count) || count < 0)
                {
                    throw new ArgumentException("Generic diagnostic counts must be non-negative integers.", "json");
                }

                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Generic diagnostic identifiers must be strings.", "json");
            }

            ValidateGenericIdentifier(property.Value.GetString() ?? string.Empty);
        }
    }

    private static void ValidateGenericIdentifier(string text)
    {
        RuntimeIdentifier.Validate(text, "json");
    }
}

public sealed record OutboxItem
{
    public OutboxItem(
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CorrelationId correlationId,
        RuntimeFeatureId featureId,
        OutboxCommandKind command,
        OutboxContractVersion version,
        OutboxAggregateId aggregateId,
        long aggregateSequence,
        DateTimeOffset createdUtc,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset expiresUtc,
        OutboxPayload payload,
        OutboxAttemptPolicy attemptPolicy)
    {
        OperationId = operationId.IsDefined ? operationId : throw new ArgumentException("An operation id is required.", nameof(operationId));
        IdempotencyKey = idempotencyKey.IsDefined ? idempotencyKey : throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        CorrelationId = correlationId.IsDefined ? correlationId : throw new ArgumentException("A correlation id is required.", nameof(correlationId));
        FeatureId = featureId.IsDefined ? featureId : throw new ArgumentException("A feature id is required.", nameof(featureId));
        Command = Enum.IsDefined(command) ? command : throw new ArgumentOutOfRangeException(nameof(command));
        Version = version.IsDefined ? version : throw new ArgumentException("A command version is required.", nameof(version));
        AggregateId = aggregateId.IsDefined ? aggregateId : throw new ArgumentException("An aggregate id is required.", nameof(aggregateId));
        if (aggregateSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateSequence));
        }

        CreatedUtc = createdUtc.ToUniversalTime();
        NotBeforeUtc = notBeforeUtc.ToUniversalTime();
        ExpiresUtc = expiresUtc.ToUniversalTime();
        if (NotBeforeUtc < CreatedUtc || ExpiresUtc <= NotBeforeUtc)
        {
            throw new ArgumentException("Outbox timestamps must be ordered created, not-before, then expiry.");
        }

        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        AttemptPolicy = attemptPolicy ?? throw new ArgumentNullException(nameof(attemptPolicy));
        AggregateSequence = aggregateSequence;
    }

    public OperationId OperationId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public CorrelationId CorrelationId { get; }
    public RuntimeFeatureId FeatureId { get; }
    public OutboxCommandKind Command { get; }
    public OutboxContractVersion Version { get; }
    public OutboxAggregateId AggregateId { get; }
    public long AggregateSequence { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset NotBeforeUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public OutboxPayload Payload { get; }
    public OutboxAttemptPolicy AttemptPolicy { get; }
}

public sealed record OutboxStoredItem(
    OutboxItem Item,
    OutboxDeliveryState State,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    OutboxLeaseToken? LeaseToken,
    DateTimeOffset? LeaseExpiresUtc,
    RuntimeFault? LastFault,
    DateTimeOffset? CompletedUtc);

public sealed record OutboxCounts(
    int Pending,
    int Processing,
    int Retrying,
    int DeadLetter,
    int Completed)
{
    public int Total => checked(Pending + Processing + Retrying + DeadLetter + Completed);
}

public sealed record OutboxSnapshot(OutboxCounts Counts, TimeSpan? OldestOutstandingAge)
{
    public static OutboxSnapshot Empty { get; } = new(new(0, 0, 0, 0, 0), null);
}

public sealed record OutboxEnqueueReceipt(bool Added, OperationId OperationId);

public sealed class OutboxCapacityException() : Exception("The bounded outbox has no admission capacity.");

public interface IOutboxStore
{
    Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken);

    Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken);

    Task<bool> RetryAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset notBeforeUtc,
        RuntimeFault fault,
        CancellationToken cancellationToken);

    Task<bool> DeadLetterAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        RuntimeFault fault,
        DateTimeOffset deadLetteredUtc,
        CancellationToken cancellationToken);

    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken);
}

public readonly record struct OutboxDeliveryContext(
    OperationId OperationId,
    IdempotencyKey IdempotencyKey,
    CorrelationId CorrelationId,
    RuntimeFeatureId FeatureId,
    int Attempt);

public interface IOutboxCommandHandler
{
    Task HandleAsync(OutboxItem item, OutboxDeliveryContext context, CancellationToken cancellationToken);
}
