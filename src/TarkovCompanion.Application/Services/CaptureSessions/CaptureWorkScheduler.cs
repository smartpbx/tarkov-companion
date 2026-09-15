namespace TarkovCompanion.Application.Services.CaptureSessions;

public enum CaptureWorkPriority
{
    Intake = 1,
    ReviewBlocking,
}

public sealed record CaptureWorkRequest(
    CaptureCorrelationId CorrelationId,
    string Scope,
    CaptureWorkPriority Priority)
{
    public CaptureCorrelationId CorrelationId { get; } = CorrelationId.IsDefined
        ? CorrelationId
        : throw new ArgumentException("A capture correlation id is required.", nameof(CorrelationId));

    public string Scope { get; } = string.IsNullOrWhiteSpace(Scope) || Scope.Trim().Length > 128
        ? throw new ArgumentException("A bounded capture work scope is required.", nameof(Scope))
        : Scope.Trim();

    public CaptureWorkPriority Priority { get; } = Enum.IsDefined(Priority)
        ? Priority
        : throw new ArgumentOutOfRangeException(nameof(Priority));
}

public sealed record CaptureWorkResult(bool Accepted, bool Succeeded, string? DiagnosticCode)
{
    public static CaptureWorkResult Success { get; } = new(true, true, null);
}

/// <summary>
/// Capture-owned admission seam. The runtime composition owner adapts this to the process-wide
/// supervisor; capture code does not bind to an in-flight runtime implementation contract.
/// </summary>
public interface ICaptureWorkScheduler
{
    Task<CaptureWorkResult> RunAsync(
        CaptureWorkRequest request,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}

/// <summary>Deterministic fixture scheduler; production composition must use the runtime adapter.</summary>
public sealed class InlineCaptureWorkScheduler : ICaptureWorkScheduler
{
    public async Task<CaptureWorkResult> RunAsync(
        CaptureWorkRequest request,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return CaptureWorkResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(true, false, "capture_work_cancelled");
        }
        catch
        {
            return new(true, false, "capture_work_failed");
        }
    }
}
