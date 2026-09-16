using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class ExecutionPrimitiveTests
{
    [Theory]
    [InlineData("")]
    [InlineData("contains a space")]
    [InlineData("contains/path")]
    [InlineData("coordinate,12.5")]
    public void RuntimeIdentifiersRejectUnsafeValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeFeatureId(value));
        Assert.ThrowsAny<ArgumentException>(() => new OperationScopeId(value));
    }

    [Fact]
    public void OperationPolicyRejectsRetryForNonIdempotentWork()
    {
        Assert.Throws<ArgumentException>(() => new OperationPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            0,
            3,
            TimeSpan.FromSeconds(1),
            WorkloadClass.Light,
            OperationRestartMode.Never,
            IdempotencyRequirement.SingleAttempt));
    }

    [Fact]
    public void RetryPolicyThatRequiresAKeyFailsClosed()
    {
        var policy = RetryPolicy(IdempotencyRequirement.RequireKey);

        Assert.Throws<ArgumentException>(() => new OperationExecutionRequest(
            new("feature"),
            OperationId.New(),
            CorrelationId.New(),
            new("dependency"),
            policy));
    }

    [Fact]
    public void RuntimeFaultHasNoSlotForSensitiveOrRawExceptionData()
    {
        var propertyNames = typeof(RuntimeFault).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Message", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(Exception), typeof(RuntimeFault).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void GenericPayloadRejectsPrivateAndCoordinateFields()
    {
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("{\"ocrText\":\"value\"}"));
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("{\"userName\":\"value\"}"));
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("{\"position\":12.5}"));
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("{\"detail\":\"/private/path\"}"));
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("{\"failureCount\":12.5}"));
        Assert.Throws<ArgumentException>(() => OutboxPayload.CreateGenericJson("[12.5,42.1]"));

        var safe = OutboxPayload.CreateGenericJson("{\"failureCount\":2,\"state\":\"open\"}");
        Assert.NotEmpty(safe.Bytes);
    }

    [Fact]
    public void LatestGateRejectsAnOlderCompletionEvenWhenItIgnoresCancellation()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("map-selection");
        var older = gate.Begin(scope);
        var newer = gate.Begin(scope);
        var published = string.Empty;

        Assert.True(older.CancellationToken.IsCancellationRequested);
        Assert.False(gate.TryCommit(older, () => published = "old"));
        Assert.True(gate.TryCommit(newer, () => published = "new"));
        Assert.Equal("new", published);
    }

    internal static OperationPolicy RetryPolicy(IdempotencyRequirement idempotency) => new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(10),
        3,
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(1),
        2,
        0,
        4,
        TimeSpan.FromSeconds(5),
        WorkloadClass.IO,
        OperationRestartMode.Manual,
        idempotency);
}
