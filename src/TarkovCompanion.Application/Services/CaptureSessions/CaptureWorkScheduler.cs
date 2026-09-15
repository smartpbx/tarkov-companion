using TarkovCompanion.Application.Services.Execution;

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

/// <summary>
/// Production adapter onto the process-wide bounded supervisor introduced by #268. Registration
/// remains with the application composition owner; this adapter keeps capture from inventing a
/// second scheduler or bypassing interactive admission.
/// </summary>
public sealed class SupervisedCaptureWorkScheduler : ICaptureWorkScheduler
{
    private static readonly RuntimeFeatureId FeatureId = new("capture-sessions");
    private static readonly RuntimeDependencyId DependencyId = new("capture-recognition");
    private readonly IBackgroundWorkSupervisor _supervisor;
    private readonly TimeSpan _operationTimeout;

    public SupervisedCaptureWorkScheduler(
        IBackgroundWorkSupervisor supervisor,
        TimeSpan? operationTimeout = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(45);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    public async Task<CaptureWorkResult> RunAsync(
        CaptureWorkRequest request,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        var execution = new OperationExecutionRequest(
            FeatureId,
            OperationId.New(),
            new CorrelationId(request.CorrelationId.Value),
            DependencyId,
            OperationPolicy.Once(_operationTimeout, WorkloadClass.CPU));
        var admission = _supervisor.Submit(
            new(
                execution,
                new OperationScopeId(request.Scope),
                request.Priority == CaptureWorkPriority.ReviewBlocking
                    ? WorkPriority.UserBlocking
                    : WorkPriority.Interactive),
            (_, token) => operation(token),
            cancellationToken);
        if (!admission.Accepted)
        {
            return new(false, false, admission.Fault?.Code.Value ?? "capture_supervisor_rejected");
        }

        var completed = await admission.Handle.Completion.ConfigureAwait(false);
        return completed.State == BackgroundWorkState.Succeeded
            ? CaptureWorkResult.Success
            : new(true, false, completed.Fault?.Code.Value ?? $"capture_{completed.State.ToString().ToLowerInvariant()}");
    }
}
