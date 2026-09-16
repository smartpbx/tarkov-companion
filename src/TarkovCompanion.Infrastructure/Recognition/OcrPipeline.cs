using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// The deadline a recognizer starts for a frame when it is not already reading one inside a scan.
/// </summary>
/// <remarks>
/// Each provider call already stops at its own frame timeout. That bounded a call, not a scan:
/// the coordinator reads a frame twice and the container recognizer up to twenty-five times, so
/// fifteen seconds per call quietly became minutes per frame. Inside a scan the frame's deadline
/// is <see cref="ScanFrameDeadline"/>, started once by the scan, and a recognizer handed its token
/// joins it; this budget then plays no part. A recognizer called on its own starts this one, and
/// the components it calls join that. A provider's own frame timeout remains an inner cap on each
/// call.
/// </remarks>
public sealed record OcrPipelineOptions
{
    /// <summary>Wall-clock budget for one frame read outside a scan.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock the budget runs on: the system's, unless a test measures on its own.</summary>
    /// <remarks>
    /// A system timer fires on a thread-pool thread, and a test that stalls pixel work for longer
    /// than the budget and expects the deadline to have fired meanwhile was measuring the pool's
    /// latency under a parallel test run, not the deadline. It failed on both CI hosts at once.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>The frame deadline a recognizer reads under: the scan's, or one it started itself.</summary>
internal sealed class OcrPipelineDeadline : IDisposable
{
    public const string DiagnosticCode = ScanFrameDeadline.DiagnosticCode;

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);
    private readonly ScanFrameDeadline _frame;
    private readonly bool _owned;

    private OcrPipelineDeadline(ScanFrameDeadline frame, bool owned)
    {
        _frame = frame;
        _owned = owned;
    }

    public CancellationToken Token => _frame.Token;

    /// <summary>True when the deadline, rather than the caller, ended the work.</summary>
    public bool IsExpired => _frame.IsExpired;

    /// <summary>False when this joined a deadline somebody else started and will end.</summary>
    public bool IsOwned => _owned;

    /// <summary>
    /// True once the deadline has run out, for work that checks between bounded steps and keeps
    /// what it finished. Throws when the caller cancelled instead.
    /// </summary>
    public bool HasRunOut()
    {
        if (IsExpired)
        {
            return true;
        }

        Token.ThrowIfCancellationRequested();
        return false;
    }

    public static OcrPipelineOptions Validate(OcrPipelineOptions? options)
    {
        options ??= new OcrPipelineOptions();
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The OCR pipeline deadline must be positive and bounded.");
        }

        return options;
    }

    /// <summary>
    /// Joins the frame deadline the token belongs to, or starts one linked to it when it belongs
    /// to none. Never a second budget inside the first.
    /// </summary>
    public static OcrPipelineDeadline Start(OcrPipelineOptions options, CancellationToken cancellationToken) =>
        ScanFrameDeadline.Joining(cancellationToken) is { } frame
            ? new(frame, owned: false)
            : new(ScanFrameDeadline.Start(options.Timeout, cancellationToken, options.TimeProvider), owned: true);

    /// <summary>
    /// True when the token is a frame deadline's and that deadline, rather than a caller, ended
    /// the work. False for any other token, whose cancellation is the caller's.
    /// </summary>
    public static bool HasExpired(CancellationToken cancellationToken) =>
        ScanFrameDeadline.Joining(cancellationToken)?.IsExpired == true;

    public void Dispose()
    {
        if (_owned)
        {
            _frame.Dispose();
        }
    }
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

    /// <summary>The code a degraded read carries forward, or null when nothing degraded it.</summary>
    public static string? Degradation(OcrResult result) =>
        IsDegraded(result) ? result.DiagnosticCode ?? "ocr_provider_unavailable" : null;
}
