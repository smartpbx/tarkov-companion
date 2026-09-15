using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.Application.Services.Execution;

/// <summary>The closed set of durable commands. Values are persisted and must never be reused.</summary>
public enum OutboxCommandKind
{
    RaidStarted = 1,
    RaidStateRecorded = 2,
    RaidEnded = 3,
    RaidPositionRecorded = 4,
    RaidExtractsRecorded = 5,
    RaidScanRecorded = 6,
    RaidSaleRecorded = 7,
    RaidQuestRecorded = 8,
}

public enum OutboxDeliveryState
{
    Pending = 1,
    Processing,
    Retrying,
    DeadLetter,
    Completed,
}

public enum OutboxPumpState
{
    Idle = 1,
    Running,
    Faulted,
    Stopping,
    Stopped,
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

    // Typed command payloads are read back strictly: a member the codec did not write, a
    // missing constructor argument, or a null where the contract says none is a corrupt command
    // rather than something to tolerate and deliver.
    private static readonly JsonSerializerOptions ClosedTypedJson = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

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

    /// <summary>Rehydrates bytes already accepted by a durable outbox.</summary>
    /// <remarks>
    /// This is deliberately not a generic producer API: it validates only the bounded JSON
    /// envelope, while the closed command codec still validates the exact typed shape before
    /// delivery. Persistence cannot call the internal typed factory because it does not know
    /// which command type owns the bytes.
    /// </remarks>
    public static OutboxPayload FromStoredBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes)
        {
            throw new InvalidDataException("A stored outbox payload is outside the bounded contract.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A stored outbox payload must be one JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A stored outbox payload is malformed JSON.", exception);
        }

        return new(bytes);
    }

    internal static OutboxPayload FromTypedJson<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    internal T ReadTypedJson<T>() where T : class =>
        JsonSerializer.Deserialize<T>(Bytes.AsSpan(), ClosedTypedJson)
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

/// <summary>A dead-lettered command as health reporting may show it: identity and fault, never payload.</summary>
public sealed record OutboxDeadLetterSnapshot(
    OperationId OperationId,
    OutboxAggregateId AggregateId,
    long AggregateSequence,
    OutboxCommandKind Command,
    int AttemptCount,
    RuntimeFault? LastFault,
    DateTimeOffset? DeadLetteredUtc)
{
    /// <summary>Whether the command remains inside its retention window and can be retried.</summary>
    public bool CanRetry { get; init; } = true;

    /// <summary>
    /// Whether the command must be explicitly resolved instead of retried. Resolution records a
    /// deliberate discard and releases the next command in this aggregate; it is never implicit.
    /// </summary>
    public bool RequiresResolution => !CanRetry;
}

/// <summary>Store counts plus the delivery health an adapter publishes into runtime state.</summary>
public sealed record OutboxSnapshot(OutboxCounts Counts, TimeSpan? OldestOutstandingAge)
{
    public const int MaxListedDeadLetters = 16;

    /// <summary>
    /// A bounded recovery-prioritized dead-letter window. Retryable rows come first, then each
    /// class is oldest-first so permanently un-retryable rows cannot hide actionable failures.
    /// </summary>
    public ImmutableArray<OutboxDeadLetterSnapshot> DeadLetters { get; init; } = [];

    public OutboxPumpState PumpState { get; init; } = OutboxPumpState.Idle;

    /// <summary>The store or processor failure that paused delivery, until a pass succeeds.</summary>
    public RuntimeFault? LastPumpFault { get; init; }

    public int ConsecutivePumpFaults { get; init; }

    public DateTimeOffset? LastSuccessfulPumpUtc { get; init; }

    /// <summary>Why the most recent acceptance was refused, until one is accepted.</summary>
    public RuntimeFault? LastAcceptanceFault { get; init; }

    public static OutboxSnapshot Empty { get; } = new(new(0, 0, 0, 0, 0), null);
}

public sealed record OutboxEnqueueReceipt(bool Added, OperationId OperationId);

public sealed class OutboxCapacityException() : Exception("The bounded outbox has no admission capacity.");

/// <summary>A leased, ordered, at-least-once command store.</summary>
/// <remarks>
/// A store may forget a <see cref="OutboxDeliveryState.Completed"/> row once it has been
/// retained long enough. Dead letters count against bounded admission until an operator retries
/// or explicitly resolves them, because a dead-lettered head is what holds the rest of its
/// aggregate in order. A processor may issue one lease renewal concurrently with one terminal
/// mutation for the same operation. Implementations must make every mutation an exact-token
/// atomic compare-and-swap and remain thread-safe under that bounded overlap.
/// </remarks>
public interface IOutboxStore
{
    Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken);

    /// <summary>Accepts every item or none of them, returning one receipt per item in order.</summary>
    /// <remarks>
    /// One observed transition can need several commands — a raid start, its state, and the end
    /// of the raid before it. Accepting them one at a time let a later refusal leave an earlier
    /// command stored for a transition that was never applied.
    /// </remarks>
    Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
        ImmutableArray<OutboxItem> items,
        CancellationToken cancellationToken);

    Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes only the unexpired processing lease identified by the exact token. A false
    /// result is an ownership loss, never a reason to invoke an already-successful handler again.
    /// </summary>
    Task<bool> CompleteAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extends the unexpired processing lease identified by the exact token without changing its
    /// attempt count. The owner renews from handler launch through terminal store acknowledgement,
    /// including while a timed-out handler or an acknowledgement retry remains outstanding.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only when the same token still owns an unexpired processing row;
    /// otherwise <see langword="false"/>.
    /// </returns>
    Task<bool> RenewLeaseAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns only the unexpired processing lease identified by the exact token to retry state.
    /// <paramref name="retryingUtc"/> is the injected-clock transition time; the later
    /// <paramref name="notBeforeUtc"/> controls when the next attempt becomes eligible.
    /// </summary>
    Task<bool> RetryAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset retryingUtc,
        DateTimeOffset notBeforeUtc,
        RuntimeFault fault,
        CancellationToken cancellationToken);

    /// <summary>Dead-letters only the unexpired processing lease identified by the exact token.</summary>
    Task<bool> DeadLetterAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        RuntimeFault fault,
        DateTimeOffset deadLetteredUtc,
        CancellationToken cancellationToken);

    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly resolves a dead letter that must not be replayed. This is the only operation
    /// that may release a dead-lettered aggregate head without delivering that command.
    /// </summary>
    Task<bool> ResolveDeadLetterAsync(
        OperationId operationId,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken);

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
