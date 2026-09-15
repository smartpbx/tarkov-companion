using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// One deadline for every OCR pass a single captured frame receives from one recognizer.
/// </summary>
/// <remarks>
/// Each provider call already stops at its own frame timeout. That bounded a call, not a scan:
/// the coordinator reads a frame twice and the container recognizer up to twenty-five times, so
/// fifteen seconds per call quietly became minutes per frame. The deadline starts once, is
/// linked to the caller's token, and every pass for the frame spends from it. A provider's own
/// frame timeout remains an inner cap on each call.
/// </remarks>
public sealed record OcrPipelineOptions
{
    /// <summary>Wall-clock budget shared by every OCR pass for one frame.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>A started pipeline deadline linked to the caller's cancellation.</summary>
internal sealed class OcrPipelineDeadline : IDisposable
{
    public const string DiagnosticCode = "ocr_pipeline_timeout";

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _caller;

    private OcrPipelineDeadline(CancellationTokenSource source, CancellationToken caller)
    {
        _source = source;
        _caller = caller;
    }

    public CancellationToken Token => _source.Token;

    /// <summary>True when the deadline, rather than the caller, ended the work.</summary>
    public bool IsExpired => _source.IsCancellationRequested && !_caller.IsCancellationRequested;

    public static OcrPipelineOptions Validate(OcrPipelineOptions? options)
    {
        options ??= new OcrPipelineOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The OCR pipeline deadline must be positive and bounded.");
        }

        return options;
    }

    public static OcrPipelineDeadline Start(OcrPipelineOptions options, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.Timeout);
        return new(source, cancellationToken);
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>How an OCR result reads once provider detail is gone.</summary>
internal static class OcrOutcome
{
    public const string NoText = "ocr_no_text";
    public const string RegionEmpty = "ocr_region_empty";
    public const string MemoryExhausted = "ocr_memory_exhausted";
    public const string DeduplicationBudgetExhausted = "ocr_dedupe_budget_exhausted";

    /// <summary>
    /// The provider was available, nothing degraded the read, and no text came back. This is a
    /// measured answer about the pixels, distinct from a provider that could not answer.
    /// </summary>
    public static bool IsEmpty(OcrResult result) =>
        result.IsAvailable &&
        result.Lines.Count == 0 &&
        result.DiagnosticCode is null or NoText or RegionEmpty;

    /// <summary>The read is missing evidence: unavailable, or carrying a degradation code.</summary>
    public static bool IsDegraded(OcrResult result) =>
        !result.IsAvailable ||
        result.DiagnosticCode is not (null or NoText or RegionEmpty);

    public static bool IsMemoryExhausted(OcrResult result) =>
        string.Equals(result.DiagnosticCode, MemoryExhausted, StringComparison.Ordinal);
}
